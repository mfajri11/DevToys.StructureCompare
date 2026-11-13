using System.Text;
using DevToys.StructuredCompare.Models;

namespace DevToys.StructuredCompare.Services;

internal static class DiffTreeAnalyzer
{
    private const int MaxLcsMatrixCells = 100_000;

    private static readonly string[] IdentifierKeyNames =
    [
        "name",
        "id",
        "key",
        "env",
        "host",
        "type",
        "kind"
    ];

    public static (IReadOnlyList<DiffHighlightSpan> OriginalHighlights, IReadOnlyList<DiffHighlightSpan> ModifiedHighlights, DiffSummary Summary)
        AnalyzeDocuments(
            IReadOnlyList<DiffNode> originalDocuments,
            IReadOnlyList<DiffNode> modifiedDocuments,
            OrderSensitivity orderSensitivity)
    {
        var originalHighlights = new List<DiffHighlightSpan>();
        var modifiedHighlights = new List<DiffHighlightSpan>();
        var counts = new MutableCounts();

        if (originalDocuments.Count == 0 && modifiedDocuments.Count == 0)
        {
            return ([], [], new DiffSummary(0, 0, 0, 0));
        }

        if (originalDocuments.Count == 0)
        {
            for (int i = 0; i < modifiedDocuments.Count; i++)
            {
                string prefix = modifiedDocuments.Count > 1 ? $"doc[{i}]" : string.Empty;
                EmitWholeTree(modifiedDocuments[i], prefix, DiffChangeKind.Added, modifiedHighlights, counts);
            }

            return (
                [],
                DeconflictSpans(modifiedHighlights),
                counts.ToSummary());
        }

        if (modifiedDocuments.Count == 0)
        {
            for (int i = 0; i < originalDocuments.Count; i++)
            {
                string prefix = originalDocuments.Count > 1 ? $"doc[{i}]" : string.Empty;
                EmitWholeTree(originalDocuments[i], prefix, DiffChangeKind.Removed, originalHighlights, counts);
            }

            return (
                DeconflictSpans(originalHighlights),
                [],
                counts.ToSummary());
        }

        int sharedCount = Math.Min(originalDocuments.Count, modifiedDocuments.Count);
        bool multiDoc = originalDocuments.Count > 1 || modifiedDocuments.Count > 1;

        for (int i = 0; i < sharedCount; i++)
        {
            string prefix = multiDoc ? $"doc[{i}]" : string.Empty;
            CompareNodes(
                originalDocuments[i],
                modifiedDocuments[i],
                prefix,
                orderSensitivity,
                originalHighlights,
                modifiedHighlights,
                counts,
                fallbackOriginalSpan: originalDocuments[i].Span,
                fallbackModifiedSpan: modifiedDocuments[i].Span);
        }

        for (int i = sharedCount; i < originalDocuments.Count; i++)
        {
            EmitWholeTree(originalDocuments[i], $"doc[{i}]", DiffChangeKind.Removed, originalHighlights, counts);
        }

        for (int i = sharedCount; i < modifiedDocuments.Count; i++)
        {
            EmitWholeTree(modifiedDocuments[i], $"doc[{i}]", DiffChangeKind.Added, modifiedHighlights, counts);
        }

        return (
            DeconflictSpans(originalHighlights),
            DeconflictSpans(modifiedHighlights),
            counts.ToSummary());
    }

    public static string FormatPreview(string? text, int maxLength = 60)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "<empty>";
        }

        var sb = new StringBuilder(Math.Min(text.Length, maxLength + 4));
        bool previousWasSpace = false;
        foreach (char c in text.Trim())
        {
            char normalized = c is '\r' or '\n' or '\t' ? ' ' : c;
            if (normalized == ' ')
            {
                if (previousWasSpace)
                {
                    continue;
                }

                previousWasSpace = true;
            }
            else
            {
                previousWasSpace = false;
            }

            sb.Append(normalized);
            if (sb.Length >= maxLength)
            {
                sb.Append("...");
                break;
            }
        }

        return sb.Length == 0 ? "<empty>" : sb.ToString();
    }

    private static void CompareNodes(
        DiffNode original,
        DiffNode modified,
        string path,
        OrderSensitivity orderSensitivity,
        List<DiffHighlightSpan> originalHighlights,
        List<DiffHighlightSpan> modifiedHighlights,
        MutableCounts counts,
        DiffSpan fallbackOriginalSpan,
        DiffSpan fallbackModifiedSpan)
    {
        if (string.Equals(original.CanonicalValue, modified.CanonicalValue, StringComparison.Ordinal))
        {
            return;
        }

        if (original.Kind == DiffNodeKind.Mapping && modified.Kind == DiffNodeKind.Mapping)
        {
            if (original.Entries.Count == 0 && modified.Entries.Count == 0)
            {
                EmitModifiedPair(original, modified, path, originalHighlights, modifiedHighlights, counts, fallbackOriginalSpan, fallbackModifiedSpan);
                return;
            }

            CompareMappings(original, modified, path, orderSensitivity, originalHighlights, modifiedHighlights, counts);
            return;
        }

        if (original.Kind == DiffNodeKind.Sequence && modified.Kind == DiffNodeKind.Sequence)
        {
            if (original.Items.Count == 0 && modified.Items.Count == 0)
            {
                EmitModifiedPair(original, modified, path, originalHighlights, modifiedHighlights, counts, fallbackOriginalSpan, fallbackModifiedSpan);
                return;
            }

            CompareSequences(original, modified, path, orderSensitivity, originalHighlights, modifiedHighlights, counts);
            return;
        }

        EmitModifiedPair(original, modified, path, originalHighlights, modifiedHighlights, counts, fallbackOriginalSpan, fallbackModifiedSpan);
    }

    private static void CompareMappings(
        DiffNode original,
        DiffNode modified,
        string path,
        OrderSensitivity orderSensitivity,
        List<DiffHighlightSpan> originalHighlights,
        List<DiffHighlightSpan> modifiedHighlights,
        MutableCounts counts)
    {
        var origIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < original.Entries.Count; i++)
        {
            origIndexByKey[original.Entries[i].CanonicalKey] = i;
        }

        var modIndexByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int j = 0; j < modified.Entries.Count; j++)
        {
            modIndexByKey[modified.Entries[j].CanonicalKey] = j;
        }

        if (orderSensitivity == OrderSensitivity.StrictOrder)
        {
            var origCommon = new List<(string Key, int EntryIndex)>();
            for (int i = 0; i < original.Entries.Count; i++)
            {
                string key = original.Entries[i].CanonicalKey;
                if (modIndexByKey.ContainsKey(key))
                {
                    origCommon.Add((key, i));
                }
            }

            var modCommon = new List<(string Key, int EntryIndex)>();
            for (int j = 0; j < modified.Entries.Count; j++)
            {
                string key = modified.Entries[j].CanonicalKey;
                if (origIndexByKey.ContainsKey(key))
                {
                    modCommon.Add((key, j));
                }
            }

            List<(int LeftIndex, int RightIndex)> lcs = ComputeLcsPairs(
                origCommon.Select(static x => x.Key).ToList(),
                modCommon.Select(static x => x.Key).ToList());

            var origInLcs = new HashSet<int>();
            var modInLcs = new HashSet<int>();
            foreach ((int leftIdx, int rightIdx) in lcs)
            {
                origInLcs.Add(origCommon[leftIdx].EntryIndex);
                modInLcs.Add(modCommon[rightIdx].EntryIndex);
            }

            for (int i = 0; i < original.Entries.Count; i++)
            {
                DiffMappingEntry origEntry = original.Entries[i];
                if (modIndexByKey.TryGetValue(origEntry.CanonicalKey, out int j) && !origInLcs.Contains(i))
                {
                    DiffMappingEntry modEntry = modified.Entries[j];
                    string childPath = CombinePath(path, origEntry.DisplayKey);
                    string tooltip = $"Order changed for '{childPath}': Original #{i + 1} vs Modified #{j + 1} (ignore order to treat as equal).";
                    DiffSpan origSpan = FirstValidSpan(origEntry.KeySpan, origEntry.EntrySpan);
                    DiffSpan modSpan = FirstValidSpan(modEntry.KeySpan, modEntry.EntrySpan);
                    if (origSpan.IsValid && modSpan.IsValid)
                    {
                        originalHighlights.Add(new DiffHighlightSpan(origSpan.Start, origSpan.Length, DiffChangeKind.Reordered, childPath, tooltip));
                        modifiedHighlights.Add(new DiffHighlightSpan(modSpan.Start, modSpan.Length, DiffChangeKind.Reordered, childPath, tooltip));
                        counts.Reordered++;
                    }
                }
            }
        }

        for (int i = 0; i < original.Entries.Count; i++)
        {
            DiffMappingEntry origEntry = original.Entries[i];
            string childPath = CombinePath(path, origEntry.DisplayKey);

            if (!modIndexByKey.TryGetValue(origEntry.CanonicalKey, out int j))
            {
                DiffSpan span = FirstValidSpan(origEntry.EntrySpan, origEntry.KeySpan);
                if (span.IsValid)
                {
                    string tooltip = $"Removed '{childPath}' (only in Original)\nValue: {origEntry.Value.DisplayPreview}";
                    originalHighlights.Add(new DiffHighlightSpan(span.Start, span.Length, DiffChangeKind.Removed, childPath, tooltip));
                    counts.Removed++;
                }

                continue;
            }

            DiffMappingEntry modEntry = modified.Entries[j];
            CompareNodes(
                origEntry.Value,
                modEntry.Value,
                childPath,
                orderSensitivity,
                originalHighlights,
                modifiedHighlights,
                counts,
                fallbackOriginalSpan: FirstValidSpan(origEntry.KeySpan, origEntry.EntrySpan),
                fallbackModifiedSpan: FirstValidSpan(modEntry.KeySpan, modEntry.EntrySpan));
        }

        for (int j = 0; j < modified.Entries.Count; j++)
        {
            DiffMappingEntry modEntry = modified.Entries[j];
            if (origIndexByKey.ContainsKey(modEntry.CanonicalKey))
            {
                continue;
            }

            string childPath = CombinePath(path, modEntry.DisplayKey);
            DiffSpan span = FirstValidSpan(modEntry.EntrySpan, modEntry.KeySpan);
            if (span.IsValid)
            {
                string tooltip = $"Added '{childPath}' (only in Modified)\nValue: {modEntry.Value.DisplayPreview}";
                modifiedHighlights.Add(new DiffHighlightSpan(span.Start, span.Length, DiffChangeKind.Added, childPath, tooltip));
                counts.Added++;
            }
        }
    }

    private static void CompareSequences(
        DiffNode original,
        DiffNode modified,
        string path,
        OrderSensitivity orderSensitivity,
        List<DiffHighlightSpan> originalHighlights,
        List<DiffHighlightSpan> modifiedHighlights,
        MutableCounts counts)
    {
        if (orderSensitivity == OrderSensitivity.IgnoreOrder)
        {
            CompareSequencesIgnoreOrder(original.Items, modified.Items, path, orderSensitivity, originalHighlights, modifiedHighlights, counts);
        }
        else
        {
            CompareSequencesStrictOrder(original.Items, modified.Items, path, orderSensitivity, originalHighlights, modifiedHighlights, counts);
        }
    }

    private static void CompareSequencesIgnoreOrder(
        IReadOnlyList<DiffNode> origItems,
        IReadOnlyList<DiffNode> modItems,
        string path,
        OrderSensitivity orderSensitivity,
        List<DiffHighlightSpan> originalHighlights,
        List<DiffHighlightSpan> modifiedHighlights,
        MutableCounts counts)
    {
        var modQueues = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (int j = 0; j < modItems.Count; j++)
        {
            string canonical = modItems[j].CanonicalValue;
            if (!modQueues.TryGetValue(canonical, out Queue<int>? queue))
            {
                queue = new Queue<int>();
                modQueues[canonical] = queue;
            }

            queue.Enqueue(j);
        }

        var origPaired = new bool[origItems.Count];
        var modPaired = new bool[modItems.Count];

        // Pass 1: exact canonical matches regardless of position
        for (int i = 0; i < origItems.Count; i++)
        {
            if (modQueues.TryGetValue(origItems[i].CanonicalValue, out Queue<int>? queue) && queue.Count > 0)
            {
                int j = queue.Dequeue();
                origPaired[i] = true;
                modPaired[j] = true;
            }
        }

        // Pass 2: pair remaining mappings by identifier/key similarity
        PairMappingsBySimilarity(
            origItems,
            modItems,
            origPaired,
            modPaired,
            minScore: 1,
            markReorderedIfMoved: false,
            path,
            orderSensitivity,
            originalHighlights,
            modifiedHighlights,
            counts);

        // Pass 3: collect remaining unmatched items
        var remOrig = CollectUnpairedIndices(origPaired);
        var remMod = CollectUnpairedIndices(modPaired);

        if (origItems.Count == modItems.Count
            && remOrig.Count == remMod.Count
            && remOrig.Count > 0
            && remOrig.All(i => origItems[i].Kind == DiffNodeKind.Scalar)
            && remMod.All(j => modItems[j].Kind == DiffNodeKind.Scalar))
        {
            for (int k = 0; k < remOrig.Count; k++)
            {
                int i = remOrig[k];
                int j = remMod[k];
                string itemPath = $"{DisplayPath(path)}[{i}]";
                CompareNodes(
                    origItems[i],
                    modItems[j],
                    itemPath,
                    orderSensitivity,
                    originalHighlights,
                    modifiedHighlights,
                    counts,
                    origItems[i].Span,
                    modItems[j].Span);
            }

            return;
        }

        foreach (int i in remOrig)
        {
            string itemPath = $"{DisplayPath(path)}[{i}]";
            EmitItemDiff(origItems[i], itemPath, DiffChangeKind.Removed, originalHighlights, counts);
        }

        foreach (int j in remMod)
        {
            string itemPath = $"{DisplayPath(path)}[{j}]";
            EmitItemDiff(modItems[j], itemPath, DiffChangeKind.Added, modifiedHighlights, counts);
        }
    }

    private static void CompareSequencesStrictOrder(
        IReadOnlyList<DiffNode> origItems,
        IReadOnlyList<DiffNode> modItems,
        string path,
        OrderSensitivity orderSensitivity,
        List<DiffHighlightSpan> originalHighlights,
        List<DiffHighlightSpan> modifiedHighlights,
        MutableCounts counts)
    {
        List<(int LeftIndex, int RightIndex)> lcs = ComputeLcsPairs(
            origItems.Select(static x => x.CanonicalValue).ToList(),
            modItems.Select(static x => x.CanonicalValue).ToList());

        var origPaired = new bool[origItems.Count];
        var modPaired = new bool[modItems.Count];

        foreach ((int i, int j) in lcs)
        {
            origPaired[i] = true;
            modPaired[j] = true;
        }

        // Pass 2: exact items that exist in both sequences at reordered positions
        var modUnpairedByCanonical = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (int j = 0; j < modItems.Count; j++)
        {
            if (modPaired[j])
            {
                continue;
            }

            string canonical = modItems[j].CanonicalValue;
            if (!modUnpairedByCanonical.TryGetValue(canonical, out Queue<int>? queue))
            {
                queue = new Queue<int>();
                modUnpairedByCanonical[canonical] = queue;
            }

            queue.Enqueue(j);
        }

        for (int i = 0; i < origItems.Count; i++)
        {
            if (origPaired[i])
            {
                continue;
            }

            if (modUnpairedByCanonical.TryGetValue(origItems[i].CanonicalValue, out Queue<int>? queue) && queue.Count > 0)
            {
                int j = queue.Dequeue();
                origPaired[i] = true;
                modPaired[j] = true;

                string itemPath = $"{DisplayPath(path)}[{i}]";
                string tooltip = $"Order changed at '{itemPath}': Original [{i}] vs Modified [{j}] (ignore order to treat as equal).";
                if (origItems[i].Span.IsValid && modItems[j].Span.IsValid)
                {
                    originalHighlights.Add(new DiffHighlightSpan(origItems[i].Span.Start, origItems[i].Span.Length, DiffChangeKind.Reordered, itemPath, tooltip));
                    modifiedHighlights.Add(new DiffHighlightSpan(modItems[j].Span.Start, modItems[j].Span.Length, DiffChangeKind.Reordered, itemPath, tooltip));
                    counts.Reordered++;
                }
            }
        }

        // Pass 3: cross-gap pairing for identifier-matched mappings (e.g. - name: APP_ENV moved and changed value)
        PairMappingsBySimilarity(
            origItems,
            modItems,
            origPaired,
            modPaired,
            minScore: 1000,
            markReorderedIfMoved: true,
            path,
            orderSensitivity,
            originalHighlights,
            modifiedHighlights,
            counts);

        // Pass 4: process remaining unpaired items within each LCS anchor gap
        var anchors = new List<(int LeftIndex, int RightIndex)>(lcs.Count + 2) { (-1, -1) };
        anchors.AddRange(lcs);
        anchors.Add((origItems.Count, modItems.Count));

        for (int a = 0; a < anchors.Count - 1; a++)
        {
            int origStart = anchors[a].LeftIndex + 1;
            int origEnd = anchors[a + 1].LeftIndex;
            int modStart = anchors[a].RightIndex + 1;
            int modEnd = anchors[a + 1].RightIndex;

            var gapOrig = new List<int>();
            for (int i = origStart; i < origEnd; i++)
            {
                if (!origPaired[i])
                {
                    gapOrig.Add(i);
                }
            }

            var gapMod = new List<int>();
            for (int j = modStart; j < modEnd; j++)
            {
                if (!modPaired[j])
                {
                    gapMod.Add(j);
                }
            }

            if (gapOrig.Count == 0 && gapMod.Count == 0)
            {
                continue;
            }

            // Pair mappings inside the gap by similarity
            PairGapMappings(
                origItems,
                modItems,
                gapOrig,
                gapMod,
                origPaired,
                modPaired,
                path,
                orderSensitivity,
                originalHighlights,
                modifiedHighlights,
                counts);

            gapOrig.RemoveAll(i => origPaired[i]);
            gapMod.RemoveAll(j => modPaired[j]);

            int pairCount = Math.Min(gapOrig.Count, gapMod.Count);
            for (int k = 0; k < pairCount; k++)
            {
                int i = gapOrig[k];
                int j = gapMod[k];
                origPaired[i] = true;
                modPaired[j] = true;

                string itemPath = $"{DisplayPath(path)}[{i}]";
                CompareNodes(
                    origItems[i],
                    modItems[j],
                    itemPath,
                    orderSensitivity,
                    originalHighlights,
                    modifiedHighlights,
                    counts,
                    origItems[i].Span,
                    modItems[j].Span);
            }

            for (int k = pairCount; k < gapOrig.Count; k++)
            {
                int i = gapOrig[k];
                origPaired[i] = true;
                EmitItemDiff(origItems[i], $"{DisplayPath(path)}[{i}]", DiffChangeKind.Removed, originalHighlights, counts);
            }

            for (int k = pairCount; k < gapMod.Count; k++)
            {
                int j = gapMod[k];
                modPaired[j] = true;
                EmitItemDiff(modItems[j], $"{DisplayPath(path)}[{j}]", DiffChangeKind.Added, modifiedHighlights, counts);
            }
        }
    }

    private static void PairMappingsBySimilarity(
        IReadOnlyList<DiffNode> origItems,
        IReadOnlyList<DiffNode> modItems,
        bool[] origPaired,
        bool[] modPaired,
        int minScore,
        bool markReorderedIfMoved,
        string path,
        OrderSensitivity orderSensitivity,
        List<DiffHighlightSpan> originalHighlights,
        List<DiffHighlightSpan> modifiedHighlights,
        MutableCounts counts)
    {
        var candidates = new List<(int Score, int OrigIndex, int ModIndex)>();

        for (int i = 0; i < origItems.Count; i++)
        {
            if (origPaired[i] || origItems[i].Kind != DiffNodeKind.Mapping)
            {
                continue;
            }

            for (int j = 0; j < modItems.Count; j++)
            {
                if (modPaired[j] || modItems[j].Kind != DiffNodeKind.Mapping)
                {
                    continue;
                }

                int score = ScoreMappingSimilarity(origItems[i], modItems[j]);
                if (score >= minScore)
                {
                    candidates.Add((score, i, j));
                }
            }
        }

        candidates.Sort(static (a, b) =>
        {
            int cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = a.OrigIndex.CompareTo(b.OrigIndex);
            return cmp != 0 ? cmp : a.ModIndex.CompareTo(b.ModIndex);
        });

        foreach ((_, int i, int j) in candidates)
        {
            if (origPaired[i] || modPaired[j])
            {
                continue;
            }

            origPaired[i] = true;
            modPaired[j] = true;

            string itemPath = BuildMappingItemPath(path, origItems[i], i);

            if (markReorderedIfMoved && i != j)
            {
                DiffMappingEntry? idOrig = FindMatchedIdentifierEntry(origItems[i], modItems[j]);
                if (idOrig is not null)
                {
                    DiffMappingEntry idMod = modItems[j].Entries.First(e => string.Equals(e.CanonicalKey, idOrig.CanonicalKey, StringComparison.Ordinal));
                    DiffSpan origIdSpan = FirstValidSpan(idOrig.KeySpan, origItems[i].Span);
                    DiffSpan modIdSpan = FirstValidSpan(idMod.KeySpan, modItems[j].Span);
                    if (origIdSpan.IsValid && modIdSpan.IsValid)
                    {
                        string tooltip = $"Order changed at '{itemPath}': Original [{i}] vs Modified [{j}].";
                        originalHighlights.Add(new DiffHighlightSpan(origIdSpan.Start, origIdSpan.Length, DiffChangeKind.Reordered, itemPath, tooltip));
                        modifiedHighlights.Add(new DiffHighlightSpan(modIdSpan.Start, modIdSpan.Length, DiffChangeKind.Reordered, itemPath, tooltip));
                        counts.Reordered++;
                    }
                }
            }

            CompareNodes(
                origItems[i],
                modItems[j],
                itemPath,
                orderSensitivity,
                originalHighlights,
                modifiedHighlights,
                counts,
                origItems[i].Span,
                modItems[j].Span);
        }
    }

    private static void PairGapMappings(
        IReadOnlyList<DiffNode> origItems,
        IReadOnlyList<DiffNode> modItems,
        IReadOnlyList<int> gapOrig,
        IReadOnlyList<int> gapMod,
        bool[] origPaired,
        bool[] modPaired,
        string path,
        OrderSensitivity orderSensitivity,
        List<DiffHighlightSpan> originalHighlights,
        List<DiffHighlightSpan> modifiedHighlights,
        MutableCounts counts)
    {
        var candidates = new List<(int Score, int OrigIndex, int ModIndex)>();
        foreach (int i in gapOrig)
        {
            if (origPaired[i] || origItems[i].Kind != DiffNodeKind.Mapping)
            {
                continue;
            }

            foreach (int j in gapMod)
            {
                if (modPaired[j] || modItems[j].Kind != DiffNodeKind.Mapping)
                {
                    continue;
                }

                int score = ScoreMappingSimilarity(origItems[i], modItems[j]);
                if (score > 0)
                {
                    candidates.Add((score, i, j));
                }
            }
        }

        candidates.Sort(static (a, b) =>
        {
            int cmp = b.Score.CompareTo(a.Score);
            if (cmp != 0)
            {
                return cmp;
            }

            cmp = a.OrigIndex.CompareTo(b.OrigIndex);
            return cmp != 0 ? cmp : a.ModIndex.CompareTo(b.ModIndex);
        });

        foreach ((_, int i, int j) in candidates)
        {
            if (origPaired[i] || modPaired[j])
            {
                continue;
            }

            origPaired[i] = true;
            modPaired[j] = true;

            string itemPath = BuildMappingItemPath(path, origItems[i], i);
            CompareNodes(
                origItems[i],
                modItems[j],
                itemPath,
                orderSensitivity,
                originalHighlights,
                modifiedHighlights,
                counts,
                origItems[i].Span,
                modItems[j].Span);
        }
    }

    private static int ScoreMappingSimilarity(DiffNode left, DiffNode right)
    {
        var rightMap = new Dictionary<string, DiffMappingEntry>(StringComparer.Ordinal);
        foreach (DiffMappingEntry entry in right.Entries)
        {
            rightMap[entry.CanonicalKey] = entry;
        }

        int score = 0;
        bool hasMatchingValue = false;

        foreach (DiffMappingEntry leftEntry in left.Entries)
        {
            if (!rightMap.TryGetValue(leftEntry.CanonicalKey, out DiffMappingEntry? rightEntry))
            {
                continue;
            }

            bool equalValue = string.Equals(leftEntry.Value.CanonicalValue, rightEntry.Value.CanonicalValue, StringComparison.Ordinal);

            if (IsIdentifierKey(leftEntry.DisplayKey)
                && leftEntry.Value.Kind == DiffNodeKind.Scalar
                && rightEntry.Value.Kind == DiffNodeKind.Scalar)
            {
                if (!equalValue)
                {
                    return 0;
                }

                score += 1000;
                hasMatchingValue = true;
            }
            else if (equalValue)
            {
                score += 10;
                hasMatchingValue = true;
            }
            else
            {
                score += 2;
            }
        }

        return hasMatchingValue ? score : 0;
    }

    private static DiffMappingEntry? FindMatchedIdentifierEntry(DiffNode left, DiffNode right)
    {
        var rightMap = new Dictionary<string, DiffMappingEntry>(StringComparer.Ordinal);
        foreach (DiffMappingEntry entry in right.Entries)
        {
            rightMap[entry.CanonicalKey] = entry;
        }

        foreach (DiffMappingEntry leftEntry in left.Entries)
        {
            if (IsIdentifierKey(leftEntry.DisplayKey)
                && leftEntry.Value.Kind == DiffNodeKind.Scalar
                && rightMap.TryGetValue(leftEntry.CanonicalKey, out DiffMappingEntry? rightEntry)
                && string.Equals(leftEntry.Value.CanonicalValue, rightEntry.Value.CanonicalValue, StringComparison.Ordinal))
            {
                return leftEntry;
            }
        }

        return null;
    }

    private static bool IsIdentifierKey(string key)
    {
        foreach (string idName in IdentifierKeyNames)
        {
            if (string.Equals(key, idName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildMappingItemPath(string parentPath, DiffNode mapping, int index)
    {
        foreach (string idName in IdentifierKeyNames)
        {
            foreach (DiffMappingEntry entry in mapping.Entries)
            {
                if (string.Equals(entry.DisplayKey, idName, StringComparison.OrdinalIgnoreCase)
                    && entry.Value.Kind == DiffNodeKind.Scalar)
                {
                    return $"{DisplayPath(parentPath)}[{entry.DisplayKey}={entry.Value.DisplayPreview}]";
                }
            }
        }

        return $"{DisplayPath(parentPath)}[{index}]";
    }

    private static void EmitWholeTree(
        DiffNode node,
        string path,
        DiffChangeKind kind,
        List<DiffHighlightSpan> target,
        MutableCounts counts)
    {
        if (node.Kind == DiffNodeKind.Mapping && node.Entries.Count > 0)
        {
            foreach (DiffMappingEntry entry in node.Entries)
            {
                string childPath = CombinePath(path, entry.DisplayKey);
                DiffSpan span = FirstValidSpan(entry.EntrySpan, entry.KeySpan);
                if (!span.IsValid)
                {
                    continue;
                }

                string verb = kind == DiffChangeKind.Added ? "Added" : "Removed";
                string side = kind == DiffChangeKind.Added ? "Modified" : "Original";
                target.Add(new DiffHighlightSpan(
                    span.Start,
                    span.Length,
                    kind,
                    childPath,
                    $"{verb} '{childPath}' (only in {side})\nValue: {entry.Value.DisplayPreview}"));
                IncrementCount(counts, kind);
            }

            return;
        }

        if (node.Kind == DiffNodeKind.Sequence && node.Items.Count > 0)
        {
            for (int i = 0; i < node.Items.Count; i++)
            {
                EmitItemDiff(node.Items[i], $"{DisplayPath(path)}[{i}]", kind, target, counts);
            }

            return;
        }

        if (node.Span.IsValid)
        {
            string displayPath = DisplayPath(path);
            string verb = kind == DiffChangeKind.Added ? "Added" : "Removed";
            string side = kind == DiffChangeKind.Added ? "Modified" : "Original";
            target.Add(new DiffHighlightSpan(
                node.Span.Start,
                node.Span.Length,
                kind,
                displayPath,
                $"{verb} '{displayPath}' (only in {side})\nValue: {node.DisplayPreview}"));
            IncrementCount(counts, kind);
        }
    }

    private static void EmitItemDiff(
        DiffNode item,
        string itemPath,
        DiffChangeKind kind,
        List<DiffHighlightSpan> target,
        MutableCounts counts)
    {
        if (!item.Span.IsValid)
        {
            return;
        }

        string verb = kind == DiffChangeKind.Added ? "Added" : "Removed";
        string side = kind == DiffChangeKind.Added ? "Modified" : "Original";
        target.Add(new DiffHighlightSpan(
            item.Span.Start,
            item.Span.Length,
            kind,
            itemPath,
            $"{verb} item at '{itemPath}' (only in {side})\nValue: {item.DisplayPreview}"));
        IncrementCount(counts, kind);
    }

    private static void EmitModifiedPair(
        DiffNode original,
        DiffNode modified,
        string path,
        List<DiffHighlightSpan> originalHighlights,
        List<DiffHighlightSpan> modifiedHighlights,
        MutableCounts counts,
        DiffSpan fallbackOriginalSpan,
        DiffSpan fallbackModifiedSpan)
    {
        DiffSpan origSpan = FirstValidSpan(original.Span, fallbackOriginalSpan);
        DiffSpan modSpan = FirstValidSpan(modified.Span, fallbackModifiedSpan);
        string displayPath = DisplayPath(path);
        string tooltip = $"Modified at '{displayPath}'\nOriginal: {original.DisplayPreview}\nModified: {modified.DisplayPreview}";

        if (origSpan.IsValid)
        {
            originalHighlights.Add(new DiffHighlightSpan(origSpan.Start, origSpan.Length, DiffChangeKind.Modified, displayPath, tooltip));
        }

        if (modSpan.IsValid)
        {
            modifiedHighlights.Add(new DiffHighlightSpan(modSpan.Start, modSpan.Length, DiffChangeKind.Modified, displayPath, tooltip));
        }

        if (origSpan.IsValid || modSpan.IsValid)
        {
            counts.Modified++;
        }
    }

    internal static List<(int LeftIndex, int RightIndex)> ComputeLcsPairs(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        int n = left.Count;
        int m = right.Count;

        if (n == 0 || m == 0)
        {
            return [];
        }

        if ((long)n * m > MaxLcsMatrixCells)
        {
            return ComputeGreedySubsequencePairs(left, right);
        }

        var dp = new int[n + 1, m + 1];
        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var pairs = new List<(int LeftIndex, int RightIndex)>(dp[0, 0]);
        int x = 0;
        int y = 0;
        while (x < n && y < m)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal))
            {
                pairs.Add((x, y));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                x++;
            }
            else
            {
                y++;
            }
        }

        return pairs;
    }

    private static List<(int LeftIndex, int RightIndex)> ComputeGreedySubsequencePairs(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
    {
        var rightPositions = new Dictionary<string, Queue<int>>(StringComparer.Ordinal);
        for (int j = 0; j < right.Count; j++)
        {
            if (!rightPositions.TryGetValue(right[j], out Queue<int>? queue))
            {
                queue = new Queue<int>();
                rightPositions[right[j]] = queue;
            }

            queue.Enqueue(j);
        }

        var pairs = new List<(int LeftIndex, int RightIndex)>();
        int minRight = 0;
        for (int i = 0; i < left.Count; i++)
        {
            if (!rightPositions.TryGetValue(left[i], out Queue<int>? queue))
            {
                continue;
            }

            while (queue.Count > 0 && queue.Peek() < minRight)
            {
                queue.Dequeue();
            }

            if (queue.Count > 0)
            {
                int j = queue.Dequeue();
                pairs.Add((i, j));
                minRight = j + 1;
            }
        }

        return pairs;
    }

    private static List<int> CollectUnpairedIndices(bool[] paired)
    {
        var result = new List<int>();
        for (int i = 0; i < paired.Length; i++)
        {
            if (!paired[i])
            {
                result.Add(i);
            }
        }

        return result;
    }

    private static IReadOnlyList<DiffHighlightSpan> DeconflictSpans(List<DiffHighlightSpan> spans)
    {
        if (spans.Count == 0)
        {
            return [];
        }

        spans.Sort(static (a, b) =>
        {
            int priorityCmp = GetKindPriority(a.Kind).CompareTo(GetKindPriority(b.Kind));
            if (priorityCmp != 0)
            {
                return priorityCmp;
            }

            int startCmp = a.Start.CompareTo(b.Start);
            return startCmp != 0 ? startCmp : a.Length.CompareTo(b.Length);
        });

        var accepted = new List<DiffHighlightSpan>(spans.Count);
        foreach (DiffHighlightSpan candidate in spans)
        {
            if (candidate.Start < 0 || candidate.Length <= 0)
            {
                continue;
            }

            int candidateEnd = candidate.Start + candidate.Length;
            bool overlaps = false;
            foreach (DiffHighlightSpan existing in accepted)
            {
                int existingEnd = existing.Start + existing.Length;
                if (candidate.Start < existingEnd && existing.Start < candidateEnd)
                {
                    overlaps = true;
                    break;
                }
            }

            if (!overlaps)
            {
                accepted.Add(candidate);
            }
        }

        accepted.Sort(static (a, b) => a.Start.CompareTo(b.Start));
        return accepted;
    }

    private static int GetKindPriority(DiffChangeKind kind) => kind switch
    {
        DiffChangeKind.Modified => 0,
        DiffChangeKind.Removed => 1,
        DiffChangeKind.Added => 2,
        DiffChangeKind.Reordered => 3,
        _ => 4
    };

    private static DiffSpan FirstValidSpan(DiffSpan primary, DiffSpan fallback)
        => primary.IsValid ? primary : fallback;

    private static string CombinePath(string parent, string key)
        => string.IsNullOrEmpty(parent) ? key : $"{parent}.{key}";

    private static string DisplayPath(string path)
        => string.IsNullOrEmpty(path) ? "<root>" : path;

    private static void IncrementCount(MutableCounts counts, DiffChangeKind kind)
    {
        switch (kind)
        {
            case DiffChangeKind.Added:
                counts.Added++;
                break;
            case DiffChangeKind.Removed:
                counts.Removed++;
                break;
            case DiffChangeKind.Modified:
                counts.Modified++;
                break;
            case DiffChangeKind.Reordered:
                counts.Reordered++;
                break;
        }
    }

    private sealed class MutableCounts
    {
        public int Added;
        public int Removed;
        public int Modified;
        public int Reordered;

        public DiffSummary ToSummary() => new(Added, Removed, Modified, Reordered);
    }
}

using System.Text;
using DevToys.StructuredCompare.Models;

namespace DevToys.StructuredCompare.Services;

public static class InlineDiffFormatter
{
    public static (string Text, IReadOnlyList<DiffHighlightSpan> Highlights) Build(ComparisonResult result)
    {
        if (result.Status == ComparisonStatus.Empty || result.Status == ComparisonStatus.InvalidInput)
        {
            return (string.Empty, []);
        }

        if (result.Status == ComparisonStatus.Equivalent)
        {
            return (result.NormalizedOriginal, []);
        }

        IReadOnlyList<LineSlice> origLines = SplitLinesWithOffsets(result.NormalizedOriginal);
        IReadOnlyList<LineSlice> modLines = SplitLinesWithOffsets(result.NormalizedModified);

        if (origLines.Count == 0 && modLines.Count == 0)
        {
            return (string.Empty, []);
        }

        List<string> origTexts = origLines.Select(static l => l.Text).ToList();
        List<string> modTexts = modLines.Select(static l => l.Text).ToList();
        List<(int LeftIndex, int RightIndex)> lcs = DiffTreeAnalyzer.ComputeLcsPairs(origTexts, modTexts);

        var anchors = new List<(int LeftIndex, int RightIndex)>(lcs.Count + 2) { (-1, -1) };
        anchors.AddRange(lcs);
        anchors.Add((origLines.Count, modLines.Count));

        var sb = new StringBuilder();
        var highlights = new List<DiffHighlightSpan>();

        for (int a = 0; a < anchors.Count - 1; a++)
        {
            int origStart = anchors[a].LeftIndex + 1;
            int origEnd = anchors[a + 1].LeftIndex;
            int modStart = anchors[a].RightIndex + 1;
            int modEnd = anchors[a + 1].RightIndex;

            int origCount = origEnd - origStart;
            int modCount = modEnd - modStart;
            int pairedCount = Math.Min(origCount, modCount);

            // Emit paired changed lines adjacent (- then +) so modified conflicts read cleanly
            for (int k = 0; k < pairedCount; k++)
            {
                LineSlice origLine = origLines[origStart + k];
                LineSlice modLine = modLines[modStart + k];

                DiffHighlightSpan? origSemantic = FindOverlappingSpan(origLine, result.OriginalHighlights);
                DiffHighlightSpan? modSemantic = FindOverlappingSpan(modLine, result.ModifiedHighlights);

                DiffChangeKind origKind = ResolveLineKind(origSemantic, defaultKind: modSemantic?.Kind == DiffChangeKind.Modified
                    ? DiffChangeKind.Modified
                    : DiffChangeKind.Removed);
                DiffChangeKind modKind = ResolveLineKind(modSemantic, defaultKind: origSemantic?.Kind == DiffChangeKind.Modified
                    ? DiffChangeKind.Modified
                    : DiffChangeKind.Added);

                AppendDiffLine(
                    sb,
                    highlights,
                    origKind == DiffChangeKind.Reordered ? "⇄ " : "- ",
                    origLine.Text,
                    origKind,
                    origSemantic?.Path ?? string.Empty,
                    origSemantic?.Tooltip ?? $"Removed / Changed from Original: {DiffTreeAnalyzer.FormatPreview(origLine.Text)}");

                AppendDiffLine(
                    sb,
                    highlights,
                    modKind == DiffChangeKind.Reordered ? "⇄ " : "+ ",
                    modLine.Text,
                    modKind,
                    modSemantic?.Path ?? string.Empty,
                    modSemantic?.Tooltip ?? $"Added / Changed in Modified: {DiffTreeAnalyzer.FormatPreview(modLine.Text)}");
            }

            // Remaining unmatched Original lines in this gap
            for (int k = pairedCount; k < origCount; k++)
            {
                LineSlice origLine = origLines[origStart + k];
                DiffHighlightSpan? origSemantic = FindOverlappingSpan(origLine, result.OriginalHighlights);
                DiffChangeKind kind = ResolveLineKind(origSemantic, DiffChangeKind.Removed);

                AppendDiffLine(
                    sb,
                    highlights,
                    kind == DiffChangeKind.Reordered ? "⇄ " : "- ",
                    origLine.Text,
                    kind,
                    origSemantic?.Path ?? string.Empty,
                    origSemantic?.Tooltip ?? $"Removed from Original: {DiffTreeAnalyzer.FormatPreview(origLine.Text)}");
            }

            // Remaining unmatched Modified lines in this gap
            for (int k = pairedCount; k < modCount; k++)
            {
                LineSlice modLine = modLines[modStart + k];
                DiffHighlightSpan? modSemantic = FindOverlappingSpan(modLine, result.ModifiedHighlights);
                DiffChangeKind kind = ResolveLineKind(modSemantic, DiffChangeKind.Added);

                AppendDiffLine(
                    sb,
                    highlights,
                    kind == DiffChangeKind.Reordered ? "⇄ " : "+ ",
                    modLine.Text,
                    kind,
                    modSemantic?.Path ?? string.Empty,
                    modSemantic?.Tooltip ?? $"Added in Modified: {DiffTreeAnalyzer.FormatPreview(modLine.Text)}");
            }

            // Matched unchanged anchor line
            if (a + 1 < anchors.Count - 1)
            {
                LineSlice equalLine = origLines[anchors[a + 1].LeftIndex];
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }

                sb.Append("  ");
                sb.Append(equalLine.Text);
            }
        }

        return (sb.ToString(), highlights);
    }

    private static void AppendDiffLine(
        StringBuilder sb,
        List<DiffHighlightSpan> highlights,
        string prefix,
        string lineText,
        DiffChangeKind kind,
        string path,
        string tooltip)
    {
        if (sb.Length > 0)
        {
            sb.Append('\n');
        }

        int lineStart = sb.Length;
        sb.Append(prefix);
        sb.Append(lineText);
        int lineLength = sb.Length - lineStart;

        if (lineLength > 0)
        {
            highlights.Add(new DiffHighlightSpan(lineStart, lineLength, kind, path, tooltip));
        }
    }

    private static DiffChangeKind ResolveLineKind(DiffHighlightSpan? semantic, DiffChangeKind defaultKind)
    {
        return semantic?.Kind ?? defaultKind;
    }

    private static DiffHighlightSpan? FindOverlappingSpan(LineSlice line, IReadOnlyList<DiffHighlightSpan>? spans)
    {
        if (spans is null || spans.Count == 0)
        {
            return null;
        }

        int lineStart = line.Start;
        int lineEnd = Math.Max(line.Start + 1, line.End);

        DiffHighlightSpan? best = null;
        foreach (DiffHighlightSpan span in spans)
        {
            int spanEnd = span.Start + span.Length;
            if (span.Start < lineEnd && lineStart < spanEnd)
            {
                if (best is null || GetPriority(span.Kind) < GetPriority(best.Kind))
                {
                    best = span;
                }
            }
        }

        return best;
    }

    private static int GetPriority(DiffChangeKind kind) => kind switch
    {
        DiffChangeKind.Modified => 0,
        DiffChangeKind.Removed => 1,
        DiffChangeKind.Added => 2,
        DiffChangeKind.Reordered => 3,
        _ => 4
    };

    private static List<LineSlice> SplitLinesWithOffsets(string text)
    {
        var lines = new List<LineSlice>();
        if (string.IsNullOrEmpty(text))
        {
            return lines;
        }

        int lineStart = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                int end = i > lineStart && text[i - 1] == '\r' ? i - 1 : i;
                lines.Add(new LineSlice(text[lineStart..end], lineStart, end));
                lineStart = i + 1;
            }
        }

        if (lineStart <= text.Length)
        {
            lines.Add(new LineSlice(text[lineStart..], lineStart, text.Length));
        }

        return lines;
    }

    private readonly record struct LineSlice(string Text, int Start, int End);
}

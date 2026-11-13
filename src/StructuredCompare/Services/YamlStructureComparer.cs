using System.Globalization;
using System.Text.RegularExpressions;
using DevToys.StructuredCompare.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace DevToys.StructuredCompare.Services;

public sealed partial class YamlStructureComparer : IStructureComparer
{
    public ComparisonFormat Format => ComparisonFormat.Yaml;

    public ComparisonResult Compare(string? original, string? modified, OrderSensitivity orderSensitivity)
    {
        bool originalEmpty = string.IsNullOrWhiteSpace(original);
        bool modifiedEmpty = string.IsNullOrWhiteSpace(modified);

        if (originalEmpty && modifiedEmpty)
        {
            return new ComparisonResult(ComparisonStatus.Empty, ComparisonFormat.Yaml, string.Empty, string.Empty);
        }

        if (!TryNormalize(original, "Original", orderSensitivity, out string normalizedOriginal, out _, out string? originalError))
        {
            return new ComparisonResult(
                ComparisonStatus.InvalidInput,
                ComparisonFormat.Yaml,
                original ?? string.Empty,
                modified ?? string.Empty,
                originalError);
        }

        if (!TryNormalize(modified, "Modified", orderSensitivity, out string normalizedModified, out _, out string? modifiedError))
        {
            return new ComparisonResult(
                ComparisonStatus.InvalidInput,
                ComparisonFormat.Yaml,
                original ?? string.Empty,
                modified ?? string.Empty,
                modifiedError);
        }

        ComparisonStatus status = string.Equals(normalizedOriginal, normalizedModified, StringComparison.Ordinal)
            ? ComparisonStatus.Equivalent
            : ComparisonStatus.Different;

        if (status != ComparisonStatus.Different)
        {
            return new ComparisonResult(
                status,
                ComparisonFormat.Yaml,
                normalizedOriginal,
                normalizedModified);
        }

        List<DiffNode> origOutputDocs = TryNormalize(normalizedOriginal, "Original", orderSensitivity, out _, out List<DiffNode> parsedOrigDocs, out _)
            ? parsedOrigDocs
            : [];
        List<DiffNode> modOutputDocs = TryNormalize(normalizedModified, "Modified", orderSensitivity, out _, out List<DiffNode> parsedModDocs, out _)
            ? parsedModDocs
            : [];

        (IReadOnlyList<DiffHighlightSpan> origHighlights, IReadOnlyList<DiffHighlightSpan> modHighlights, DiffSummary summary) =
            DiffTreeAnalyzer.AnalyzeDocuments(origOutputDocs, modOutputDocs, orderSensitivity);

        return new ComparisonResult(
            status,
            ComparisonFormat.Yaml,
            normalizedOriginal,
            normalizedModified,
            OriginalHighlights: origHighlights,
            ModifiedHighlights: modHighlights,
            Summary: summary);
    }

    private static bool TryNormalize(
        string? input,
        string sideLabel,
        OrderSensitivity orderSensitivity,
        out string normalized,
        out List<DiffNode> diffDocs,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            normalized = string.Empty;
            diffDocs = [];
            error = null;
            return true;
        }

        try
        {
            var stream = new YamlStream();
            using var reader = new StringReader(input);
            stream.Load(reader);

            var canonicalDocuments = new List<YamlDocument>(stream.Documents.Count);
            diffDocs = new List<DiffNode>(stream.Documents.Count);

            foreach (YamlDocument document in stream.Documents)
            {
                if (document.RootNode is null)
                {
                    continue;
                }

                (YamlNode canonicalRoot, DiffNode diffRoot) = BuildCanonicalAndDiffNode(document.RootNode, input, orderSensitivity);
                canonicalDocuments.Add(new YamlDocument(canonicalRoot));
                diffDocs.Add(diffRoot);
            }

            if (canonicalDocuments.Count == 0)
            {
                normalized = string.Empty;
                diffDocs = [];
                error = null;
                return true;
            }

            normalized = SerializeDocuments(canonicalDocuments);
            error = null;
            return true;
        }
        catch (YamlException ex)
        {
            normalized = string.Empty;
            diffDocs = [];
            string details = ex.InnerException?.Message ?? ex.Message;
            error = $"{sideLabel} YAML is invalid (Line {ex.Start.Line}, Column {ex.Start.Column}): {details}";
            return false;
        }
        catch (Exception ex)
        {
            normalized = string.Empty;
            diffDocs = [];
            error = $"{sideLabel} YAML could not be parsed: {ex.Message}";
            return false;
        }
    }

    private static (YamlNode Canonical, DiffNode Diff) BuildCanonicalAndDiffNode(
        YamlNode node,
        string rawInput,
        OrderSensitivity orderSensitivity)
    {
        return node switch
        {
            YamlMappingNode mapping => BuildMapping(mapping, rawInput, orderSensitivity),
            YamlSequenceNode sequence => BuildSequence(sequence, rawInput, orderSensitivity),
            YamlScalarNode scalar => BuildScalar(scalar, rawInput),
            _ => (node, new DiffNode(
                DiffNodeKind.Scalar,
                DiffSpan.FromBounds(rawInput, (int)node.Start.Index, (int)node.End.Index),
                SerializeSingleNode(node),
                DiffTreeAnalyzer.FormatPreview(SerializeSingleNode(node)),
                [],
                []))
        };
    }

    private static (YamlMappingNode Canonical, DiffNode Diff) BuildMapping(
        YamlMappingNode mapping,
        string rawInput,
        OrderSensitivity orderSensitivity)
    {
        var canonicalEntries = new List<(YamlNode Key, YamlNode Value, string SortKey, string SortValue)>(mapping.Children.Count);
        var diffEntries = new List<DiffMappingEntry>(mapping.Children.Count);

        int minStart = int.MaxValue;
        int maxEnd = -1;

        foreach (KeyValuePair<YamlNode, YamlNode> entry in mapping.Children)
        {
            (YamlNode canonicalKey, DiffNode diffKey) = BuildCanonicalAndDiffNode(entry.Key, rawInput, orderSensitivity);
            (YamlNode canonicalValue, DiffNode diffValue) = BuildCanonicalAndDiffNode(entry.Value, rawInput, orderSensitivity);

            string canonicalKeyStr = SerializeSingleNode(canonicalKey);
            string sortValue = orderSensitivity == OrderSensitivity.IgnoreOrder
                ? SerializeSingleNode(canonicalValue)
                : string.Empty;

            canonicalEntries.Add((canonicalKey, canonicalValue, canonicalKeyStr, sortValue));

            string displayKey = entry.Key is YamlScalarNode keyScalar && !string.IsNullOrEmpty(keyScalar.Value)
                ? keyScalar.Value
                : DiffTreeAnalyzer.FormatPreview(canonicalKeyStr, 40);

            DiffSpan keySpan = diffKey.Span;
            DiffSpan entrySpan;
            if (keySpan.IsValid && diffValue.Span.IsValid)
            {
                entrySpan = DiffSpan.FromBounds(
                    rawInput,
                    Math.Min(keySpan.Start, diffValue.Span.Start),
                    Math.Max(keySpan.Start + keySpan.Length, diffValue.Span.Start + diffValue.Span.Length));
            }
            else
            {
                entrySpan = keySpan.IsValid ? keySpan : diffValue.Span;
            }

            if (entrySpan.IsValid)
            {
                minStart = Math.Min(minStart, entrySpan.Start);
                maxEnd = Math.Max(maxEnd, entrySpan.Start + entrySpan.Length);
            }

            diffEntries.Add(new DiffMappingEntry(
                canonicalKeyStr,
                displayKey,
                keySpan,
                entrySpan,
                diffValue));
        }

        if (orderSensitivity == OrderSensitivity.IgnoreOrder)
        {
            canonicalEntries.Sort((a, b) =>
            {
                int keyCompare = string.CompareOrdinal(a.SortKey, b.SortKey);
                return keyCompare != 0 ? keyCompare : string.CompareOrdinal(a.SortValue, b.SortValue);
            });
        }

        var result = new YamlMappingNode();
        if (!mapping.Tag.IsEmpty)
        {
            result.Tag = mapping.Tag;
        }

        foreach ((YamlNode key, YamlNode value, _, _) in canonicalEntries)
        {
            result.Add(key, value);
        }

        DiffSpan containerSpan = maxEnd > minStart
            ? DiffSpan.FromBounds(rawInput, minStart, maxEnd)
            : FindEmptyContainerSpan(rawInput, (int)mapping.Start.Index, '{', '}');

        string canonicalStr = SerializeSingleNode(result);
        var diffNode = new DiffNode(
            DiffNodeKind.Mapping,
            containerSpan,
            canonicalStr,
            DiffTreeAnalyzer.FormatPreview(canonicalStr),
            diffEntries,
            []);

        return (result, diffNode);
    }

    private static (YamlSequenceNode Canonical, DiffNode Diff) BuildSequence(
        YamlSequenceNode sequence,
        string rawInput,
        OrderSensitivity orderSensitivity)
    {
        var canonicalItems = new List<(YamlNode Node, string SortKey)>(sequence.Children.Count);
        var diffItems = new List<DiffNode>(sequence.Children.Count);

        int minStart = int.MaxValue;
        int maxEnd = -1;

        foreach (YamlNode child in sequence.Children)
        {
            (YamlNode canonicalChild, DiffNode diffChild) = BuildCanonicalAndDiffNode(child, rawInput, orderSensitivity);
            string sortKey = orderSensitivity == OrderSensitivity.IgnoreOrder
                ? SerializeSingleNode(canonicalChild)
                : string.Empty;

            canonicalItems.Add((canonicalChild, sortKey));
            diffItems.Add(diffChild);

            if (diffChild.Span.IsValid)
            {
                minStart = Math.Min(minStart, diffChild.Span.Start);
                maxEnd = Math.Max(maxEnd, diffChild.Span.Start + diffChild.Span.Length);
            }
        }

        if (orderSensitivity == OrderSensitivity.IgnoreOrder)
        {
            canonicalItems.Sort((a, b) => string.CompareOrdinal(a.SortKey, b.SortKey));
        }

        var result = new YamlSequenceNode();
        if (!sequence.Tag.IsEmpty)
        {
            result.Tag = sequence.Tag;
        }

        foreach ((YamlNode item, _) in canonicalItems)
        {
            result.Add(item);
        }

        DiffSpan containerSpan = maxEnd > minStart
            ? DiffSpan.FromBounds(rawInput, minStart, maxEnd)
            : FindEmptyContainerSpan(rawInput, (int)sequence.Start.Index, '[', ']');

        string canonicalStr = SerializeSingleNode(result);
        var diffNode = new DiffNode(
            DiffNodeKind.Sequence,
            containerSpan,
            canonicalStr,
            DiffTreeAnalyzer.FormatPreview(canonicalStr),
            [],
            diffItems);

        return (result, diffNode);
    }

    private static (YamlScalarNode Canonical, DiffNode Diff) BuildScalar(YamlScalarNode scalar, string rawInput)
    {
        YamlScalarNode canonicalScalar = CanonicalizeScalar(scalar);
        DiffSpan span = DiffSpan.FromBounds(rawInput, (int)scalar.Start.Index, (int)scalar.End.Index);
        string canonicalStr = SerializeSingleNode(canonicalScalar);
        string rawSlice = span.IsValid
            ? rawInput.Substring(span.Start, span.Length)
            : (scalar.Value is { Length: > 0 } ? scalar.Value : canonicalStr);

        var diffNode = new DiffNode(
            DiffNodeKind.Scalar,
            span,
            canonicalStr,
            DiffTreeAnalyzer.FormatPreview(rawSlice),
            [],
            []);

        return (canonicalScalar, diffNode);
    }

    private static DiffSpan FindEmptyContainerSpan(string rawInput, int approxStart, char openChar, char closeChar)
    {
        if (string.IsNullOrEmpty(rawInput))
        {
            return DiffSpan.Empty;
        }

        int index = Math.Clamp(approxStart, 0, rawInput.Length);
        while (index < rawInput.Length && char.IsWhiteSpace(rawInput[index]))
        {
            index++;
        }

        if (index < rawInput.Length && rawInput[index] == openChar)
        {
            int end = index + 1;
            while (end < rawInput.Length && char.IsWhiteSpace(rawInput[end]))
            {
                end++;
            }

            if (end < rawInput.Length && rawInput[end] == closeChar)
            {
                return DiffSpan.FromBounds(rawInput, index, end + 1);
            }
        }

        return DiffSpan.Empty;
    }

    private static YamlScalarNode CanonicalizeScalar(YamlScalarNode scalar)
    {
        string value = scalar.Value ?? string.Empty;
        bool wasQuoted = scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted;

        ScalarStyle style;
        if (value.Contains('\n') || value.Contains('\r'))
        {
            style = value.Contains('\r') || value.Contains('\t')
                ? ScalarStyle.DoubleQuoted
                : ScalarStyle.Literal;
        }
        else if (wasQuoted && NeedsQuotingInYaml(value))
        {
            style = ScalarStyle.DoubleQuoted;
        }
        else
        {
            style = ScalarStyle.Plain;
        }

        var result = new YamlScalarNode(value) { Style = style };
        if (!scalar.Tag.IsEmpty)
        {
            result.Tag = scalar.Tag;
        }

        return result;
    }

    private static string SerializeSingleNode(YamlNode node)
    {
        return SerializeDocuments([new YamlDocument(node)]);
    }

    private static string SerializeDocuments(IReadOnlyList<YamlDocument> documents)
    {
        var stream = new YamlStream(documents);
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: false);

        string raw = writer.ToString().Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

        if (documents.Count == 1 && raw.EndsWith("\n...", StringComparison.Ordinal))
        {
            raw = raw[..^4].TrimEnd();
        }
        else if (documents.Count == 1 && raw == "...")
        {
            raw = string.Empty;
        }

        return raw;
    }

    private static bool NeedsQuotingInYaml(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return true;
        }

        if (char.IsWhiteSpace(value[0]) || char.IsWhiteSpace(value[^1]))
        {
            return true;
        }

        if (value.Equals("null", StringComparison.OrdinalIgnoreCase)
            || value.Equals("~", StringComparison.Ordinal)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase)
            || value.Equals("false", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase)
            || value.Equals("on", StringComparison.OrdinalIgnoreCase)
            || value.Equals("off", StringComparison.OrdinalIgnoreCase)
            || value.Equals("y", StringComparison.OrdinalIgnoreCase)
            || value.Equals("n", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
        {
            return true;
        }

        if (YamlIndicatorStartRegex().IsMatch(value))
        {
            return true;
        }

        return value.Contains(": ", StringComparison.Ordinal)
            || value.Contains(" #", StringComparison.Ordinal)
            || value.EndsWith(':');
    }

    [GeneratedRegex(@"^[-?:,\[\]{}#&*!|>'" + "\"" + @"%@`]")]
    private static partial Regex YamlIndicatorStartRegex();
}

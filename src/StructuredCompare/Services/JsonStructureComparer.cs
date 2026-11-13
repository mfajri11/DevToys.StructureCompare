using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using DevToys.StructuredCompare.Models;

namespace DevToys.StructuredCompare.Services;

public sealed class JsonStructureComparer : IStructureComparer
{
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    private static readonly JsonSerializerOptions IndentedOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly JsonSerializerOptions CompactOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public ComparisonFormat Format => ComparisonFormat.Json;

    public ComparisonResult Compare(string? original, string? modified, OrderSensitivity orderSensitivity)
    {
        bool originalEmpty = string.IsNullOrWhiteSpace(original);
        bool modifiedEmpty = string.IsNullOrWhiteSpace(modified);

        if (originalEmpty && modifiedEmpty)
        {
            return new ComparisonResult(ComparisonStatus.Empty, ComparisonFormat.Json, string.Empty, string.Empty);
        }

        if (!TryNormalize(original, "Original", orderSensitivity, out string normalizedOriginal, out string? originalError))
        {
            return new ComparisonResult(
                ComparisonStatus.InvalidInput,
                ComparisonFormat.Json,
                original ?? string.Empty,
                modified ?? string.Empty,
                originalError);
        }

        if (!TryNormalize(modified, "Modified", orderSensitivity, out string normalizedModified, out string? modifiedError))
        {
            return new ComparisonResult(
                ComparisonStatus.InvalidInput,
                ComparisonFormat.Json,
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
                ComparisonFormat.Json,
                normalizedOriginal,
                normalizedModified);
        }

        DiffNode? origOutputNode = string.IsNullOrWhiteSpace(normalizedOriginal)
            ? null
            : BuildJsonDiffTree(normalizedOriginal, orderSensitivity);
        DiffNode? modOutputNode = string.IsNullOrWhiteSpace(normalizedModified)
            ? null
            : BuildJsonDiffTree(normalizedModified, orderSensitivity);

        IReadOnlyList<DiffNode> origDocs = origOutputNode is null ? [] : [origOutputNode];
        IReadOnlyList<DiffNode> modDocs = modOutputNode is null ? [] : [modOutputNode];
        (IReadOnlyList<DiffHighlightSpan> origHighlights, IReadOnlyList<DiffHighlightSpan> modHighlights, DiffSummary summary) =
            DiffTreeAnalyzer.AnalyzeDocuments(origDocs, modDocs, orderSensitivity);

        return new ComparisonResult(
            status,
            ComparisonFormat.Json,
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
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            normalized = string.Empty;
            error = null;
            return true;
        }

        try
        {
            JsonNode? parsed = JsonNode.Parse(input, documentOptions: ParseOptions);
            if (parsed is null)
            {
                normalized = "null";
                error = null;
                return true;
            }

            JsonNode? canonical = CanonicalizeNode(parsed, orderSensitivity);
            normalized = (canonical?.ToJsonString(IndentedOptions) ?? "null")
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .TrimEnd();
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            normalized = string.Empty;
            long line = (ex.LineNumber ?? 0) + 1;
            long col = (ex.BytePositionInLine ?? 0) + 1;
            error = $"{sideLabel} JSON is invalid (Line {line}, Column {col}): {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            normalized = string.Empty;
            error = $"{sideLabel} JSON could not be parsed: {ex.Message}";
            return false;
        }
    }

    private static DiffNode? BuildJsonDiffTree(string input, OrderSensitivity orderSensitivity)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(input);
        int[] byteToChar = BuildByteToCharMap(input, utf8.Length);
        var reader = new Utf8JsonReader(utf8, ReaderOptions);

        if (!reader.Read())
        {
            return null;
        }

        return ReadDiffNode(ref reader, byteToChar, input, orderSensitivity).Diff;
    }

    private static (DiffNode Diff, JsonNode? CanonicalJson) ReadDiffNode(
        ref Utf8JsonReader reader,
        int[] byteToChar,
        string rawText,
        OrderSensitivity orderSensitivity)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                int startChar = byteToChar[(int)reader.TokenStartIndex];
                int endChar = byteToChar[(int)reader.BytesConsumed];

                var entries = new List<(DiffMappingEntry Entry, JsonNode? CanonicalValue)>();
                var keyIndices = new Dictionary<string, int>(StringComparer.Ordinal);

                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndObject)
                    {
                        endChar = byteToChar[(int)reader.BytesConsumed];
                        break;
                    }

                    if (reader.TokenType != JsonTokenType.PropertyName)
                    {
                        continue;
                    }

                    int keyStartChar = byteToChar[(int)reader.TokenStartIndex];
                    int keyRawEndChar = byteToChar[(int)reader.BytesConsumed];
                    int keyEndChar = TrimPropertyColon(rawText, keyStartChar, keyRawEndChar);
                    DiffSpan keySpan = DiffSpan.FromBounds(rawText, keyStartChar, keyEndChar);

                    string propName = reader.GetString() ?? string.Empty;

                    if (!reader.Read())
                    {
                        break;
                    }

                    (DiffNode valueDiff, JsonNode? valueCanonical) = ReadDiffNode(ref reader, byteToChar, rawText, orderSensitivity);
                    int entryEndChar = valueDiff.Span.IsValid
                        ? valueDiff.Span.Start + valueDiff.Span.Length
                        : keyEndChar;
                    DiffSpan entrySpan = DiffSpan.FromBounds(rawText, keyStartChar, entryEndChar);

                    var entry = new DiffMappingEntry(
                        propName,
                        propName,
                        keySpan,
                        entrySpan,
                        valueDiff);

                    if (keyIndices.TryGetValue(propName, out int existingIndex))
                    {
                        entries[existingIndex] = (entry, valueCanonical);
                    }
                    else
                    {
                        keyIndices[propName] = entries.Count;
                        entries.Add((entry, valueCanonical));
                    }
                }

                IEnumerable<(DiffMappingEntry Entry, JsonNode? CanonicalValue)> ordered = orderSensitivity == OrderSensitivity.IgnoreOrder
                    ? entries.OrderBy(static e => e.Entry.CanonicalKey, StringComparer.Ordinal)
                    : entries;

                var canonicalObj = new JsonObject();
                foreach ((DiffMappingEntry entry, JsonNode? valNode) in ordered)
                {
                    canonicalObj[entry.CanonicalKey] = valNode?.DeepClone();
                }

                string canonicalStr = canonicalObj.ToJsonString(CompactOptions);
                var diffNode = new DiffNode(
                    DiffNodeKind.Mapping,
                    DiffSpan.FromBounds(rawText, startChar, endChar),
                    canonicalStr,
                    DiffTreeAnalyzer.FormatPreview(canonicalStr),
                    entries.Select(static e => e.Entry).ToList(),
                    []);

                return (diffNode, canonicalObj);
            }

            case JsonTokenType.StartArray:
            {
                int startChar = byteToChar[(int)reader.TokenStartIndex];
                int endChar = byteToChar[(int)reader.BytesConsumed];

                var items = new List<(DiffNode Diff, JsonNode? CanonicalJson, string SortKey)>();
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.EndArray)
                    {
                        endChar = byteToChar[(int)reader.BytesConsumed];
                        break;
                    }

                    (DiffNode itemDiff, JsonNode? itemCanonical) = ReadDiffNode(ref reader, byteToChar, rawText, orderSensitivity);
                    items.Add((itemDiff, itemCanonical, itemDiff.CanonicalValue));
                }

                IEnumerable<(DiffNode Diff, JsonNode? CanonicalJson, string SortKey)> ordered = orderSensitivity == OrderSensitivity.IgnoreOrder
                    ? items.OrderBy(static i => i.SortKey, StringComparer.Ordinal)
                    : items;

                var canonicalArray = new JsonArray();
                foreach ((_, JsonNode? itemNode, _) in ordered)
                {
                    canonicalArray.Add(itemNode?.DeepClone());
                }

                string canonicalStr = canonicalArray.ToJsonString(CompactOptions);
                var diffNode = new DiffNode(
                    DiffNodeKind.Sequence,
                    DiffSpan.FromBounds(rawText, startChar, endChar),
                    canonicalStr,
                    DiffTreeAnalyzer.FormatPreview(canonicalStr),
                    [],
                    items.Select(static i => i.Diff).ToList());

                return (diffNode, canonicalArray);
            }

            default:
            {
                int startChar = byteToChar[(int)reader.TokenStartIndex];
                int endChar = byteToChar[(int)reader.BytesConsumed];
                DiffSpan span = DiffSpan.FromBounds(rawText, startChar, endChar);
                string tokenSlice = span.IsValid ? rawText.Substring(span.Start, span.Length) : "null";

                JsonNode? scalarNode = JsonNode.Parse(tokenSlice, documentOptions: ParseOptions);
                string canonicalStr = scalarNode?.ToJsonString(CompactOptions) ?? "null";

                var diffNode = new DiffNode(
                    DiffNodeKind.Scalar,
                    span,
                    canonicalStr,
                    DiffTreeAnalyzer.FormatPreview(tokenSlice),
                    [],
                    []);

                return (diffNode, scalarNode);
            }
        }
    }

    private static int TrimPropertyColon(string text, int start, int end)
    {
        int clampedEnd = Math.Clamp(end, start, text.Length);
        while (clampedEnd > start && (char.IsWhiteSpace(text[clampedEnd - 1]) || text[clampedEnd - 1] == ':'))
        {
            clampedEnd--;
        }

        return clampedEnd;
    }

    private static int[] BuildByteToCharMap(string text, int utf8ByteCount)
    {
        var map = new int[utf8ByteCount + 1];
        int byteIndex = 0;
        int charIndex = 0;

        while (charIndex < text.Length)
        {
            int charStep = char.IsHighSurrogate(text[charIndex])
                && charIndex + 1 < text.Length
                && char.IsLowSurrogate(text[charIndex + 1])
                ? 2
                : 1;

            int byteStep = Encoding.UTF8.GetByteCount(text.AsSpan(charIndex, charStep));
            for (int b = 0; b < byteStep && byteIndex + b <= utf8ByteCount; b++)
            {
                map[byteIndex + b] = charIndex;
            }

            byteIndex += byteStep;
            charIndex += charStep;
        }

        map[utf8ByteCount] = text.Length;
        return map;
    }

    private static JsonNode? CanonicalizeNode(JsonNode? node, OrderSensitivity orderSensitivity)
    {
        return node switch
        {
            JsonObject obj => CanonicalizeObject(obj, orderSensitivity),
            JsonArray arr => CanonicalizeArray(arr, orderSensitivity),
            JsonValue val => val.DeepClone(),
            null => null,
            _ => node.DeepClone()
        };
    }

    private static JsonObject CanonicalizeObject(JsonObject obj, OrderSensitivity orderSensitivity)
    {
        IEnumerable<KeyValuePair<string, JsonNode?>> properties = obj;
        if (orderSensitivity == OrderSensitivity.IgnoreOrder)
        {
            properties = obj.OrderBy(static kvp => kvp.Key, StringComparer.Ordinal);
        }

        var result = new JsonObject();
        foreach (KeyValuePair<string, JsonNode?> kvp in properties)
        {
            result[kvp.Key] = CanonicalizeNode(kvp.Value, orderSensitivity);
        }

        return result;
    }

    private static JsonArray CanonicalizeArray(JsonArray array, OrderSensitivity orderSensitivity)
    {
        var items = new List<(JsonNode? Node, string SortKey)>(array.Count);
        foreach (JsonNode? item in array)
        {
            JsonNode? canonicalItem = CanonicalizeNode(item, orderSensitivity);
            string sortKey = orderSensitivity == OrderSensitivity.IgnoreOrder
                ? canonicalItem?.ToJsonString(CompactOptions) ?? "null"
                : string.Empty;
            items.Add((canonicalItem, sortKey));
        }

        if (orderSensitivity == OrderSensitivity.IgnoreOrder)
        {
            items.Sort((a, b) => string.CompareOrdinal(a.SortKey, b.SortKey));
        }

        var result = new JsonArray();
        foreach ((JsonNode? item, _) in items)
        {
            result.Add(item);
        }

        return result;
    }
}

using System.Text;
using System.Text.RegularExpressions;
using DevToys.StructuredCompare.Models;

namespace DevToys.StructuredCompare.Services;

public sealed partial class EnvStructureComparer : IStructureComparer
{
    public ComparisonFormat Format => ComparisonFormat.Env;

    public ComparisonResult Compare(string? original, string? modified, OrderSensitivity orderSensitivity)
    {
        bool originalEmpty = string.IsNullOrWhiteSpace(original);
        bool modifiedEmpty = string.IsNullOrWhiteSpace(modified);

        if (originalEmpty && modifiedEmpty)
        {
            return new ComparisonResult(ComparisonStatus.Empty, ComparisonFormat.Env, string.Empty, string.Empty);
        }

        if (!TryNormalize(original, "Original", orderSensitivity, out string normalizedOriginal, out _, out string? originalError))
        {
            return new ComparisonResult(
                ComparisonStatus.InvalidInput,
                ComparisonFormat.Env,
                original ?? string.Empty,
                modified ?? string.Empty,
                originalError);
        }

        if (!TryNormalize(modified, "Modified", orderSensitivity, out string normalizedModified, out _, out string? modifiedError))
        {
            return new ComparisonResult(
                ComparisonStatus.InvalidInput,
                ComparisonFormat.Env,
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
                ComparisonFormat.Env,
                normalizedOriginal,
                normalizedModified);
        }

        DiffNode? origOutputNode = TryNormalize(normalizedOriginal, "Original", orderSensitivity, out _, out DiffNode? parsedOrigNode, out _)
            ? parsedOrigNode
            : null;
        DiffNode? modOutputNode = TryNormalize(normalizedModified, "Modified", orderSensitivity, out _, out DiffNode? parsedModNode, out _)
            ? parsedModNode
            : null;

        IReadOnlyList<DiffNode> origDocs = origOutputNode is null ? [] : [origOutputNode];
        IReadOnlyList<DiffNode> modDocs = modOutputNode is null ? [] : [modOutputNode];
        (IReadOnlyList<DiffHighlightSpan> origHighlights, IReadOnlyList<DiffHighlightSpan> modHighlights, DiffSummary summary) =
            DiffTreeAnalyzer.AnalyzeDocuments(origDocs, modDocs, orderSensitivity);

        return new ComparisonResult(
            status,
            ComparisonFormat.Env,
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
        out DiffNode? rootNode,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            normalized = string.Empty;
            rootNode = null;
            error = null;
            return true;
        }

        if (!TryParseEntries(input, sideLabel, out List<EnvEntry> entries, out error))
        {
            normalized = string.Empty;
            rootNode = null;
            return false;
        }

        if (entries.Count == 0)
        {
            normalized = string.Empty;
            rootNode = null;
            error = null;
            return true;
        }

        List<EnvEntry> orderedEntries = orderSensitivity == OrderSensitivity.IgnoreOrder
            ? entries.OrderBy(static e => e.Key, StringComparer.Ordinal).ToList()
            : entries;

        normalized = string.Join('\n', orderedEntries.Select(static e => $"{e.Key}={FormatEnvValue(e.Value)}"));

        var mappingEntries = new List<DiffMappingEntry>(entries.Count);
        foreach (EnvEntry entry in entries)
        {
            string formatted = FormatEnvValue(entry.Value);
            var valueNode = new DiffNode(
                DiffNodeKind.Scalar,
                entry.ValueSpan,
                formatted,
                DiffTreeAnalyzer.FormatPreview(entry.Value.Length == 0 ? "<empty>" : entry.Value),
                [],
                []);

            mappingEntries.Add(new DiffMappingEntry(
                entry.Key,
                entry.Key,
                entry.KeySpan,
                entry.EntrySpan,
                valueNode));
        }

        rootNode = new DiffNode(
            DiffNodeKind.Mapping,
            DiffSpan.FromBounds(input, 0, input.Length),
            normalized,
            "{.env}",
            mappingEntries,
            []);

        error = null;
        return true;
    }

    private static bool TryParseEntries(
        string input,
        string sideLabel,
        out List<EnvEntry> entries,
        out string? error)
    {
        entries = [];
        var keyIndices = new Dictionary<string, int>(StringComparer.Ordinal);

        int index = input.Length > 0 && input[0] == '\uFEFF' ? 1 : 0;
        int lineNumber = 1;
        int length = input.Length;

        while (index < length)
        {
            // Skip horizontal whitespace
            while (index < length && (input[index] == ' ' || input[index] == '\t'))
            {
                index++;
            }

            if (index >= length)
            {
                break;
            }

            if (IsNewLine(input[index]))
            {
                AdvanceNewLine(input, ref index, ref lineNumber);
                continue;
            }

            // Skip comment line
            if (input[index] == '#')
            {
                while (index < length && !IsNewLine(input[index]))
                {
                    index++;
                }

                continue;
            }

            int entryStartLine = lineNumber;
            int entryStart = index;

            // Optional "export " prefix
            if (index + 7 <= length
                && input.AsSpan(index, 6).SequenceEqual("export")
                && (input[index + 6] == ' ' || input[index + 6] == '\t'))
            {
                index += 6;
                while (index < length && (input[index] == ' ' || input[index] == '\t'))
                {
                    index++;
                }
            }

            // Read key up to '=' or newline
            int keyStart = index;
            while (index < length && input[index] != '=' && !IsNewLine(input[index]))
            {
                index++;
            }

            if (index >= length || input[index] != '=')
            {
                error = $"{sideLabel} .env is invalid at line {entryStartLine}: expected KEY=VALUE format.";
                return false;
            }

            int keyRawEnd = index;
            string key = input[keyStart..keyRawEnd].Trim();
            if (!ValidKeyRegex().IsMatch(key))
            {
                error = $"{sideLabel} .env is invalid at line {entryStartLine}: invalid variable name '{key}'.";
                return false;
            }

            DiffSpan keySpan = DiffSpan.FromBounds(input, keyStart, keyRawEnd);

            // Skip '=' and leading horizontal whitespace before value
            index++;
            while (index < length && (input[index] == ' ' || input[index] == '\t'))
            {
                index++;
            }

            string value;
            DiffSpan valueSpan;
            int entryEnd;

            if (index < length && (input[index] == '"' || input[index] == '\''))
            {
                char quote = input[index];
                int quotedStart = index;
                index++;
                var sb = new StringBuilder();
                bool closed = false;

                while (index < length)
                {
                    char c = input[index];
                    if (IsNewLine(c))
                    {
                        if (c == '\r' && index + 1 < length && input[index + 1] == '\n')
                        {
                            sb.Append('\n');
                            index += 2;
                            lineNumber++;
                            continue;
                        }

                        if (c == '\r')
                        {
                            sb.Append('\n');
                            index++;
                            lineNumber++;
                            continue;
                        }

                        lineNumber++;
                    }

                    if (quote == '"' && c == '\\' && index + 1 < length)
                    {
                        char next = input[index + 1];
                        switch (next)
                        {
                            case 'n':
                                sb.Append('\n');
                                index += 2;
                                continue;
                            case 'r':
                                sb.Append('\r');
                                index += 2;
                                continue;
                            case 't':
                                sb.Append('\t');
                                index += 2;
                                continue;
                            case '\\':
                            case '"':
                                sb.Append(next);
                                index += 2;
                                continue;
                        }
                    }

                    if (c == quote)
                    {
                        closed = true;
                        index++;
                        break;
                    }

                    sb.Append(c);
                    index++;
                }

                if (!closed)
                {
                    error = $"{sideLabel} .env is invalid at line {entryStartLine}: unclosed {quote} quote for '{key}'.";
                    return false;
                }

                entryEnd = index;
                valueSpan = DiffSpan.FromBounds(input, quotedStart, entryEnd);
                value = sb.ToString();

                // Skip trailing whitespace and optional inline comment on the closing line
                while (index < length && (input[index] == ' ' || input[index] == '\t'))
                {
                    index++;
                }

                if (index < length && input[index] == '#')
                {
                    while (index < length && !IsNewLine(input[index]))
                    {
                        index++;
                    }
                }
                else if (index < length && !IsNewLine(input[index]))
                {
                    error = $"{sideLabel} .env is invalid at line {lineNumber}: unexpected trailing characters after quoted value for '{key}'.";
                    return false;
                }
            }
            else
            {
                int valStart = index;
                while (index < length && !IsNewLine(input[index]))
                {
                    index++;
                }

                string rawVal = input[valStart..index];
                string unquotedWithoutComment = StripUnquotedInlineComment(rawVal);
                string trimmed = unquotedWithoutComment.Trim();
                value = trimmed;

                if (trimmed.Length > 0)
                {
                    int leadingSpaces = 0;
                    while (leadingSpaces < unquotedWithoutComment.Length && char.IsWhiteSpace(unquotedWithoutComment[leadingSpaces]))
                    {
                        leadingSpaces++;
                    }

                    int trimmedEnd = unquotedWithoutComment.Length;
                    while (trimmedEnd > leadingSpaces && char.IsWhiteSpace(unquotedWithoutComment[trimmedEnd - 1]))
                    {
                        trimmedEnd--;
                    }

                    valueSpan = DiffSpan.FromBounds(input, valStart + leadingSpaces, valStart + trimmedEnd);
                    entryEnd = valStart + trimmedEnd;
                }
                else
                {
                    valueSpan = DiffSpan.Empty;
                    entryEnd = keyRawEnd + 1;
                }
            }

            DiffSpan entrySpan = DiffSpan.FromBounds(input, entryStart, entryEnd);
            var envEntry = new EnvEntry(key, value, keySpan, valueSpan, entrySpan);

            if (keyIndices.TryGetValue(key, out int existingIndex))
            {
                entries[existingIndex] = envEntry;
            }
            else
            {
                keyIndices[key] = entries.Count;
                entries.Add(envEntry);
            }
        }

        error = null;
        return true;
    }

    private static bool IsNewLine(char c) => c is '\n' or '\r';

    private static void AdvanceNewLine(string text, ref int index, ref int lineNumber)
    {
        if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
        {
            index += 2;
        }
        else
        {
            index++;
        }

        lineNumber++;
    }

    private static string StripUnquotedInlineComment(string rawValue)
    {
        for (int i = 0; i < rawValue.Length; i++)
        {
            if (rawValue[i] == '#' && (i == 0 || char.IsWhiteSpace(rawValue[i - 1])))
            {
                return rawValue[..i];
            }
        }

        return rawValue;
    }

    private static string FormatEnvValue(string value)
    {
        if (value.Length == 0)
        {
            return string.Empty;
        }

        bool needsQuotes = char.IsWhiteSpace(value[0])
            || char.IsWhiteSpace(value[^1])
            || value.Contains('\n')
            || value.Contains('\r')
            || value.Contains('\t')
            || value.Contains('#')
            || value.Contains('"')
            || value.Contains('\'');

        if (!needsQuotes)
        {
            return value;
        }

        string escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);

        return $"\"{escaped}\"";
    }

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.-]*$")]
    private static partial Regex ValidKeyRegex();

    private sealed record EnvEntry(
        string Key,
        string Value,
        DiffSpan KeySpan,
        DiffSpan ValueSpan,
        DiffSpan EntrySpan);
}

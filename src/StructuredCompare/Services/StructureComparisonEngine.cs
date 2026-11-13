using System.Text.Json;
using System.Text.RegularExpressions;
using DevToys.StructuredCompare.Models;

namespace DevToys.StructuredCompare.Services;

public sealed partial class StructureComparisonEngine
{
    private readonly YamlStructureComparer _yamlComparer = new();
    private readonly JsonStructureComparer _jsonComparer = new();
    private readonly EnvStructureComparer _envComparer = new();

    public ComparisonResult Compare(
        string? original,
        string? modified,
        ComparisonFormat requestedFormat,
        OrderSensitivity orderSensitivity)
    {
        ComparisonFormat effectiveFormat = requestedFormat == ComparisonFormat.AutoDetect
            ? DetectFormat(original, modified)
            : requestedFormat;

        return effectiveFormat switch
        {
            ComparisonFormat.Json => _jsonComparer.Compare(original, modified, orderSensitivity),
            ComparisonFormat.Env => _envComparer.Compare(original, modified, orderSensitivity),
            _ => _yamlComparer.Compare(original, modified, orderSensitivity)
        };
    }

    public ComparisonFormat DetectFormat(string? original, string? modified)
    {
        string? primary = !string.IsNullOrWhiteSpace(original) ? original : modified;
        if (string.IsNullOrWhiteSpace(primary))
        {
            return ComparisonFormat.Yaml;
        }

        if (LooksLikeJson(original, modified))
        {
            return ComparisonFormat.Json;
        }

        if (LooksLikeEnv(_envComparer, original)
            && (string.IsNullOrWhiteSpace(modified) || LooksLikeEnv(_envComparer, modified)))
        {
            return ComparisonFormat.Env;
        }

        if (string.IsNullOrWhiteSpace(original) && LooksLikeEnv(_envComparer, modified))
        {
            return ComparisonFormat.Env;
        }

        return ComparisonFormat.Yaml;
    }

    private static bool LooksLikeJson(string? original, string? modified)
    {
        bool origHasJsonStart = StartsLikeJsonContainer(original);
        bool modHasJsonStart = StartsLikeJsonContainer(modified);

        if (!origHasJsonStart && !modHasJsonStart)
        {
            return false;
        }

        if (IsValidJson(original) || IsValidJson(modified))
        {
            return true;
        }

        return (origHasJsonStart && (original!.Contains('"', StringComparison.Ordinal) || original.TrimStart().StartsWith('[')))
            || (modHasJsonStart && (modified!.Contains('"', StringComparison.Ordinal) || modified.TrimStart().StartsWith('[')));
    }

    private static bool StartsLikeJsonContainer(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = text.AsSpan().TrimStart();
        return trimmed.Length > 0 && (trimmed[0] == '{' || trimmed[0] == '[');
    }

    private static bool IsValidJson(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            using JsonDocument _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool LooksLikeEnv(EnvStructureComparer envComparer, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string? firstMeaningfulLine = null;
        using var reader = new StringReader(text);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            string trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            firstMeaningfulLine = trimmed;
            break;
        }

        if (firstMeaningfulLine is null || !EnvAssignmentRegex().IsMatch(firstMeaningfulLine))
        {
            return false;
        }

        return envComparer.Compare(text, text, OrderSensitivity.StrictOrder).Status == ComparisonStatus.Equivalent;
    }

    [GeneratedRegex(@"^(?:export\s+)?[A-Za-z_][A-Za-z0-9_.-]*\s*=")]
    private static partial Regex EnvAssignmentRegex();
}

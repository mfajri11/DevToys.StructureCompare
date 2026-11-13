using DevToys.StructuredCompare.Models;
using DevToys.StructuredCompare.Services;

namespace StructuredCompare.Tests;

public sealed class InlineDiffFormatterTests
{
    private readonly YamlStructureComparer _yamlComparer = new();

    [Fact]
    public void Build_DifferentYaml_EmitsBoundedInlineDiffHighlightsAndTooltips()
    {
        const string original = """
            envVariables:
              - name: APP_ENV
                value: predeploy
              - name: REMOVED_KEY
                value: old
            """;

        const string modified = """
            envVariables:
              - name: APP_ENV
                value: prod
              - name: ADDED_KEY
                value: new
            """;

        ComparisonResult result = _yamlComparer.Compare(original, modified, OrderSensitivity.IgnoreOrder);
        (string text, IReadOnlyList<DiffHighlightSpan> highlights) = InlineDiffFormatter.Build(result);

        Assert.NotEmpty(text);
        Assert.NotEmpty(highlights);
        Assert.Contains(highlights, h => h.Kind == DiffChangeKind.Modified && text.Substring(h.Start, h.Length).Contains("predeploy", StringComparison.Ordinal));
        Assert.Contains(highlights, h => h.Kind == DiffChangeKind.Modified && text.Substring(h.Start, h.Length).Contains("prod", StringComparison.Ordinal));
        Assert.Contains(highlights, h => h.Kind == DiffChangeKind.Removed && text.Substring(h.Start, h.Length).Contains("REMOVED_KEY", StringComparison.Ordinal));
        Assert.Contains(highlights, h => h.Kind == DiffChangeKind.Added && text.Substring(h.Start, h.Length).Contains("ADDED_KEY", StringComparison.Ordinal));
        Assert.All(highlights, h =>
        {
            Assert.True(h.Start >= 0);
            Assert.True(h.Length > 0);
            Assert.True(h.Start + h.Length <= text.Length);
            Assert.False(string.IsNullOrWhiteSpace(h.Tooltip));
        });
    }

    [Fact]
    public void Build_Equivalent_ReturnsNormalizedTextWithoutHighlights()
    {
        ComparisonResult result = _yamlComparer.Compare("a: 1\nb: 2", "b: 2\na: 1", OrderSensitivity.IgnoreOrder);
        (string text, IReadOnlyList<DiffHighlightSpan> highlights) = InlineDiffFormatter.Build(result);

        Assert.Equal(result.NormalizedOriginal, text);
        Assert.Empty(highlights);
    }

    [Fact]
    public void Build_InvalidOrEmpty_ReturnsEmptyWithoutHighlights()
    {
        ComparisonResult empty = _yamlComparer.Compare("", "", OrderSensitivity.StrictOrder);
        ComparisonResult invalid = _yamlComparer.Compare("key: [unclosed", "key: 1", OrderSensitivity.StrictOrder);

        Assert.Equal((string.Empty, []), InlineDiffFormatter.Build(empty));
        Assert.Equal((string.Empty, []), InlineDiffFormatter.Build(invalid));
    }
}

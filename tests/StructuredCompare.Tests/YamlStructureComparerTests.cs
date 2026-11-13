using DevToys.StructuredCompare.Models;
using DevToys.StructuredCompare.Services;

namespace StructuredCompare.Tests;

public sealed class YamlStructureComparerTests
{
    private readonly YamlStructureComparer _sut = new();

    [Fact]
    public void Compare_BothEmpty_ReturnsEmpty()
    {
        ComparisonResult result = _sut.Compare("   ", "\n\t", OrderSensitivity.StrictOrder);

        Assert.Equal(ComparisonStatus.Empty, result.Status);
        Assert.Equal(ComparisonFormat.Yaml, result.DetectedFormat);
    }

    [Fact]
    public void Compare_SameOrderDifferentCommentsAndSpacing_EquivalentInBothModes()
    {
        const string original = """
            # header comment
            app:
              name:   "my-service"
              port: 8080
              tags: [api, v1]
            """;

        const string modified = """
            app:
              name: 'my-service'
              port: 8080 # inline comment
              tags:
                - api
                - v1
            """;

        ComparisonResult strict = _sut.Compare(original, modified, OrderSensitivity.StrictOrder);
        ComparisonResult ignore = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Equivalent, strict.Status);
        Assert.Equal(ComparisonStatus.Equivalent, ignore.Status);
        Assert.Null(strict.OriginalHighlights);
        Assert.Null(ignore.OriginalHighlights);
    }

    [Fact]
    public void Compare_ReorderedKeys_DifferentInStrict_EquivalentInIgnore()
    {
        const string original = """
            server:
              host: localhost
              port: 8080
            enabled: true
            """;

        const string modified = """
            enabled: true
            server:
              port: 8080
              host: localhost
            """;

        ComparisonResult strict = _sut.Compare(original, modified, OrderSensitivity.StrictOrder);
        ComparisonResult ignore = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Different, strict.Status);
        Assert.NotNull(strict.OriginalHighlights);
        Assert.NotNull(strict.ModifiedHighlights);
        Assert.Contains(strict.OriginalHighlights!, h => h.Kind == DiffChangeKind.Reordered);
        Assert.Contains(strict.ModifiedHighlights!, h => h.Kind == DiffChangeKind.Reordered);
        Assert.Equal(ComparisonStatus.Equivalent, ignore.Status);
        Assert.Null(ignore.OriginalHighlights);
    }

    [Fact]
    public void Compare_ReorderedSequenceOfMaps_DifferentInStrict_EquivalentInIgnore()
    {
        const string original = """
            services:
              - name: auth
                port: 9000
              - name: api
                port: 8080
            """;

        const string modified = """
            services:
              - port: 8080
                name: api
              - port: 9000
                name: auth
            """;

        ComparisonResult strict = _sut.Compare(original, modified, OrderSensitivity.StrictOrder);
        ComparisonResult ignore = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Different, strict.Status);
        Assert.Equal(ComparisonStatus.Equivalent, ignore.Status);
    }

    [Fact]
    public void Compare_SequenceOfMapsIgnoreOrder_HighlightsExactChangedFieldAndAddedRemovedItemsOnNormalizedOutput()
    {
        const string original = """
            # comment on input
            envVariables:
              - name: APP_NAME
                value: "qcash-lucario-service"
              - name: APP_ENV
                value: "predeploy"
              - name: REMOVED_VAR
                value: "gone"
            """;

        const string modified = """
            envVariables:
              - name: APP_ENV
                value: "prod"
              - name: APP_NAME
                value: "qcash-lucario-service"
              - name: NEW_VAR
                value: "added"
            """;

        ComparisonResult result = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.NotNull(result.Summary);
        Assert.Equal(1, result.Summary!.ModifiedCount);
        Assert.Equal(1, result.Summary.RemovedCount);
        Assert.Equal(1, result.Summary.AddedCount);
        Assert.Equal(0, result.Summary.ReorderedCount);

        DiffHighlightSpan origMod = Assert.Single(result.OriginalHighlights!, h => h.Kind == DiffChangeKind.Modified);
        Assert.Equal("predeploy", result.NormalizedOriginal.Substring(origMod.Start, origMod.Length));

        DiffHighlightSpan modMod = Assert.Single(result.ModifiedHighlights!, h => h.Kind == DiffChangeKind.Modified);
        Assert.Equal("prod", result.NormalizedModified.Substring(modMod.Start, modMod.Length));

        DiffHighlightSpan origRem = Assert.Single(result.OriginalHighlights!, h => h.Kind == DiffChangeKind.Removed);
        Assert.Contains("REMOVED_VAR", result.NormalizedOriginal.Substring(origRem.Start, origRem.Length), StringComparison.Ordinal);

        DiffHighlightSpan modAdd = Assert.Single(result.ModifiedHighlights!, h => h.Kind == DiffChangeKind.Added);
        Assert.Contains("NEW_VAR", result.NormalizedModified.Substring(modAdd.Start, modAdd.Length), StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_EmptyScalarAndCrlfLineEndings_ProducesValidBoundedOutputHighlights()
    {
        const string original = "name: app\r\nextra:\r\n";
        const string modified = "name: app\r\nextra: present\r\n";

        ComparisonResult result = _sut.Compare(original, modified, OrderSensitivity.StrictOrder);

        Assert.Equal(ComparisonStatus.Different, result.Status);
        DiffHighlightSpan origSpan = Assert.Single(result.OriginalHighlights!);
        Assert.Equal("''", result.NormalizedOriginal.Substring(origSpan.Start, origSpan.Length));

        DiffHighlightSpan modSpan = Assert.Single(result.ModifiedHighlights!);
        Assert.Equal("present", result.NormalizedModified.Substring(modSpan.Start, modSpan.Length));
    }

    [Fact]
    public void Compare_OneSideEmpty_HighlightsEntriesOnNonEmptyOutputSide()
    {
        const string modified = "host: localhost\nport: 8080";

        ComparisonResult result = _sut.Compare(string.Empty, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.Empty(result.OriginalHighlights!);
        Assert.Equal(2, result.ModifiedHighlights!.Count);
        Assert.All(result.ModifiedHighlights!, h => Assert.Equal(DiffChangeKind.Added, h.Kind));
    }

    [Fact]
    public void Compare_QuotedNumberVsUnquotedNumber_RemainsDifferent()
    {
        const string original = "port: \"8080\"";
        const string modified = "port: 8080";

        ComparisonResult strict = _sut.Compare(original, modified, OrderSensitivity.StrictOrder);
        ComparisonResult ignore = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Different, strict.Status);
        Assert.Equal(ComparisonStatus.Different, ignore.Status);
    }

    [Fact]
    public void Compare_MultiDocumentYaml_HandlesOrderCorrectly()
    {
        const string original = """
            ---
            kind: ConfigMap
            name: first
            ---
            kind: Secret
            name: second
            """;

        const string modified = """
            ---
            name: first
            kind: ConfigMap
            ---
            name: second
            kind: Secret
            """;

        ComparisonResult strict = _sut.Compare(original, modified, OrderSensitivity.StrictOrder);
        ComparisonResult ignore = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Different, strict.Status);
        Assert.Equal(ComparisonStatus.Equivalent, ignore.Status);
    }

    [Fact]
    public void Compare_InvalidYaml_ReturnsInvalidInputWithSideAndLocation()
    {
        const string original = "key: [unclosed";
        const string modified = "key: value";

        ComparisonResult result = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.InvalidInput, result.Status);
        Assert.Contains("Original YAML is invalid", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Line ", result.ErrorMessage, StringComparison.Ordinal);
    }
}

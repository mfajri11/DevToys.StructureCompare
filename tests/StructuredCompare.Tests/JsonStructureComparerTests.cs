using DevToys.StructuredCompare.Models;
using DevToys.StructuredCompare.Services;

namespace StructuredCompare.Tests;

public sealed class JsonStructureComparerTests
{
    private readonly JsonStructureComparer _sut = new();

    [Fact]
    public void Compare_BothEmpty_ReturnsEmpty()
    {
        ComparisonResult result = _sut.Compare(string.Empty, "   ", OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Empty, result.Status);
        Assert.Equal(ComparisonFormat.Json, result.DetectedFormat);
    }

    [Fact]
    public void Compare_MinifiedVsPrettyPrintedSameOrder_EquivalentInBothModes()
    {
        const string original = "{\"name\":\"app\",\"ports\":[80,443],\"nested\":{\"a\":1,\"b\":2}}";
        const string modified = """
            {
              "name": "app",
              "ports": [
                80,
                443
              ],
              "nested": {
                "a": 1,
                "b": 2
              }
            }
            """;

        Assert.Equal(ComparisonStatus.Equivalent, _sut.Compare(original, modified, OrderSensitivity.StrictOrder).Status);
        Assert.Equal(ComparisonStatus.Equivalent, _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder).Status);
    }

    [Fact]
    public void Compare_ReorderedKeysAndArrayObjects_DifferentInStrict_EquivalentInIgnore()
    {
        const string original = """
            {
              "env": [
                { "name": "B", "value": "2" },
                { "name": "A", "value": "1" }
              ],
              "version": 1
            }
            """;

        const string modified = """
            {
              "version": 1,
              "env": [
                { "value": "1", "name": "A" },
                { "value": "2", "name": "B" }
              ]
            }
            """;

        ComparisonResult strict = _sut.Compare(original, modified, OrderSensitivity.StrictOrder);
        ComparisonResult ignore = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Different, strict.Status);
        Assert.NotEmpty(strict.OriginalHighlights!);
        Assert.NotEmpty(strict.ModifiedHighlights!);
        Assert.Equal(ComparisonStatus.Equivalent, ignore.Status);
        Assert.Null(ignore.OriginalHighlights);
    }

    [Fact]
    public void Compare_DifferentValues_DifferentInBothModes()
    {
        const string original = """{"a": 1, "b": 2}""";
        const string modified = """{"b": 3, "a": 1}""";

        Assert.Equal(ComparisonStatus.Different, _sut.Compare(original, modified, OrderSensitivity.StrictOrder).Status);
        Assert.Equal(ComparisonStatus.Different, _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder).Status);
    }

    [Fact]
    public void Compare_EmojiAndAddedRemovedModified_MapsNormalizedOutputUtf16SpansAccurately()
    {
        const string original = """{"icon":"🚀","count":1,"legacy":true}""";
        const string modified = """{"extra":"ok","icon":"🚀","count":2}""";

        ComparisonResult result = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.Equal(1, result.Summary!.ModifiedCount);
        Assert.Equal(1, result.Summary.RemovedCount);
        Assert.Equal(1, result.Summary.AddedCount);

        DiffHighlightSpan origMod = Assert.Single(result.OriginalHighlights!, h => h.Kind == DiffChangeKind.Modified);
        Assert.Equal("1", result.NormalizedOriginal.Substring(origMod.Start, origMod.Length));

        DiffHighlightSpan modMod = Assert.Single(result.ModifiedHighlights!, h => h.Kind == DiffChangeKind.Modified);
        Assert.Equal("2", result.NormalizedModified.Substring(modMod.Start, modMod.Length));

        DiffHighlightSpan origRem = Assert.Single(result.OriginalHighlights!, h => h.Kind == DiffChangeKind.Removed);
        Assert.Equal("\"legacy\": true", result.NormalizedOriginal.Substring(origRem.Start, origRem.Length));

        DiffHighlightSpan modAdd = Assert.Single(result.ModifiedHighlights!, h => h.Kind == DiffChangeKind.Added);
        Assert.Equal("\"extra\": \"ok\"", result.NormalizedModified.Substring(modAdd.Start, modAdd.Length));
    }

    [Fact]
    public void Compare_InvalidJson_ReturnsInvalidInputWithSide()
    {
        const string original = """{"a": 1}""";
        const string modified = """{"a": }""";

        ComparisonResult result = _sut.Compare(original, modified, OrderSensitivity.StrictOrder);

        Assert.Equal(ComparisonStatus.InvalidInput, result.Status);
        Assert.Contains("Modified JSON is invalid", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Line 1", result.ErrorMessage, StringComparison.Ordinal);
    }
}

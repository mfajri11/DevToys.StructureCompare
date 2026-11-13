using DevToys.StructuredCompare.Models;
using DevToys.StructuredCompare.Services;
using StringsRes = DevToys.StructuredCompare.Strings.StructuredCompare;

namespace StructuredCompare.Tests;

public sealed class StructureComparisonEngineTests
{
    private readonly StructureComparisonEngine _sut = new();

    [Fact]
    public void AutoDetect_JsonInputs_DetectsJsonAndIgnoresOrder()
    {
        ComparisonResult result = _sut.Compare(
            "{\"b\": 2, \"a\": 1}",
            "{\n  \"a\": 1,\n  \"b\": 2\n}",
            ComparisonFormat.AutoDetect,
            OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonFormat.Json, result.DetectedFormat);
        Assert.Equal(ComparisonStatus.Equivalent, result.Status);
    }

    [Fact]
    public void AutoDetect_EnvInputsWithMultilineValue_DetectsEnvAndIgnoresOrder()
    {
        const string original = """
            CERT="line1
            line2"
            HOST=localhost
            """;

        const string modified = """
            HOST=localhost
            CERT="line1\nline2"
            """;

        ComparisonResult result = _sut.Compare(
            original,
            modified,
            ComparisonFormat.AutoDetect,
            OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonFormat.Env, result.DetectedFormat);
        Assert.Equal(ComparisonStatus.Equivalent, result.Status);
    }

    [Fact]
    public void AutoDetect_YamlInputs_DetectsYaml()
    {
        ComparisonResult result = _sut.Compare(
            "b: 2\na: 1",
            "a: 1\nb: 2",
            ComparisonFormat.AutoDetect,
            OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonFormat.Yaml, result.DetectedFormat);
        Assert.Equal(ComparisonStatus.Equivalent, result.Status);
    }

    [Fact]
    public void Resources_AllDisplayStringsLoadProperly()
    {
        Assert.Equal("Structured Compare", StringsRes.ShortDisplayTitle);
        Assert.Equal("Strict Order", StringsRes.OrderStrict);
        Assert.Equal("Ignore Order", StringsRes.OrderIgnore);
        Assert.Equal(".env", StringsRes.FormatEnv);
    }
}

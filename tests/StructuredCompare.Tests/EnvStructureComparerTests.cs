using DevToys.StructuredCompare.Models;
using DevToys.StructuredCompare.Services;

namespace StructuredCompare.Tests;

public sealed class EnvStructureComparerTests
{
    private readonly EnvStructureComparer _sut = new();

    [Fact]
    public void Compare_CommentsBlankLinesAndExportNormalized_EquivalentInBothModes()
    {
        const string original = """
            # Database settings
            export DB_HOST=localhost
            DB_PORT="5432"
            API_TOKEN='abc==def' # trailing comment
            """;

        const string modified = """
            DB_HOST=localhost
            DB_PORT=5432
            API_TOKEN=abc==def
            """;

        Assert.Equal(ComparisonStatus.Equivalent, _sut.Compare(original, modified, OrderSensitivity.StrictOrder).Status);
        Assert.Equal(ComparisonStatus.Equivalent, _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder).Status);
    }

    [Fact]
    public void Compare_ReorderedEntries_DifferentInStrict_EquivalentInIgnore()
    {
        const string original = """
            APP_ENV=production
            LOG_LEVEL=info
            PORT=8080
            """;

        const string modified = """
            PORT=8080
            APP_ENV=production
            LOG_LEVEL=info
            """;

        ComparisonResult strict = _sut.Compare(original, modified, OrderSensitivity.StrictOrder);
        ComparisonResult ignore = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Different, strict.Status);
        DiffHighlightSpan origReordered = Assert.Single(strict.OriginalHighlights!);
        Assert.Equal(DiffChangeKind.Reordered, origReordered.Kind);
        Assert.Equal("PORT", resultSubstring(strict.NormalizedOriginal, origReordered));
        Assert.Equal(ComparisonStatus.Equivalent, ignore.Status);

        static string resultSubstring(string text, DiffHighlightSpan span) => text.Substring(span.Start, span.Length);
    }

    [Fact]
    public void Compare_AddedRemovedModifiedWithCrlfAndBom_ProducesExactNormalizedOutputSpans()
    {
        const string original = "\uFEFFexport DB_HOST=localhost\r\nOLD_FLAG=true # comment\r\nEMPTY_KEY=\r\n";
        const string modified = "DB_HOST=127.0.0.1\r\nNEW_FLAG=\"on\"\r\nEMPTY_KEY=filled\r\n";

        ComparisonResult result = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Different, result.Status);
        Assert.Equal(2, result.Summary!.ModifiedCount);
        Assert.Equal(1, result.Summary.RemovedCount);
        Assert.Equal(1, result.Summary.AddedCount);

        Assert.Contains(result.OriginalHighlights!, h =>
            h.Kind == DiffChangeKind.Modified && result.NormalizedOriginal.Substring(h.Start, h.Length) == "localhost");
        Assert.Contains(result.OriginalHighlights!, h =>
            h.Kind == DiffChangeKind.Modified && result.NormalizedOriginal.Substring(h.Start, h.Length) == "EMPTY_KEY");
        Assert.Contains(result.OriginalHighlights!, h =>
            h.Kind == DiffChangeKind.Removed && result.NormalizedOriginal.Substring(h.Start, h.Length) == "OLD_FLAG=true");

        Assert.Contains(result.ModifiedHighlights!, h =>
            h.Kind == DiffChangeKind.Modified && result.NormalizedModified.Substring(h.Start, h.Length) == "127.0.0.1");
        Assert.Contains(result.ModifiedHighlights!, h =>
            h.Kind == DiffChangeKind.Modified && result.NormalizedModified.Substring(h.Start, h.Length) == "filled");
        Assert.Contains(result.ModifiedHighlights!, h =>
            h.Kind == DiffChangeKind.Added && result.NormalizedModified.Substring(h.Start, h.Length) == "NEW_FLAG=on");
    }

    [Fact]
    public void Compare_MultilineAndEscapedQuotedValues_NormalizedEquivalently()
    {
        const string original = """
            PRIVATE_KEY="line1
            line2"
            HASH_VALUE="foo#bar"
            """;

        const string modified = """
            HASH_VALUE='foo#bar'
            PRIVATE_KEY="line1\nline2"
            """;

        ComparisonResult result = _sut.Compare(original, modified, OrderSensitivity.IgnoreOrder);

        Assert.Equal(ComparisonStatus.Equivalent, result.Status);
    }

    [Fact]
    public void Compare_DuplicateKeys_LastAssignmentWins()
    {
        const string original = """
            PORT=3000
            PORT=8080
            """;

        const string modified = "PORT=8080";

        ComparisonResult result = _sut.Compare(original, modified, OrderSensitivity.StrictOrder);

        Assert.Equal(ComparisonStatus.Equivalent, result.Status);
    }

    [Fact]
    public void Compare_InvalidEnvLine_ReturnsInvalidInput()
    {
        const string original = "NOT_AN_ASSIGNMENT";
        const string modified = "KEY=VALUE";

        ComparisonResult result = _sut.Compare(original, modified, OrderSensitivity.StrictOrder);

        Assert.Equal(ComparisonStatus.InvalidInput, result.Status);
        Assert.Contains("Original .env is invalid at line 1", result.ErrorMessage, StringComparison.Ordinal);
    }
}

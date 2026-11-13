namespace DevToys.StructuredCompare.Models;

public sealed record ComparisonResult(
    ComparisonStatus Status,
    ComparisonFormat DetectedFormat,
    string NormalizedOriginal,
    string NormalizedModified,
    string? ErrorMessage = null,
    IReadOnlyList<DiffHighlightSpan>? OriginalHighlights = null,
    IReadOnlyList<DiffHighlightSpan>? ModifiedHighlights = null,
    DiffSummary? Summary = null);

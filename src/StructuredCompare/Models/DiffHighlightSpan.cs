namespace DevToys.StructuredCompare.Models;

public sealed record DiffHighlightSpan(
    int Start,
    int Length,
    DiffChangeKind Kind,
    string Path,
    string Tooltip);

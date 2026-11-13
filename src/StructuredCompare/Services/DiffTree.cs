namespace DevToys.StructuredCompare.Services;

internal enum DiffNodeKind
{
    Mapping,
    Sequence,
    Scalar
}

internal readonly record struct DiffSpan(int Start, int Length)
{
    public static DiffSpan Empty => new(0, 0);

    public bool IsValid => Start >= 0 && Length > 0;

    public static DiffSpan FromBounds(string sourceText, int start, int end)
    {
        if (string.IsNullOrEmpty(sourceText))
        {
            return Empty;
        }

        int clampedStart = Math.Clamp(start, 0, sourceText.Length);
        int clampedEnd = Math.Clamp(end, clampedStart, sourceText.Length);

        while (clampedEnd > clampedStart && char.IsWhiteSpace(sourceText[clampedEnd - 1]))
        {
            clampedEnd--;
        }

        while (clampedStart < clampedEnd && char.IsWhiteSpace(sourceText[clampedStart]))
        {
            clampedStart++;
        }

        return clampedEnd > clampedStart
            ? new DiffSpan(clampedStart, clampedEnd - clampedStart)
            : Empty;
    }
}

internal sealed record DiffMappingEntry(
    string CanonicalKey,
    string DisplayKey,
    DiffSpan KeySpan,
    DiffSpan EntrySpan,
    DiffNode Value);

internal sealed record DiffNode(
    DiffNodeKind Kind,
    DiffSpan Span,
    string CanonicalValue,
    string DisplayPreview,
    IReadOnlyList<DiffMappingEntry> Entries,
    IReadOnlyList<DiffNode> Items);

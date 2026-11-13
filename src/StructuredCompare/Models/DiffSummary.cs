namespace DevToys.StructuredCompare.Models;

public sealed record DiffSummary(
    int AddedCount,
    int RemovedCount,
    int ModifiedCount,
    int ReorderedCount)
{
    public int TotalCount => AddedCount + RemovedCount + ModifiedCount + ReorderedCount;
}

using DevToys.StructuredCompare.Models;

namespace DevToys.StructuredCompare.Services;

public interface IStructureComparer
{
    ComparisonFormat Format { get; }

    ComparisonResult Compare(string? original, string? modified, OrderSensitivity orderSensitivity);
}

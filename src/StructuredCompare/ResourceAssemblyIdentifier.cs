using System.ComponentModel.Composition;
using DevToys.Api;

namespace DevToys.StructuredCompare;

[Export(typeof(IResourceAssemblyIdentifier))]
[Name(nameof(ResourceAssemblyIdentifier))]
public sealed class ResourceAssemblyIdentifier : IResourceAssemblyIdentifier
{
    public ValueTask<FontDefinition[]> GetFontDefinitionsAsync()
    {
        return new ValueTask<FontDefinition[]>(Array.Empty<FontDefinition>());
    }
}

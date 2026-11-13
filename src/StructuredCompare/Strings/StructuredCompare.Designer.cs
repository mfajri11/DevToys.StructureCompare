#nullable enable
using System.Globalization;
using System.Resources;

namespace DevToys.StructuredCompare.Strings;

public static class StructuredCompare
{
    private static ResourceManager? _resourceManager;

    public static ResourceManager ResourceManager =>
        _resourceManager ??= new ResourceManager(
            "DevToys.StructuredCompare.Strings.StructuredCompare",
            typeof(StructuredCompare).Assembly);

    public static CultureInfo? Culture { get; set; }

    public static string ShortDisplayTitle => GetString(nameof(ShortDisplayTitle));
    public static string LongDisplayTitle => GetString(nameof(LongDisplayTitle));
    public static string Description => GetString(nameof(Description));
    public static string AccessibleName => GetString(nameof(AccessibleName));
    public static string SearchKeywords => GetString(nameof(SearchKeywords));
    public static string FormatSettingTitle => GetString(nameof(FormatSettingTitle));
    public static string FormatSettingDescription => GetString(nameof(FormatSettingDescription));
    public static string FormatAutoDetect => GetString(nameof(FormatAutoDetect));
    public static string FormatYaml => GetString(nameof(FormatYaml));
    public static string FormatJson => GetString(nameof(FormatJson));
    public static string FormatEnv => GetString(nameof(FormatEnv));
    public static string OrderSensitivitySettingTitle => GetString(nameof(OrderSensitivitySettingTitle));
    public static string OrderSensitivitySettingDescription => GetString(nameof(OrderSensitivitySettingDescription));
    public static string OrderStrict => GetString(nameof(OrderStrict));
    public static string OrderIgnore => GetString(nameof(OrderIgnore));
    public static string DiffViewSettingTitle => GetString(nameof(DiffViewSettingTitle));
    public static string DiffViewSettingDescription => GetString(nameof(DiffViewSettingDescription));
    public static string DiffViewSplit => GetString(nameof(DiffViewSplit));
    public static string DiffViewInline => GetString(nameof(DiffViewInline));
    public static string OriginalInputTitle => GetString(nameof(OriginalInputTitle));
    public static string ModifiedInputTitle => GetString(nameof(ModifiedInputTitle));
    public static string OutputOriginalTitle => GetString(nameof(OutputOriginalTitle));
    public static string OutputModifiedTitle => GetString(nameof(OutputModifiedTitle));
    public static string DiffOutputTitle => GetString(nameof(DiffOutputTitle));
    public static string StatusEquivalentTitle => GetString(nameof(StatusEquivalentTitle));
    public static string StatusEquivalentDescription => GetString(nameof(StatusEquivalentDescription));
    public static string StatusDifferentTitle => GetString(nameof(StatusDifferentTitle));
    public static string StatusDifferentDescription => GetString(nameof(StatusDifferentDescription));
    public static string StatusDifferentSummaryDescription => GetString(nameof(StatusDifferentSummaryDescription));
    public static string StatusInvalidTitle => GetString(nameof(StatusInvalidTitle));

    private static string GetString(string name) =>
        ResourceManager.GetString(name, Culture) ?? name;
}

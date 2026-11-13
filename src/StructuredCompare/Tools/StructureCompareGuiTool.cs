using System.ComponentModel.Composition;
using System.Globalization;
using DevToys.Api;
using DevToys.StructuredCompare.Models;
using DevToys.StructuredCompare.Services;
using static DevToys.Api.GUI;

namespace DevToys.StructuredCompare.Tools;

[Export(typeof(IGuiTool))]
[Name("StructuredCompare")]
[ToolDisplayInformation(
    IconFontName = "FluentSystemIcons",
    IconGlyph = '\uE670',
    GroupName = PredefinedCommonToolGroupNames.Text,
    ResourceManagerAssemblyIdentifier = nameof(ResourceAssemblyIdentifier),
    ResourceManagerBaseName = "DevToys.StructuredCompare.Strings.StructuredCompare",
    ShortDisplayTitleResourceName = nameof(Strings.StructuredCompare.ShortDisplayTitle),
    LongDisplayTitleResourceName = nameof(Strings.StructuredCompare.LongDisplayTitle),
    DescriptionResourceName = nameof(Strings.StructuredCompare.Description),
    AccessibleNameResourceName = nameof(Strings.StructuredCompare.AccessibleName),
    SearchKeywordsResourceName = nameof(Strings.StructuredCompare.SearchKeywords))]
[AcceptedDataTypeName(PredefinedCommonDataTypeNames.Json)]
public sealed class StructureCompareGuiTool : IGuiTool
{
    private static readonly SettingDefinition<string> FormatSetting = new(
        name: $"{nameof(StructureCompareGuiTool)}.{nameof(FormatSetting)}",
        defaultValue: nameof(ComparisonFormat.AutoDetect));

    private static readonly SettingDefinition<string> OrderSensitivitySetting = new(
        name: $"{nameof(StructureCompareGuiTool)}.{nameof(OrderSensitivitySetting)}",
        defaultValue: nameof(OrderSensitivity.StrictOrder));

    private static readonly SettingDefinition<string> DiffViewModeSetting = new(
        name: $"{nameof(StructureCompareGuiTool)}.{nameof(DiffViewModeSetting)}",
        defaultValue: nameof(DiffViewMode.Split));

    private readonly ISettingsProvider _settingsProvider;
    private readonly StructureComparisonEngine _comparisonEngine = new();

    private readonly IUISelectDropDownList _formatDropDown = SelectDropDownList("structured-compare-format-dropdown");
    private readonly IUISelectDropDownList _orderDropDown = SelectDropDownList("structured-compare-order-dropdown");
    private readonly IUISelectDropDownList _diffViewDropDown = SelectDropDownList("structured-compare-diff-view-dropdown");

    private readonly IUIMultiLineTextInput _originalInput = MultiLineTextInput("structured-compare-original");
    private readonly IUIMultiLineTextInput _modifiedInput = MultiLineTextInput("structured-compare-modified");

    private readonly IUIMultiLineTextInput _outputOriginal = MultiLineTextInput("structured-compare-output-original");
    private readonly IUIMultiLineTextInput _outputModified = MultiLineTextInput("structured-compare-output-modified");
    private readonly IUIGrid _splitOutputGrid = Grid("structured-compare-split-output-grid");
    private readonly IUIMultiLineTextInput _outputInline = MultiLineTextInput("structured-compare-output-inline");
    private readonly IUIGrid _outputHostGrid = Grid("structured-compare-output-host-grid");

    private readonly IUIInfoBar _statusInfoBar = InfoBar("structured-compare-status");

    private UIToolView? _view;
    private ComparisonFormat _selectedFormat;
    private OrderSensitivity _selectedOrderSensitivity;
    private DiffViewMode _selectedDiffViewMode;
    private string _currentLanguage = string.Empty;
    private string _inlineLanguage = string.Empty;
    private string _originalText = string.Empty;
    private string _modifiedText = string.Empty;

    [ImportingConstructor]
    public StructureCompareGuiTool(ISettingsProvider settingsProvider)
    {
        _settingsProvider = settingsProvider;
        _selectedFormat = ReadEnumSetting(FormatSetting, ComparisonFormat.AutoDetect);
        _selectedOrderSensitivity = ReadEnumSetting(OrderSensitivitySetting, OrderSensitivity.StrictOrder);
        _selectedDiffViewMode = ReadEnumSetting(DiffViewModeSetting, DiffViewMode.Split);
    }

    public UIToolView View => _view ??= BuildView();

    public void OnDataReceived(string dataTypeName, object? parsedData)
    {
        if (parsedData is string text && !string.IsNullOrWhiteSpace(text))
        {
            _originalInput.Text(text);
        }
    }

    private UIToolView BuildView()
    {
        IUIDropDownListItem[] formatItems =
        [
            Item(Strings.StructuredCompare.FormatAutoDetect, ComparisonFormat.AutoDetect),
            Item(Strings.StructuredCompare.FormatYaml, ComparisonFormat.Yaml),
            Item(Strings.StructuredCompare.FormatJson, ComparisonFormat.Json),
            Item(Strings.StructuredCompare.FormatEnv, ComparisonFormat.Env)
        ];

        IUIDropDownListItem[] orderItems =
        [
            Item(Strings.StructuredCompare.OrderStrict, OrderSensitivity.StrictOrder),
            Item(Strings.StructuredCompare.OrderIgnore, OrderSensitivity.IgnoreOrder)
        ];

        IUIDropDownListItem[] diffViewItems =
        [
            Item(Strings.StructuredCompare.DiffViewSplit, DiffViewMode.Split),
            Item(Strings.StructuredCompare.DiffViewInline, DiffViewMode.Inline)
        ];

        _formatDropDown
            .WithItems(formatItems)
            .Select(FindItemIndex(formatItems, _selectedFormat))
            .OnItemSelected(OnFormatItemSelected);

        _orderDropDown
            .WithItems(orderItems)
            .Select(FindItemIndex(orderItems, _selectedOrderSensitivity))
            .OnItemSelected(OnOrderItemSelected);

        _diffViewDropDown
            .WithItems(diffViewItems)
            .Select(FindItemIndex(diffViewItems, _selectedDiffViewMode))
            .OnItemSelected(OnDiffViewItemSelected);

        _outputOriginal
            .Title(Strings.StructuredCompare.OutputOriginalTitle)
            .ReadOnly()
            .Extendable()
            .SynchronizeScrollBarWith(_outputModified);

        _outputModified
            .Title(Strings.StructuredCompare.OutputModifiedTitle)
            .ReadOnly()
            .Extendable()
            .SynchronizeScrollBarWith(_outputOriginal);

        _splitOutputGrid
            .Rows((OutputsGridRow.Row, new UIGridLength(1, UIGridUnitType.Fraction)))
            .Columns(
                (OutputsGridColumn.Original, new UIGridLength(0.5, UIGridUnitType.Fraction)),
                (OutputsGridColumn.Modified, new UIGridLength(0.5, UIGridUnitType.Fraction)))
            .ColumnSmallSpacing()
            .Cells(
                Cell(OutputsGridRow.Row, OutputsGridColumn.Original, _outputOriginal),
                Cell(OutputsGridRow.Row, OutputsGridColumn.Modified, _outputModified));

        _outputInline
            .Title(Strings.StructuredCompare.DiffOutputTitle)
            .ReadOnly()
            .Extendable();

        _outputHostGrid
            .Rows((OutputHostRow.Content, new UIGridLength(1, UIGridUnitType.Fraction)))
            .Columns((OutputHostColumn.Stretch, new UIGridLength(1, UIGridUnitType.Fraction)));

        ApplyDiffViewMode(_selectedDiffViewMode);
        ApplyEditorSyntaxLanguage(_selectedFormat, null);
        ApplyInlineSyntaxLanguage("plaintext");

        return new UIToolView(
            isScrollable: false,
            Grid()
                .Rows(
                    (GridRows.Settings, new UIGridLength(1, UIGridUnitType.Auto)),
                    (GridRows.Status, new UIGridLength(1, UIGridUnitType.Auto)),
                    (GridRows.Workspace, new UIGridLength(1, UIGridUnitType.Fraction)))
                .Columns(
                    (GridColumns.Stretch, new UIGridLength(1, UIGridUnitType.Fraction)))
                .RowSmallSpacing()
                .Cells(
                    Cell(
                        GridRows.Settings,
                        GridColumns.Stretch,
                        Grid()
                            .Rows((SettingsGridRow.Row, new UIGridLength(1, UIGridUnitType.Auto)))
                            .Columns(
                                (SettingsGridColumn.Format, new UIGridLength(1, UIGridUnitType.Fraction)),
                                (SettingsGridColumn.Order, new UIGridLength(1, UIGridUnitType.Fraction)),
                                (SettingsGridColumn.DiffView, new UIGridLength(1, UIGridUnitType.Fraction)))
                            .ColumnSmallSpacing()
                            .Cells(
                                Cell(
                                    SettingsGridRow.Row,
                                    SettingsGridColumn.Format,
                                    Setting("structured-compare-format-setting")
                                        .Title(Strings.StructuredCompare.FormatSettingTitle)
                                        .Description(Strings.StructuredCompare.FormatSettingDescription)
                                        .InteractiveElement(_formatDropDown)),
                                Cell(
                                    SettingsGridRow.Row,
                                    SettingsGridColumn.Order,
                                    Setting("structured-compare-order-setting")
                                        .Title(Strings.StructuredCompare.OrderSensitivitySettingTitle)
                                        .Description(Strings.StructuredCompare.OrderSensitivitySettingDescription)
                                        .InteractiveElement(_orderDropDown)),
                                Cell(
                                    SettingsGridRow.Row,
                                    SettingsGridColumn.DiffView,
                                    Setting("structured-compare-diff-view-setting")
                                        .Title(Strings.StructuredCompare.DiffViewSettingTitle)
                                        .Description(Strings.StructuredCompare.DiffViewSettingDescription)
                                        .InteractiveElement(_diffViewDropDown)))),
                    Cell(
                        GridRows.Status,
                        GridColumns.Stretch,
                        _statusInfoBar
                            .NonClosable()
                            .Close()),
                    Cell(
                        GridRows.Workspace,
                        GridColumns.Stretch,
                        Grid()
                            .Rows(
                                (WorkspaceRow.Inputs, new UIGridLength(0.45, UIGridUnitType.Fraction)),
                                (WorkspaceRow.Diff, new UIGridLength(0.55, UIGridUnitType.Fraction)))
                            .Columns(
                                (WorkspaceColumn.Stretch, new UIGridLength(1, UIGridUnitType.Fraction)))
                            .RowSmallSpacing()
                            .Cells(
                                Cell(
                                    WorkspaceRow.Inputs,
                                    WorkspaceColumn.Stretch,
                                    Grid()
                                        .Rows((InputsGridRow.Row, new UIGridLength(1, UIGridUnitType.Fraction)))
                                        .Columns(
                                            (InputsGridColumn.Original, new UIGridLength(0.5, UIGridUnitType.Fraction)),
                                            (InputsGridColumn.Modified, new UIGridLength(0.5, UIGridUnitType.Fraction)))
                                        .ColumnSmallSpacing()
                                        .Cells(
                                            Cell(
                                                InputsGridRow.Row,
                                                InputsGridColumn.Original,
                                                _originalInput
                                                    .Title(Strings.StructuredCompare.OriginalInputTitle)
                                                    .Extendable()
                                                    .OnTextChanged(OnOriginalTextChanged)),
                                            Cell(
                                                InputsGridRow.Row,
                                                InputsGridColumn.Modified,
                                                _modifiedInput
                                                    .Title(Strings.StructuredCompare.ModifiedInputTitle)
                                                    .Extendable()
                                                    .OnTextChanged(OnModifiedTextChanged)))),
                                Cell(
                                    WorkspaceRow.Diff,
                                    WorkspaceColumn.Stretch,
                                    _outputHostGrid)))));
    }

    private void OnOriginalTextChanged(string text)
    {
        _originalText = text;
        RunComparison();
    }

    private void OnModifiedTextChanged(string text)
    {
        _modifiedText = text;
        RunComparison();
    }

    private void OnFormatItemSelected(IUIDropDownListItem? item)
    {
        if (item?.Value is ComparisonFormat format && format != _selectedFormat)
        {
            _selectedFormat = format;
            SaveEnumSetting(FormatSetting, format);
            RunComparison();
        }
    }

    private void OnOrderItemSelected(IUIDropDownListItem? item)
    {
        if (item?.Value is OrderSensitivity order && order != _selectedOrderSensitivity)
        {
            _selectedOrderSensitivity = order;
            SaveEnumSetting(OrderSensitivitySetting, order);
            RunComparison();
        }
    }

    private void OnDiffViewItemSelected(IUIDropDownListItem? item)
    {
        if (item?.Value is DiffViewMode diffViewMode && diffViewMode != _selectedDiffViewMode)
        {
            _selectedDiffViewMode = diffViewMode;
            SaveEnumSetting(DiffViewModeSetting, diffViewMode);
            ApplyDiffViewMode(diffViewMode);
        }
    }

    private void ApplyDiffViewMode(DiffViewMode diffViewMode)
    {
        IUIElement activeOutput = diffViewMode == DiffViewMode.Inline
            ? _outputInline
            : _splitOutputGrid;

        _outputHostGrid.Cells(Cell(OutputHostRow.Content, OutputHostColumn.Stretch, activeOutput));
    }

    private void RunComparison()
    {
        try
        {
            ComparisonResult result = _comparisonEngine.Compare(
                _originalText,
                _modifiedText,
                _selectedFormat,
                _selectedOrderSensitivity);

            ApplyEditorSyntaxLanguage(_selectedFormat, result.DetectedFormat);

            _outputOriginal.Text(result.NormalizedOriginal);
            _outputModified.Text(result.NormalizedModified);

            (string inlineText, IReadOnlyList<DiffHighlightSpan> inlineHighlights) = InlineDiffFormatter.Build(result);
            ApplyInlineSyntaxLanguage(result.Status == ComparisonStatus.Equivalent ? _currentLanguage : "plaintext");
            _outputInline.Text(inlineText);

            ApplyOutputHighlights(result, inlineText, inlineHighlights);
            UpdateStatusBar(result, _selectedOrderSensitivity);
        }
        catch (Exception ex)
        {
            ClearOutputHighlights();
            _statusInfoBar
                .Error()
                .Title("Comparison Error")
                .Description(ex.Message)
                .Open();
        }
    }

    private void ApplyOutputHighlights(
        ComparisonResult result,
        string inlineText,
        IReadOnlyList<DiffHighlightSpan> inlineHighlights)
    {
        if (result.Status != ComparisonStatus.Different)
        {
            ClearOutputHighlights();
            return;
        }

        ApplyEditorHighlights(_outputOriginal, result.NormalizedOriginal, result.OriginalHighlights);
        ApplyEditorHighlights(_outputModified, result.NormalizedModified, result.ModifiedHighlights);
        ApplyEditorHighlights(_outputInline, inlineText, inlineHighlights);
    }

    private void ClearOutputHighlights()
    {
        _outputOriginal.Highlight();
        _outputOriginal.HoverTooltip();
        _outputModified.Highlight();
        _outputModified.HoverTooltip();
        _outputInline.Highlight();
        _outputInline.HoverTooltip();
    }

    private static void ApplyEditorHighlights(
        IUIMultiLineTextInput editor,
        string rawText,
        IReadOnlyList<DiffHighlightSpan>? spans)
    {
        if (spans is null || spans.Count == 0 || string.IsNullOrEmpty(rawText))
        {
            editor.Highlight();
            editor.HoverTooltip();
            return;
        }

        var highlightSpans = new List<UIHighlightedTextSpan>(spans.Count);
        var hoverTooltips = new List<UIHoverTooltip>(spans.Count);

        foreach (DiffHighlightSpan span in spans)
        {
            if (span.Start < 0 || span.Length <= 0 || span.Start + span.Length > rawText.Length)
            {
                continue;
            }

            highlightSpans.Add(new UIHighlightedTextSpan(span.Start, span.Length, MapHighlightColor(span.Kind)));
            if (!string.IsNullOrWhiteSpace(span.Tooltip))
            {
                hoverTooltips.Add(new UIHoverTooltip(span.Start, span.Length, span.Tooltip));
            }
        }

        editor.Highlight(highlightSpans.ToArray());
        editor.HoverTooltip(hoverTooltips.ToArray());
    }

    private static UIHighlightedTextSpanColor MapHighlightColor(DiffChangeKind kind) => kind switch
    {
        DiffChangeKind.Removed => UIHighlightedTextSpanColor.Red,
        DiffChangeKind.Added => UIHighlightedTextSpanColor.Green,
        DiffChangeKind.Modified => UIHighlightedTextSpanColor.Purple,
        DiffChangeKind.Reordered => UIHighlightedTextSpanColor.Blue,
        _ => UIHighlightedTextSpanColor.Default
    };

    private void ApplyEditorSyntaxLanguage(ComparisonFormat requestedFormat, ComparisonFormat? detectedFormat)
    {
        ComparisonFormat effective = requestedFormat == ComparisonFormat.AutoDetect
            ? detectedFormat ?? ComparisonFormat.Yaml
            : requestedFormat;

        string language = effective switch
        {
            ComparisonFormat.Json => "json",
            ComparisonFormat.Env => "plaintext",
            _ => "yaml"
        };

        if (string.Equals(_currentLanguage, language, StringComparison.Ordinal))
        {
            return;
        }

        _currentLanguage = language;
        _originalInput.Language(language);
        _modifiedInput.Language(language);
        _outputOriginal.Language(language);
        _outputModified.Language(language);
    }

    private void ApplyInlineSyntaxLanguage(string language)
    {
        if (string.Equals(_inlineLanguage, language, StringComparison.Ordinal))
        {
            return;
        }

        _inlineLanguage = language;
        _outputInline.Language(language);
    }

    private void UpdateStatusBar(ComparisonResult result, OrderSensitivity orderSensitivity)
    {
        string formatLabel = result.DetectedFormat switch
        {
            ComparisonFormat.Json => Strings.StructuredCompare.FormatJson,
            ComparisonFormat.Env => Strings.StructuredCompare.FormatEnv,
            _ => Strings.StructuredCompare.FormatYaml
        };

        string orderLabel = orderSensitivity == OrderSensitivity.IgnoreOrder
            ? Strings.StructuredCompare.OrderIgnore
            : Strings.StructuredCompare.OrderStrict;

        switch (result.Status)
        {
            case ComparisonStatus.Empty:
                _statusInfoBar.Close();
                break;

            case ComparisonStatus.Equivalent:
                _statusInfoBar
                    .Success()
                    .Title(string.Format(CultureInfo.CurrentCulture, Strings.StructuredCompare.StatusEquivalentTitle, formatLabel))
                    .Description(string.Format(CultureInfo.CurrentCulture, Strings.StructuredCompare.StatusEquivalentDescription, orderLabel))
                    .Open();
                break;

            case ComparisonStatus.Different:
                _statusInfoBar
                    .Warning()
                    .Title(string.Format(CultureInfo.CurrentCulture, Strings.StructuredCompare.StatusDifferentTitle, formatLabel))
                    .Description(FormatDifferenceDescription(result.Summary, orderLabel))
                    .Open();
                break;

            case ComparisonStatus.InvalidInput:
                _statusInfoBar
                    .Error()
                    .Title(string.Format(CultureInfo.CurrentCulture, Strings.StructuredCompare.StatusInvalidTitle, formatLabel))
                    .Description(result.ErrorMessage ?? string.Empty)
                    .Open();
                break;
        }
    }

    private static string FormatDifferenceDescription(DiffSummary? summary, string orderLabel)
    {
        if (summary is null || summary.TotalCount == 0)
        {
            return string.Format(CultureInfo.CurrentCulture, Strings.StructuredCompare.StatusDifferentDescription, orderLabel);
        }

        var breakdown = new List<string>(4);
        if (summary.AddedCount > 0)
        {
            breakdown.Add($"{summary.AddedCount} added");
        }

        if (summary.RemovedCount > 0)
        {
            breakdown.Add($"{summary.RemovedCount} removed");
        }

        if (summary.ModifiedCount > 0)
        {
            breakdown.Add($"{summary.ModifiedCount} modified");
        }

        if (summary.ReorderedCount > 0)
        {
            breakdown.Add($"{summary.ReorderedCount} reordered");
        }

        string countLabel = summary.TotalCount == 1
            ? "1 difference"
            : $"{summary.TotalCount} differences";

        return string.Format(
            CultureInfo.CurrentCulture,
            Strings.StructuredCompare.StatusDifferentSummaryDescription,
            countLabel,
            orderLabel,
            string.Join(", ", breakdown));
    }

    private TEnum ReadEnumSetting<TEnum>(SettingDefinition<string> definition, TEnum fallback)
        where TEnum : struct, Enum
    {
        try
        {
            string raw = _settingsProvider.GetSetting(definition);
            return Enum.TryParse(raw, ignoreCase: true, out TEnum parsed) ? parsed : fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private void SaveEnumSetting<TEnum>(SettingDefinition<string> definition, TEnum value)
        where TEnum : struct, Enum
    {
        try
        {
            _settingsProvider.SetSetting(definition, value.ToString());
        }
        catch
        {
            // Keep working in-memory even if settings persistence fails
        }
    }

    private static int FindItemIndex<TEnum>(IUIDropDownListItem[] items, TEnum target)
        where TEnum : struct, Enum
    {
        for (int i = 0; i < items.Length; i++)
        {
            if (items[i].Value is TEnum value && EqualityComparer<TEnum>.Default.Equals(value, target))
            {
                return i;
            }
        }

        return 0;
    }

    private enum GridRows
    {
        Settings,
        Status,
        Workspace
    }

    private enum GridColumns
    {
        Stretch
    }

    private enum SettingsGridRow
    {
        Row
    }

    private enum SettingsGridColumn
    {
        Format,
        Order,
        DiffView
    }

    private enum WorkspaceRow
    {
        Inputs,
        Diff
    }

    private enum WorkspaceColumn
    {
        Stretch
    }

    private enum InputsGridRow
    {
        Row
    }

    private enum InputsGridColumn
    {
        Original,
        Modified
    }

    private enum OutputsGridRow
    {
        Row
    }

    private enum OutputsGridColumn
    {
        Original,
        Modified
    }

    private enum OutputHostRow
    {
        Content
    }

    private enum OutputHostColumn
    {
        Stretch
    }
}

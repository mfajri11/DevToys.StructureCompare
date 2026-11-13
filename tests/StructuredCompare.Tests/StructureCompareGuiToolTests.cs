using System.Reflection;
using DevToys.Api;
using DevToys.StructuredCompare.Tools;

namespace StructuredCompare.Tests;

public sealed class StructureCompareGuiToolTests
{
    [Fact]
    public void Tool_IsPlacedUnderTextGroup()
    {
        ToolDisplayInformationAttribute? info = typeof(StructureCompareGuiTool)
            .GetCustomAttribute<ToolDisplayInformationAttribute>();

        Assert.NotNull(info);
        Assert.Equal(PredefinedCommonToolGroupNames.Text, info!.GroupName);
    }

    [Fact]
    public void Tool_HighlightsSplitAndInlineOutputs_UsesPurpleForConflicts_AndNeverHighlightsInputs()
    {
        var settingsProvider = new InMemorySettingsProvider();
        var tool = new StructureCompareGuiTool(settingsProvider);

        UIToolView view = tool.View;
        Assert.Same(view, tool.View);

        var rootGrid = Assert.IsAssignableFrom<IUIGrid>(view.RootElement);
        var settingsGrid = Assert.IsAssignableFrom<IUIGrid>(rootGrid.Cells!.First().Child);
        var statusBar = Assert.IsAssignableFrom<IUIInfoBar>(rootGrid.Cells!.ElementAt(1).Child);
        var workspaceGrid = Assert.IsAssignableFrom<IUIGrid>(rootGrid.Cells!.Last().Child);
        var inputsGrid = Assert.IsAssignableFrom<IUIGrid>(workspaceGrid.Cells!.First().Child);
        var outputHostGrid = Assert.IsAssignableFrom<IUIGrid>(workspaceGrid.Cells!.Last().Child);

        var originalInput = Assert.IsAssignableFrom<IUIMultiLineTextInput>(inputsGrid.Cells!.First().Child);
        var modifiedInput = Assert.IsAssignableFrom<IUIMultiLineTextInput>(inputsGrid.Cells!.Last().Child);

        var splitOutputGrid = Assert.IsAssignableFrom<IUIGrid>(outputHostGrid.Cells!.Single().Child);
        var outputOriginal = Assert.IsAssignableFrom<IUIMultiLineTextInput>(splitOutputGrid.Cells!.First().Child);
        var outputModified = Assert.IsAssignableFrom<IUIMultiLineTextInput>(splitOutputGrid.Cells!.Last().Child);

        originalInput.Text("""
            # SERVICE
              - name: B_HOST
                value: "b:9090"
              - name: A_HOST
                value: "a:9090"
            """);

        modifiedInput.Text("""
              - name: A_HOST
                value: "a:9090"
              - name: B_HOST
                value: "b:9999"
            """);

        IUISelectDropDownList formatDropDown = FindDropDown(settingsGrid, "structured-compare-format-setting");
        IUISelectDropDownList orderDropDown = FindDropDown(settingsGrid, "structured-compare-order-setting");
        IUISelectDropDownList diffViewDropDown = FindDropDown(settingsGrid, "structured-compare-diff-view-setting");

        // Switch format across JSON, ENV, YAML
        formatDropDown.Select(2); // JSON -> invalid JSON clears output highlights
        Assert.Empty(originalInput.HighlightedSpans);
        Assert.Empty(modifiedInput.HighlightedSpans);
        Assert.Empty(outputOriginal.HighlightedSpans);
        Assert.Empty(outputModified.HighlightedSpans);

        formatDropDown.Select(3); // .env -> invalid .env clears output highlights & sets plaintext language
        Assert.Equal("plaintext", originalInput.SyntaxColorizationLanguageName);
        Assert.Equal("plaintext", outputOriginal.SyntaxColorizationLanguageName);
        Assert.Empty(originalInput.HighlightedSpans);
        Assert.Empty(outputOriginal.HighlightedSpans);

        formatDropDown.Select(1); // YAML -> Strict Order highlights reordered + conflict (Purple) on OUTPUT only
        Assert.Equal("yaml", originalInput.SyntaxColorizationLanguageName);
        Assert.Equal("yaml", outputOriginal.SyntaxColorizationLanguageName);
        Assert.Empty(originalInput.HighlightedSpans);
        Assert.Empty(modifiedInput.HighlightedSpans);
        Assert.Empty(originalInput.HoverTooltips);
        Assert.Empty(modifiedInput.HoverTooltips);

        Assert.Contains(outputOriginal.HighlightedSpans, s => s.Color == UIHighlightedTextSpanColor.Blue);
        Assert.Contains(outputOriginal.HighlightedSpans, s => s.Color == UIHighlightedTextSpanColor.Purple);
        Assert.Contains(outputModified.HighlightedSpans, s => s.Color == UIHighlightedTextSpanColor.Purple);
        Assert.DoesNotContain(outputOriginal.HighlightedSpans, s => s.Color == UIHighlightedTextSpanColor.Yellow);
        Assert.NotEmpty(outputOriginal.HoverTooltips);
        Assert.Contains("reordered", statusBar.Description, StringComparison.Ordinal);

        // Toggle Diff View to Inline -> Inline output also has highlights & tooltips using Purple for conflict
        diffViewDropDown.Select(1); // Inline
        var inlineOutput = Assert.IsAssignableFrom<IUIMultiLineTextInput>(outputHostGrid.Cells!.Single().Child);
        Assert.True(inlineOutput.IsReadOnly);
        Assert.NotEmpty(inlineOutput.HighlightedSpans);
        Assert.NotEmpty(inlineOutput.HoverTooltips);
        Assert.Contains(inlineOutput.HighlightedSpans, s => s.Color == UIHighlightedTextSpanColor.Purple);
        Assert.DoesNotContain(inlineOutput.HighlightedSpans, s => s.Color == UIHighlightedTextSpanColor.Yellow);

        // Switch to Ignore Order while in Inline -> only value conflict remains (Purple), no Blue reorder
        orderDropDown.Select(1); // Ignore Order
        Assert.Contains(inlineOutput.HighlightedSpans, s => s.Color == UIHighlightedTextSpanColor.Purple);
        Assert.DoesNotContain(inlineOutput.HighlightedSpans, s => s.Color == UIHighlightedTextSpanColor.Blue);

        // Toggle back to Split View
        diffViewDropDown.Select(0); // Split
        Assert.Same(splitOutputGrid, outputHostGrid.Cells!.Single().Child);

        // Make inputs equivalent -> all output highlights cleared
        modifiedInput.Text("""
              - name: A_HOST
                value: "a:9090"
              - name: B_HOST
                value: "b:9090"
            """);
        Assert.Equal(outputOriginal.Text, outputModified.Text);
        Assert.Empty(originalInput.HighlightedSpans);
        Assert.Empty(modifiedInput.HighlightedSpans);
        Assert.Empty(outputOriginal.HighlightedSpans);
        Assert.Empty(outputModified.HighlightedSpans);
        Assert.Empty(inlineOutput.HighlightedSpans);
        Assert.Empty(inlineOutput.HoverTooltips);
    }

    private static IUISelectDropDownList FindDropDown(IUIGrid settingsGrid, string settingId)
    {
        IUISetting setting = settingsGrid.Cells!
            .Select(static c => c.Child)
            .OfType<IUISetting>()
            .Single(s => s.Id == settingId);

        return Assert.IsAssignableFrom<IUISelectDropDownList>(setting.InteractiveElement);
    }

    private sealed class InMemorySettingsProvider : ISettingsProvider
    {
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public event EventHandler<SettingChangedEventArgs>? SettingChanged;

        public T GetSetting<T>(SettingDefinition<T> settingDefinition)
        {
            return _values.TryGetValue(settingDefinition.Name, out object? value) && value is T typed
                ? typed
                : settingDefinition.DefaultValue;
        }

        public void SetSetting<T>(SettingDefinition<T> settingDefinition, T value)
        {
            _values[settingDefinition.Name] = value;
            SettingChanged?.Invoke(this, new SettingChangedEventArgs(settingDefinition.Name, value));
        }

        public void ResetSetting<T>(SettingDefinition<T> settingDefinition)
        {
            _values.Remove(settingDefinition.Name);
        }
    }
}

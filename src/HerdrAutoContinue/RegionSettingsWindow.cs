using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Watchdog.Core;

namespace HerdrAutoContinue;

public sealed class RegionSettingsWindow : Window
{
    private readonly MainViewModel model;
    private readonly RegionSelectionDraft draft;
    private readonly StackPanel rows = new() { Spacing = 5 };
    private readonly TextBlock summary = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray };
    private readonly TextBlock error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed };
    private readonly Button apply = new() { Content = "应用并保存", Background = Brush.Parse("#087F8C"), Foreground = Brushes.White };
    private readonly Button cancel = new() { Content = "取消" }, refresh = new() { Content = "刷新地区" };
    private readonly CancellationTokenSource lifetime = new();
    private bool saving, refreshing, rebuilding;
    private string catalogueSignature = "";

    public RegionSettingsWindow(MainViewModel model)
    {
        this.model = model; draft = new(model.RegionPreferences);
        Title = "地区设置"; Width = 600; Height = 550; MinWidth = 500; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        AutomationProperties.SetAutomationId(this, "region-settings-window");
        var root = new Grid { Margin = new(20), RowDefinitions = new("Auto,Auto,Auto,*,Auto,Auto"), RowSpacing = 12 };
        root.Children.Add(new TextBlock { Text = "选择恢复地区与优先级", FontSize = 21, FontWeight = FontWeight.SemiBold });
        Grid.SetRow(summary, 1); root.Children.Add(summary); AutomationProperties.SetAutomationId(summary, "region-catalogue-summary");
        var help = new TextBlock { Text = "先尝试当前已选地区，失败后从排序首位继续，跳过本轮已失败地区。\n勾选地区后可上下调整优先级；未选择地区时只检查代理，不自动切换。", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Gray };
        Grid.SetRow(help, 2); root.Children.Add(help);
        var scroll = new ScrollViewer { Content = rows, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        Grid.SetRow(scroll, 3); root.Children.Add(scroll);
        Grid.SetRow(error, 4); root.Children.Add(error);
        var actions = new Grid { ColumnDefinitions = new("*,Auto,Auto"), ColumnSpacing = 10 };
        actions.Children.Add(refresh); Grid.SetColumn(cancel, 1); actions.Children.Add(cancel); Grid.SetColumn(apply, 2); actions.Children.Add(apply);
        Grid.SetRow(actions, 5); root.Children.Add(actions); Content = root;
        AutomationProperties.SetAutomationId(refresh, "region-refresh"); AutomationProperties.SetAutomationId(cancel, "region-cancel"); AutomationProperties.SetAutomationId(apply, "region-apply");
        cancel.Click += (_, _) => Close();
        refresh.Click += async (_, _) => await RefreshAsync();
        apply.Click += async (_, _) => await ApplyAsync();
        model.PropertyChanged += ModelChanged;
        Opened += async (_, _) => await RefreshAsync();
        Closing += (_, e) => { if (saving) e.Cancel = true; };
        Closed += (_, _) => { model.PropertyChanged -= ModelChanged; lifetime.Cancel(); lifetime.Dispose(); };
        RebuildRows();
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        summary.Text = model.RegionSummary;
        var signature = string.Join('|', model.RegionStatus.Regions.Select(r => $"{r.Region.Id}:{r.NodeCount}"));
        if (signature != catalogueSignature) RebuildRows();
    }
    private void RebuildRows()
    {
        rebuilding = true;
        try
        {
            summary.Text = model.RegionSummary;
            catalogueSignature = string.Join('|', model.RegionStatus.Regions.Select(r => $"{r.Region.Id}:{r.NodeCount}"));
            rows.Children.Clear();
            foreach (var availability in draft.Rows(model.RegionStatus.Regions))
            {
                var region = availability.Region; var selected = draft.Selected.Contains(region.Id); var index = draft.Selected.ToList().IndexOf(region.Id);
                var row = new Grid { ColumnDefinitions = new("*,80,45,45"), ColumnSpacing = 8, Margin = new(0, 3) };
                var check = new CheckBox { Content = region.Flag + " " + region.Name, IsChecked = selected, IsEnabled = !saving };
                AutomationProperties.SetAutomationId(check, "region-check-" + region.Id);
                check.IsCheckedChanged += (_, _) => { if (rebuilding) return; draft.SetSelected(region.Id, check.IsChecked == true); RebuildRows(); };
                row.Children.Add(check);
                var count = new TextBlock { Text = availability.NodeCount + " 个节点", VerticalAlignment = VerticalAlignment.Center, Foreground = availability.NodeCount == 0 ? Brushes.DarkOrange : Brushes.Gray };
                Grid.SetColumn(count, 1); row.Children.Add(count);
                var up = new Button { Content = "↑", IsEnabled = !saving && selected && index > 0, HorizontalContentAlignment = HorizontalAlignment.Center };
                var down = new Button { Content = "↓", IsEnabled = !saving && selected && index < draft.Selected.Count - 1, HorizontalContentAlignment = HorizontalAlignment.Center };
                AutomationProperties.SetAutomationId(up, "region-up-" + region.Id); AutomationProperties.SetAutomationId(down, "region-down-" + region.Id);
                ToolTip.SetTip(up, "提高优先级"); ToolTip.SetTip(down, "降低优先级");
                up.Click += (_, _) => { draft.Move(region.Id, -1); RebuildRows(); }; down.Click += (_, _) => { draft.Move(region.Id, 1); RebuildRows(); };
                Grid.SetColumn(up, 2); row.Children.Add(up); Grid.SetColumn(down, 3); row.Children.Add(down); rows.Children.Add(row);
            }
        }
        finally { rebuilding = false; }
    }
    private async Task RefreshAsync()
    {
        if (refreshing || saving) return;
        refreshing = true; refresh.IsEnabled = false;
        try { await model.RefreshRegionsAsync(lifetime.Token); RebuildRows(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        finally { refreshing = false; refresh.IsEnabled = !saving; }
    }
    private async Task ApplyAsync()
    {
        if (saving) return;
        saving = true; apply.IsEnabled = false; cancel.IsEnabled = false; refresh.IsEnabled = false; error.Text = ""; RebuildRows();
        try { await model.ApplyRegionsAsync(draft.ToPreferences(), lifetime.Token); saving = false; Close(); }
        catch (Exception e) { error.Text = "保存失败，当前设置保持不变：" + e.Message; }
        finally { saving = false; apply.IsEnabled = true; cancel.IsEnabled = true; refresh.IsEnabled = !refreshing; RebuildRows(); }
    }
}

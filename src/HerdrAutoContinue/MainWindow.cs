using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Input;
using Avalonia.Media;

namespace HerdrAutoContinue;

public sealed class MainWindow : Window
{
    private const string Accent = "AppAccent", Secondary = "AppSecondary";
    private readonly MainViewModel model;
    private readonly Func<Task> quit;
    public MainWindow(MainViewModel model, Func<bool> isExiting, Func<Task> openSettings, Func<Task> quit)
    {
        this.quit = quit;
        this.model = model; DataContext = model;
        Title = "Herdr Auto Continue"; Width = 1080; Height = 850; MinWidth = 900; MinHeight = 720;
        using (var icon = typeof(MainWindow).Assembly.GetManifestResourceStream("HerdrAutoContinue.AppIcon.ico"))
            if (icon is not null) Icon = new WindowIcon(icon);
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control), Command = model.ToggleCommand });
        Content = BuildContent(openSettings);
        Closing += (_, e) =>
        {
            if (isExiting()) return;
            e.Cancel = true; Hide();
        };
    }
    internal Task ExitAsync() => quit();

    private Control BuildContent(Func<Task> openSettings)
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto,170,Auto") };
        var heading = new Grid { ColumnDefinitions = new("64,*,Auto"), Margin = new(24, 20) };
        heading.Children.Add(Text("↻", 47, color: Accent));
        Add(heading, new StackPanel { Spacing = 4, Children = { Text("Herdr 自动续接", 25, FontWeight.SemiBold), Text("按地区优先级恢复代理，连通后续接 agent", 13, color: Secondary) } }, column: 1);
        var toggle = new Button { Width = 132, Height = 42, Classes = { "accent" }, HorizontalContentAlignment = HorizontalAlignment.Center };
        toggle.Bind(ContentControl.ContentProperty, new Binding(nameof(MainViewModel.ToggleText)));
        toggle.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanToggle)));
        AutomationProperties.SetAutomationId(toggle, "service-toggle");
        toggle.Click += async (_, _) => await model.ToggleAsync();
        var settings = new Button { Content = "设置", Height = 42, Padding = new(16, 6) };
        settings.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanToggle)));
        AutomationProperties.SetAutomationId(settings, "app-settings"); settings.Click += async (_, _) => await openSettings();
        Add(heading, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { settings, toggle } }, column: 2);
        Add(root, heading);

        var summary = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(24, 12) };
        var summaryText = Bound(nameof(MainViewModel.Summary), 13, Accent); AutomationProperties.SetAutomationId(summaryText, "service-state");
        summary.Children.Add(summaryText); Add(summary, Text("独立冷却 10 秒", 13, color: Secondary), column: 1);
        Add(root, ThemedBorder(new Border { Child = summary }, "AppSummary"), row: 1);

        var proxy = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto"), ColumnDefinitions = new("*,Auto"), RowSpacing = 7 };
        var proxyTitle = Bound(nameof(MainViewModel.ProxyTitle), 14, Accent); proxyTitle.FontWeight = FontWeight.SemiBold; AutomationProperties.SetAutomationId(proxyTitle, "proxy-status");
        proxy.Children.Add(proxyTitle); Add(proxy, Bound(nameof(MainViewModel.ProxyNumbers), 12, Secondary), column: 1);
        var selectedNode = Bound(nameof(MainViewModel.SelectedNode), 13);
        AutomationProperties.SetAutomationId(selectedNode, "selected-proxy-node"); Add(proxy, selectedNode, row: 1);
        var results = new Button { Content = "测速结果", Padding = new(8, 3), FontSize = 12 };
        results.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.HasDelays))); AutomationProperties.SetAutomationId(results, "measurement-results");
        results.Click += (_, _) => ShowDelays(); Add(proxy, results, row: 1, column: 1);
        var proxyDetail = Bound(nameof(MainViewModel.ProxyDetail), 12, Secondary); proxyDetail.TextWrapping = TextWrapping.Wrap;
        Add(proxy, proxyDetail, row: 2); Add(proxy, Bound(nameof(MainViewModel.ProxyCountdown), 12, Secondary), row: 2, column: 1);
        var regionOrder = Bound(nameof(MainViewModel.RegionOrder), 12, Secondary);
        AutomationProperties.SetAutomationId(regionOrder, "region-order");
        regionOrder.Bind(ToolTip.TipProperty, new Binding(nameof(MainViewModel.RegionOrder))); Add(proxy, regionOrder, row: 3);
        var regionSettings = new Button { Content = "切换设置", Padding = new(8, 3), FontSize = 12 };
        regionSettings.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanToggle)));
        AutomationProperties.SetAutomationId(regionSettings, "region-settings");
        regionSettings.Click += async (_, _) => await new RegionSettingsWindow(model).ShowDialog(this); Add(proxy, regionSettings, row: 3, column: 1);
        Add(root, ThemedBorder(new Border { Margin = new(24, 16, 24, 0), Padding = new(14), CornerRadius = new(10), BorderThickness = new(1), Child = proxy }, "AppPanel"), row: 2);

        var connection = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(24, 12) };
        var status = Bound(nameof(MainViewModel.Connection), 12, Secondary); AutomationProperties.SetAutomationId(status, "connection-status");
        connection.Children.Add(status); Add(connection, Bound(nameof(MainViewModel.UpdatedAt), 12, Secondary), column: 1); Add(root, connection, row: 3);

        var agentsArea = new Grid { RowDefinitions = new("Auto,*"), Margin = new(24, 0) };
        var columns = new Grid { ColumnDefinitions = new("2.2*,80,2.5*,50"), Margin = new(12, 8) };
        foreach (var (label, index) in new[] { ("Agent", 0), ("状态", 1), ("监控结果", 2), ("续接", 3) }) Add(columns, Text(label, 12, FontWeight.SemiBold, Secondary), column: index);
        agentsArea.Children.Add(columns);
        var list = new ItemsControl { ItemsSource = model.Rows, ItemTemplate = new FuncDataTemplate<AgentRowViewModel>((_, _) => AgentView()) };
        AutomationProperties.SetAutomationId(list, "agent-list");
        Add(agentsArea, new ScrollViewer { Content = list, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, row: 1);
        var empty = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Spacing = 10 };
        empty.Children.Add(Bound(nameof(MainViewModel.EmptyTitle), 22, Secondary)); empty.Children.Add(Bound(nameof(MainViewModel.EmptyDescription), 13, Secondary));
        empty.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.HasNoRows))); Add(agentsArea, empty, row: 1);
        Add(root, agentsArea, row: 4);

        var logHeading = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(24, 10) };
        logHeading.Children.Add(Text("操作日志", 13, FontWeight.SemiBold));
        var openLog = new Button { Content = "打开日志文件", FontSize = 12, Padding = new(8, 4) }; openLog.Click += (_, _) => model.RevealLog();
        AutomationProperties.SetAutomationId(openLog, "open-log"); Add(logHeading, openLog, column: 1);
        Add(root, ThemedBorder(new Border { BorderThickness = new(0, 1, 0, 0), Child = logHeading }), row: 5);
        var events = new ItemsControl { ItemsSource = model.Events, ItemTemplate = new FuncDataTemplate<Watchdog.Core.MonitorEvent>((value, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("70,145,*"), Margin = new(0, 3) };
            row.Children.Add(Text(value?.Date.ToLocalTime().ToString("HH:mm:ss") ?? "", 11, color: Secondary));
            Add(row, Text(value?.Target ?? "", 11, color: Secondary), column: 1);
            var message = Text(value?.Message ?? "", 11); message.TextWrapping = TextWrapping.Wrap; Add(row, message, column: 2); return row;
        }) };
        AutomationProperties.SetAutomationId(events, "operation-log");
        Add(root, new ScrollViewer { Content = events, Margin = new(24, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, row: 6);
        var footer = new Grid { Margin = new(24, 12), ColumnDefinitions = new("*,Auto"), ColumnSpacing = 10 };
        var help = Text("ⓘ  关闭窗口隐藏到托盘。正常完成、人工中断和等待确认的对话不会自动续接。", 12, color: Secondary);
        help.TextWrapping = TextWrapping.Wrap; footer.Children.Add(help);
        var exit = new Button { Content = "退出应用", FontSize = 12, Padding = new(8, 4) };
        AutomationProperties.SetAutomationId(exit, "app-exit"); exit.Click += async (_, _) => await ExitAsync();
        Grid.SetColumn(exit, 1); footer.Children.Add(exit); Add(root, footer, row: 7);
        return root;
    }

    private static Control AgentView()
    {
        var grid = new Grid { ColumnDefinitions = new("2.2*,80,2.5*,50"), Margin = new(12, 11), ColumnSpacing = 8 };
        var title = Bound(nameof(AgentRowViewModel.Title), 13); title.FontWeight = FontWeight.SemiBold;
        title.Bind(ToolTip.TipProperty, new Binding(nameof(AgentRowViewModel.Title)));
        var location = Bound(nameof(AgentRowViewModel.Location), 11, Secondary);
        grid.Children.Add(new StackPanel { Spacing = 4, Children = { title, location } });
        Add(grid, Bound(nameof(AgentRowViewModel.State), 12, Secondary), column: 1);
        var detail = Bound(nameof(AgentRowViewModel.Detail), 12); detail.TextWrapping = TextWrapping.Wrap;
        detail.Bind(ToolTip.TipProperty, new Binding(nameof(AgentRowViewModel.Detail)));
        Add(grid, new StackPanel { Spacing = 4, Children = { detail, Bound(nameof(AgentRowViewModel.Countdown), 11, "AppWarning") } }, column: 2);
        Add(grid, Bound(nameof(AgentRowViewModel.Attempts), 13), column: 3);
        return ThemedBorder(new Border { BorderThickness = new(0, 0, 0, 1), Child = grid });
    }
    internal Window ShowDelays()
    {
        var window = new Window { Title = model.DelayTitle, Width = 530, Height = 330, MinWidth = 420, MinHeight = 240,
            Content = new StackPanel { Margin = new(20), Spacing = 12, Children = { Text(model.DelayTitle, 18, FontWeight.SemiBold),
                new ScrollViewer { MaxHeight = 210, Content = new ItemsControl { ItemsSource = model.Delays } }, Text("同地区全部并行测速；连续两次超过 800 ms 视为超时。", 12, color: Secondary) } } };
        window.ShowDialog(this);
        return window;
    }
    private static TextBlock Text(string value, double size, FontWeight? weight = null, string? color = null)
    {
        var text = new TextBlock { Text = value, FontSize = size, FontWeight = weight ?? FontWeight.Normal, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        AppThemeManager.Bind(text, TextBlock.ForegroundProperty, color ?? "AppForeground"); return text;
    }
    private static Border ThemedBorder(Border border, string? background = null)
    {
        AppThemeManager.Bind(border, Border.BorderBrushProperty, "AppBorder");
        if (background is not null) AppThemeManager.Bind(border, Border.BackgroundProperty, background);
        return border;
    }
    private static TextBlock Bound(string property, double size, string? color = null)
    { var text = Text("", size, color: color); text.Bind(TextBlock.TextProperty, new Binding(property)); return text; }
    private static void Add(Grid grid, Control child, int row = 0, int column = 0) { Grid.SetRow(child, row); Grid.SetColumn(child, column); grid.Children.Add(child); }
}

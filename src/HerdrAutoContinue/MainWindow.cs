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
    private static readonly IBrush Teal = Brush.Parse("#087F8C"), Secondary = Brush.Parse("#667685");
    private readonly MainViewModel model;
    private bool closing;
    public MainWindow(MainViewModel model)
    {
        this.model = model; DataContext = model;
        Title = "Herdr Auto Continue"; Width = 1080; Height = 850; MinWidth = 900; MinHeight = 720;
        using (var icon = typeof(MainWindow).Assembly.GetManifestResourceStream("HerdrAutoContinue.AppIcon.ico"))
            if (icon is not null) Icon = new WindowIcon(icon);
        Background = Brush.Parse("#FAFCFD");
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.Enter, OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control), Command = model.ToggleCommand });
        Content = BuildContent();
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true;
            IsEnabled = false;
            await model.StopAsync();
            closing = true; Close();
        };
        Closed += async (_, _) => await model.DisposeAsync();
    }

    private Control BuildContent()
    {
        var root = new Grid { RowDefinitions = new("Auto,Auto,Auto,Auto,*,Auto,170,Auto") };
        var heading = new Grid { ColumnDefinitions = new("64,*,Auto"), Margin = new(24, 20) };
        heading.Children.Add(new TextBlock { Text = "↻", FontSize = 47, Foreground = Teal, VerticalAlignment = VerticalAlignment.Center });
        Add(heading, new StackPanel { Spacing = 4, Children = { Text("Herdr 自动续接", 25, FontWeight.SemiBold), Text("代理故障自动切换日本节点，恢复后续接 agent", 13, color: Secondary) } }, column: 1);
        var toggle = new Button { Width = 132, Height = 42, Background = Teal, Foreground = Brushes.White, HorizontalContentAlignment = HorizontalAlignment.Center };
        toggle.Bind(ContentControl.ContentProperty, new Binding(nameof(MainViewModel.ToggleText)));
        toggle.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanToggle)));
        AutomationProperties.SetAutomationId(toggle, "service-toggle");
        toggle.Click += async (_, _) => await model.ToggleAsync();
        Add(heading, toggle, column: 2);
        Add(root, heading);

        var summary = new Grid { ColumnDefinitions = new("*,Auto"), Margin = new(24, 12) };
        var summaryText = Bound(nameof(MainViewModel.Summary), 13, Teal); AutomationProperties.SetAutomationId(summaryText, "service-state");
        summary.Children.Add(summaryText); Add(summary, Text("独立冷却 10 秒", 13, color: Secondary), column: 1);
        Add(root, new Border { Background = Brush.Parse("#EAF5F5"), Child = summary }, row: 1);

        var proxy = new Grid { RowDefinitions = new("Auto,Auto,Auto"), ColumnDefinitions = new("*,Auto"), RowSpacing = 7 };
        var proxyTitle = Bound(nameof(MainViewModel.ProxyTitle), 14, Teal); proxyTitle.FontWeight = FontWeight.SemiBold; AutomationProperties.SetAutomationId(proxyTitle, "proxy-status");
        proxy.Children.Add(proxyTitle); Add(proxy, Bound(nameof(MainViewModel.ProxyNumbers), 12, Secondary), column: 1);
        Add(proxy, Bound(nameof(MainViewModel.SelectedNode), 13), row: 1);
        var results = new Button { Content = "测速结果", Padding = new(8, 3), FontSize = 12 };
        results.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.HasDelays))); AutomationProperties.SetAutomationId(results, "measurement-results");
        results.Click += (_, _) => ShowDelays(); Add(proxy, results, row: 1, column: 1);
        var proxyDetail = Bound(nameof(MainViewModel.ProxyDetail), 12, Secondary); proxyDetail.TextWrapping = TextWrapping.Wrap;
        Add(proxy, proxyDetail, row: 2); Add(proxy, Bound(nameof(MainViewModel.ProxyCountdown), 12, Secondary), row: 2, column: 1);
        Add(root, new Border { Margin = new(24, 16, 24, 0), Padding = new(14), CornerRadius = new(10), Background = Brush.Parse("#F0F8F7"), BorderBrush = Brush.Parse("#C9E3E0"), BorderThickness = new(1), Child = proxy }, row: 2);

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
        Add(root, new Border { BorderBrush = Brush.Parse("#DEE6EC"), BorderThickness = new(0, 1, 0, 0), Child = logHeading }, row: 5);
        var events = new ItemsControl { ItemsSource = model.Events, ItemTemplate = new FuncDataTemplate<Watchdog.Core.MonitorEvent>((value, _) =>
        {
            var row = new Grid { ColumnDefinitions = new("70,145,*"), Margin = new(0, 3) };
            row.Children.Add(Text(value?.Date.ToLocalTime().ToString("HH:mm:ss") ?? "", 11, color: Secondary));
            Add(row, Text(value?.Target ?? "", 11, color: Secondary), column: 1);
            var message = Text(value?.Message ?? "", 11); message.TextWrapping = TextWrapping.Wrap; Add(row, message, column: 2); return row;
        }) };
        AutomationProperties.SetAutomationId(events, "operation-log");
        Add(root, new ScrollViewer { Content = events, Margin = new(24, 0), HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled }, row: 6);
        var footer = Text("ⓘ  关闭窗口即停止服务。正常完成、人工中断和等待确认的对话不会自动续接。", 12, color: Secondary);
        footer.Margin = new(24, 12); footer.TextWrapping = TextWrapping.Wrap; Add(root, footer, row: 7);
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
        Add(grid, new StackPanel { Spacing = 4, Children = { detail, Bound(nameof(AgentRowViewModel.Countdown), 11, Brush.Parse("#AD6A16")) } }, column: 2);
        Add(grid, Bound(nameof(AgentRowViewModel.Attempts), 13), column: 3);
        return new Border { BorderThickness = new(0, 0, 0, 1), BorderBrush = Brush.Parse("#E5ECEF"), Child = grid };
    }
    private void ShowDelays()
    {
        var window = new Window { Title = model.DelayTitle, Width = 530, Height = 330, MinWidth = 420, MinHeight = 240,
            Content = new StackPanel { Margin = new(20), Spacing = 12, Children = { Text(model.DelayTitle, 18, FontWeight.SemiBold),
                new ScrollViewer { MaxHeight = 210, Content = new ItemsControl { ItemsSource = model.Delays } }, Text("只比较本轮成功结果；超时节点不参与选择。", 12, color: Secondary) } } };
        window.ShowDialog(this);
    }
    private static TextBlock Text(string value, double size, FontWeight? weight = null, IBrush? color = null) => new()
    { Text = value, FontSize = size, FontWeight = weight ?? FontWeight.Normal, Foreground = color ?? Brush.Parse("#203644"), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private static TextBlock Bound(string property, double size, IBrush? color = null)
    { var text = Text("", size, color: color); text.Bind(TextBlock.TextProperty, new Binding(property)); return text; }
    private static void Add(Grid grid, Control child, int row = 0, int column = 0) { Grid.SetRow(child, row); Grid.SetColumn(child, column); grid.Children.Add(child); }
}

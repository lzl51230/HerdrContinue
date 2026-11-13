using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace HerdrAutoContinue;

internal static class UiPresentation
{
    private static readonly Dictionary<string, Bitmap> Flags = new(StringComparer.OrdinalIgnoreCase);

    internal static Control Icon(string kind, double size, string color = "AppForeground")
    {
        var data = kind switch
        {
            "refresh" => "M 19,7 A 8,8 0 1 1 7,5 L 7,1 M 7,5 L 2,5",
            "up" => "M 5,14 L 12,7 L 19,14 M 12,7 L 12,21",
            "down" => "M 5,10 L 12,17 L 19,10 M 12,3 L 12,17",
            "stop" => "M 6,6 L 18,6 L 18,18 L 6,18 Z",
            "play" => "M 7,4 L 20,12 L 7,20 Z",
            _ => "M 12,10 L 12,18 M 12,6 L 12,7 M 12,2 A 10,10 0 1 1 11.99,2"
        };
        var path = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(data), StrokeThickness = 2.5,
            Width = 24, Height = 24, Stretch = Stretch.Uniform };
        AppThemeManager.Bind(path, Avalonia.Controls.Shapes.Shape.StrokeProperty, color);
        return new Viewbox { Width = size, Height = size, Child = path, VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Left };
    }

    internal static Control RegionLabel(Watchdog.Core.ProxyRegion region)
    {
        var label = new TextBlock { Text = region.Name, VerticalAlignment = VerticalAlignment.Center };
        var uri = new Uri($"avares://HerdrAutoContinue/Assets/Flags/{region.Id.ToLowerInvariant()}.png");
        if (!AssetLoader.Exists(uri)) { label.Text = region.Id + " " + region.Name; return label; }
        if (!Flags.TryGetValue(region.Id, out var bitmap))
        {
            using var stream = AssetLoader.Open(uri);
            bitmap = new Bitmap(stream); Flags.Add(region.Id, bitmap);
        }
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, Children = {
            new Image { Source = bitmap, Width = 20, Height = 15, VerticalAlignment = VerticalAlignment.Center }, label } };
    }

    internal static void FitToScreen(Window window)
    {
        var minimumWidth = window.MinWidth;
        var minimumHeight = window.MinHeight;
        window.Opened += (_, _) => Fit();
        window.ScalingChanged += (_, _) => Fit();
        window.PositionChanged += (_, _) => { if (window.IsVisible) Fit(); };
        void Fit()
        {
            if (window.WindowState != WindowState.Normal) return;
            var screen = window.Screens.ScreenFromWindow(window);
            if (screen is null) return;
            var width = Math.Max(320, screen.WorkingArea.Width / screen.Scaling - 32);
            var height = Math.Max(240, screen.WorkingArea.Height / screen.Scaling - 64);
            window.MinWidth = Math.Min(minimumWidth, width);
            window.MinHeight = Math.Min(minimumHeight, height);
            window.Width = Math.Min(window.Width, width);
            window.Height = Math.Min(window.Height, height);
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace ReClaw.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        StartupLog.Write("App.Initialize");
        AvaloniaXamlLoader.Load(this);
        RequestedThemeVariant = ThemeVariant.Dark;
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            StartupLog.Write("App.OnFrameworkInitializationCompleted: creating MainWindow");
            try
            {
                var window = new MainWindow();
                desktop.MainWindow = window;
                window.Show();
            }
            catch (Exception ex)
            {
                StartupLog.Write($"MainWindow crash: {ex}");
                var fallback = BuildFallbackWindow(ex);
                desktop.MainWindow = fallback;
                fallback.Show();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static Window BuildFallbackWindow(Exception ex)
    {
        var logPath = StartupLog.PathOnDisk;
        var stack = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(16)
        };
        stack.Children.Add(new TextBlock
        {
            Text = "ReClaw failed to start the main window.",
            FontWeight = FontWeight.SemiBold,
            FontSize = 16
        });
        stack.Children.Add(new TextBlock
        {
            Text = "A safe-mode window is shown instead. See the log for details:",
            TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = logPath,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = "Consolas"
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"Error: {ex.GetType().Name}: {ex.Message}",
            TextWrapping = TextWrapping.Wrap
        });

        return new Window
        {
            Width = 640,
            Height = 420,
            Title = "ReClaw (Safe Mode)",
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Background = new SolidColorBrush(Color.Parse("#12141a")),
            Foreground = new SolidColorBrush(Color.Parse("#e4e4e7")),
            Content = stack
        };
    }
}

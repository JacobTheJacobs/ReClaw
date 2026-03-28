using System.ComponentModel;
using Avalonia.Controls;

namespace ReClaw.Desktop;

public partial class MainWindow : Window
{
    private TrayIconService? trayService;

    public MainWindow()
    {
        StartupLog.Write("MainWindow: constructor start");
        try
        {
            InitializeComponent();
            var vm = new MainWindowViewModel();
            DataContext = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;

            // Initialize tray icon
            trayService = new TrayIconService(this, vm);
            trayService.Initialize();

            Closing += OnWindowClosing;
            StartupLog.Write("MainWindow: initialized");
        }
        catch (Exception ex)
        {
            StartupLog.Write($"MainWindow: failed {ex}");
            throw;
        }
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        if (DataContext is MainWindowViewModel vm && vm.MinimizeToTray && !vm.ForceQuit)
        {
            e.Cancel = true;
            Hide();
        }
        else
        {
            trayService?.Dispose();
            trayService = null;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.Logs))
        {
            ScrollLogsToEnd();
        }
        else if (e.PropertyName == nameof(MainWindowViewModel.TraySettingsOpen))
        {
            // Rebuild tray menu when settings close
            if (sender is MainWindowViewModel vm && !vm.TraySettingsOpen)
            {
                trayService?.RebuildMenu();
            }
        }
    }

    private void ScrollLogsToEnd()
    {
        var logsBox = this.FindControl<TextBox>("LogsOutput");
        if (logsBox == null) return;
        var text = logsBox.Text;
        if (!string.IsNullOrEmpty(text))
        {
            logsBox.CaretIndex = text.Length;
        }
    }

    private void OnActionSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && sender is TextBox textBox)
        {
            viewModel.ActionSearchText = textBox.Text ?? string.Empty;
        }
    }
}

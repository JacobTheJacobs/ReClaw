using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace ReClaw.Desktop;

public class TrayIconService : IDisposable
{
    private TrayIcon? trayIcon;
    private readonly MainWindowViewModel viewModel;
    private readonly Window window;

    // Default tray commands - user can customise via settings
    private static readonly string[] DefaultTrayCommands =
    {
        "backup-create",
        "doctor",
        "gateway-repair",
        "gateway-start",
        "gateway-stop",
        "gateway-status",
        "recover",
        "fix"
    };

    public TrayIconService(Window window, MainWindowViewModel viewModel)
    {
        this.window = window;
        this.viewModel = viewModel;
    }

    public void Initialize()
    {
        var menu = BuildMenu();
        trayIcon = new TrayIcon
        {
            ToolTipText = "ReClaw - OpenClaw Manager",
            Menu = menu,
            IsVisible = true
        };

        // Use the window's icon for the tray
        try
        {
            trayIcon.Icon = window.Icon;
        }
        catch
        {
            // Icon load failed - tray still works without it
        }

        trayIcon.Clicked += OnTrayIconClicked;
    }

    public NativeMenu BuildMenu()
    {
        var menu = new NativeMenu();

        // Show / Hide window
        var showItem = new NativeMenuItem("Show ReClaw");
        showItem.Click += (_, _) => Dispatcher.UIThread.Post(ShowWindow);
        menu.Add(showItem);

        menu.Add(new NativeMenuItemSeparator());

        // Quick commands
        var enabledCommands = viewModel.TrayCommands.Count > 0
            ? viewModel.TrayCommands
            : DefaultTrayCommands.ToList();

        foreach (var cmdId in enabledCommands)
        {
            var action = viewModel.Actions.FirstOrDefault(
                a => string.Equals(a.Id, cmdId, StringComparison.OrdinalIgnoreCase));
            if (action == null) continue;

            var item = new NativeMenuItem($"{action.Emoji} {action.Label}");
            var capturedAction = action;
            item.Click += (_, _) => Dispatcher.UIThread.Post(() =>
            {
                if (viewModel.RunActionCommand.CanExecute(capturedAction))
                    viewModel.RunActionCommand.Execute(capturedAction);
            });
            menu.Add(item);
        }

        menu.Add(new NativeMenuItemSeparator());

        // Settings
        var settingsItem = new NativeMenuItem("Tray Settings...");
        settingsItem.Click += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            ShowWindow();
            viewModel.OpenTraySettings();
        });
        menu.Add(settingsItem);

        menu.Add(new NativeMenuItemSeparator());

        // Quit
        var quitItem = new NativeMenuItem("Quit");
        quitItem.Click += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            viewModel.ForceQuit = true;
            window.Close();
        });
        menu.Add(quitItem);

        return menu;
    }

    public void RebuildMenu()
    {
        if (trayIcon != null)
            trayIcon.Menu = BuildMenu();
    }

    private void ShowWindow()
    {
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(ShowWindow);
    }

    public void Dispose()
    {
        if (trayIcon != null)
        {
            trayIcon.Clicked -= OnTrayIconClicked;
            trayIcon.IsVisible = false;
            trayIcon.Dispose();
            trayIcon = null;
        }
    }
}

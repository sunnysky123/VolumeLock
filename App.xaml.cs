using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace VolumeLock;

public partial class App : Application
{
    private readonly ThemeService _themeService = new();
    private readonly DispatcherQueue _dispatcherQueue;
    private Window? _window;
    private TrayIconService? _trayIcon;
    private bool _trayAvailable;
    private bool _exiting;
    private bool _activationPending;

    public App()
    {
        InitializeComponent();

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("当前线程没有可用的 DispatcherQueue。");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();

        _themeService.ThemeChanged += OnThemeChanged;
        _themeService.Start();

        _window.Activate();
        _window.AppWindow.Closing += OnAppWindowClosing;
        _window.Closed += OnWindowClosed;

        try
        {
            _trayIcon = new TrayIconService
            {
                ForegroundWindow = WindowNative.GetWindowHandle(_window)
            };
            _trayIcon.ShowRequested += ShowMainWindow;
            _trayIcon.ToggleSpeakerRequested += () => (_window as MainWindow)?.ToggleSpeakerLock();
            _trayIcon.ToggleMicrophoneRequested += () => (_window as MainWindow)?.ToggleMicrophoneLock();
            _trayIcon.ExitRequested += ExitFromTray;
            _trayIcon.Initialize();
            _trayAvailable = true;
        }
        catch
        {
            _trayIcon?.Dispose();
            _trayIcon = null;
            _trayAvailable = false;
        }

        if (_activationPending || Program.ConsumePendingActivation())
        {
            _activationPending = false;
            ShowMainWindow();
        }
    }

    public static void UpdateTrayLockState(
        bool speakerLocked,
        bool microphoneLocked,
        int speakerVolumePercent,
        int microphoneVolumePercent)
    {
        (Current as App)?._trayIcon?.UpdateLockState(
            speakerLocked,
            microphoneLocked,
            speakerVolumePercent,
            microphoneVolumePercent);
    }

    private void OnThemeChanged(ElementTheme theme)
    {
        (_window as MainWindow)?.ApplyTheme(theme);
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_exiting || !_trayAvailable)
        {
            return;
        }

        args.Cancel = true;
        sender.Hide();
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        CleanupResources();
    }

    public void ShowMainWindowFromActivation()
    {
        if (_window is null)
        {
            _activationPending = true;
            return;
        }

        _dispatcherQueue.TryEnqueue(ShowMainWindow);
    }

    private void ShowMainWindow()
    {
        if (_window is null)
        {
            return;
        }

        _window.AppWindow.Show();
        _window.Activate();
    }

    private void ExitFromTray()
    {
        CleanupResources();
        Exit();
    }

    private void CleanupResources()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        _themeService.Stop();
        _themeService.ThemeChanged -= OnThemeChanged;
        _trayIcon?.Dispose();
        _trayIcon = null;
        (_window as MainWindow)?.DisposeResources();
    }
}

using System;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Win32;
using Windows.UI.ViewManagement;

namespace VolumeLock;

public sealed class ThemeService
{
    private const string PersonalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private readonly UISettings _uiSettings = new();
    private readonly DispatcherQueue _dispatcherQueue;

    private ElementTheme? _currentTheme;
    private bool _started;

    public event Action<ElementTheme>? ThemeChanged;

    public ThemeService()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread()
            ?? throw new InvalidOperationException("当前线程没有可用的 DispatcherQueue。");
    }

    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _uiSettings.ColorValuesChanged += OnColorValuesChanged;
        ApplySystemTheme();
    }

    public void Stop()
    {
        if (!_started)
        {
            return;
        }

        _started = false;
        _uiSettings.ColorValuesChanged -= OnColorValuesChanged;
    }

    private void OnColorValuesChanged(UISettings sender, object args)
    {
        _dispatcherQueue.TryEnqueue(ApplySystemTheme);
    }

    private void ApplySystemTheme()
    {
        var theme = GetSystemTheme();
        if (_currentTheme == theme)
        {
            return;
        }

        _currentTheme = theme;
        ThemeChanged?.Invoke(theme);
    }

    private ElementTheme GetSystemTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKeyPath);
            if (key?.GetValue("AppsUseLightTheme") is int useLightTheme)
            {
                return useLightTheme == 0 ? ElementTheme.Dark : ElementTheme.Light;
            }
        }
        catch
        {
            // Fall back to the UISettings background color below.
        }

        var background = _uiSettings.GetColorValue(UIColorType.Background);
        var brightness = background.R + background.G + background.B;
        return brightness < 128 * 3 ? ElementTheme.Dark : ElementTheme.Light;
    }
}

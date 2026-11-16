using System.IO;
using Microsoft.UI.Xaml;

namespace VolumeLock;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        AppWindow.SetIcon(iconPath);

        RootFrame.Navigate(typeof(MainPage));
    }

    public void DisposeResources()
    {
        (RootFrame.Content as MainPage)?.DisposeResources();
    }

    public void ApplyTheme(ElementTheme theme)
    {
        RootGrid.RequestedTheme = theme;
    }

    public void ToggleSpeakerLock()
    {
        (RootFrame.Content as MainPage)?.ToggleSpeakerLockFromTray();
    }

    public void ToggleMicrophoneLock()
    {
        (RootFrame.Content as MainPage)?.ToggleMicrophoneLockFromTray();
    }
}

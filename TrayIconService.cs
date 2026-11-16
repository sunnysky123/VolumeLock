using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace VolumeLock;

public sealed class TrayIconService : IDisposable
{
    private const string WindowClassName = "VolumeLock.TrayWindow";
    private const uint CallbackMessage = 0x8001;
    private const uint TrayIconId = 1;

    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint WM_NULL = 0x0000;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;

    private const uint MF_BYPOSITION = 0x00000400;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_STRING = 0x00000000;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_UNCHECKED = 0x00000000;

    private const uint TPM_RETURNCMD = 0x0100;

    private const int CommandShow = 1001;
    private const int CommandSpeaker = 1002;
    private const int CommandMicrophone = 1003;
    private const int CommandExit = 1004;

    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly WndProc _wndProc;
    private readonly object _stateLock = new();

    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private IntPtr _unlockedIconHandle;
    private bool _disposeLockedIcon;
    private bool _disposeUnlockedIcon;
    private ushort _windowClassAtom;
    private bool _initialized;
    private bool _disposed;
    private bool _currentIconLocked;
    private bool _speakerChecked;
    private bool _microphoneChecked;
    private string _currentTooltip = LocalizationService.GetString("TrayTooltipUnlocked");

    public TrayIconService()
    {
        _wndProc = OnWindowMessage;
    }

    public IntPtr ForegroundWindow { get; set; }

    public event Action? ShowRequested;
    public event Action? ToggleSpeakerRequested;
    public event Action? ToggleMicrophoneRequested;
    public event Action? ExitRequested;

    public void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        var moduleHandle = GetModuleHandleW(null);
        var windowClass = new WNDCLASSW
        {
            lpfnWndProc = _wndProc,
            hInstance = moduleHandle,
            lpszClassName = WindowClassName
        };

        _windowClassAtom = RegisterClassW(ref windowClass);
        if (_windowClassAtom == 0)
        {
            throw new InvalidOperationException("注册托盘消息窗口失败。");
        }

        _windowHandle = CreateWindowExW(
            0,
            WindowClassName,
            "VolumeLockTray",
            0,
            0,
            0,
            0,
            0,
            HWND_MESSAGE,
            IntPtr.Zero,
            moduleHandle,
            IntPtr.Zero);

        if (_windowHandle == IntPtr.Zero)
        {
            Cleanup();
            throw new InvalidOperationException("创建托盘消息窗口失败。");
        }

        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
        var unlockedIconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIconUnlocked.ico");
        var hasLockedIconFile = File.Exists(iconPath);
        var hasUnlockedIconFile = File.Exists(unlockedIconPath);

        _iconHandle = hasLockedIconFile
            ? LoadImageW(IntPtr.Zero, iconPath, 1, 16, 16, 0x0010)
            : LoadIconW(IntPtr.Zero, new IntPtr(32512));
        _disposeLockedIcon = hasLockedIconFile && _iconHandle != IntPtr.Zero;

        _unlockedIconHandle = hasUnlockedIconFile
            ? LoadImageW(IntPtr.Zero, unlockedIconPath, 1, 16, 16, 0x0010)
            : _iconHandle;
        _disposeUnlockedIcon = hasUnlockedIconFile
            && _unlockedIconHandle != IntPtr.Zero
            && _unlockedIconHandle != _iconHandle;

        if (_iconHandle == IntPtr.Zero || _unlockedIconHandle == IntPtr.Zero)
        {
            Cleanup();
            throw new InvalidOperationException("加载托盘图标失败。");
        }

        var data = CreateNotifyIconData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _currentIconLocked ? _iconHandle : _unlockedIconHandle;
        data.szTip = ClampTooltip(_currentTooltip);

        if (!Shell_NotifyIconW(NIM_ADD, ref data))
        {
            Cleanup();
            throw new InvalidOperationException("创建托盘图标失败。");
        }

        _initialized = true;
    }

    public void UpdateLockState(
        bool speakerLocked,
        bool microphoneLocked,
        int speakerVolumePercent,
        int microphoneVolumePercent)
    {
        var anyLocked = speakerLocked || microphoneLocked;
        var tooltip = ClampTooltip(BuildTooltip(speakerLocked, microphoneLocked, speakerVolumePercent, microphoneVolumePercent));

        lock (_stateLock)
        {
            _speakerChecked = speakerLocked;
            _microphoneChecked = microphoneLocked;

            if (_disposed)
            {
                return;
            }

            if (!_initialized)
            {
                _currentIconLocked = anyLocked;
                _currentTooltip = tooltip;
                return;
            }

            if (_currentIconLocked == anyLocked && _currentTooltip == tooltip)
            {
                return;
            }

            var data = CreateNotifyIconData();
            data.uFlags = NIF_ICON | NIF_TIP;
            data.hIcon = anyLocked ? _iconHandle : _unlockedIconHandle;
            data.szTip = tooltip;

            if (!Shell_NotifyIconW(NIM_MODIFY, ref data))
            {
                return;
            }

            _currentIconLocked = anyLocked;
            _currentTooltip = tooltip;
        }
    }

    private static string ClampTooltip(string tooltip)
    {
        const int maxLength = 120;
        return tooltip.Length <= maxLength ? tooltip : tooltip[..maxLength] + "…";
    }

    private static string BuildTooltip(
        bool speakerLocked,
        bool microphoneLocked,
        int speakerVolumePercent,
        int microphoneVolumePercent)
    {
        var lines = new List<string>();

        if (speakerLocked)
        {
            lines.Add(LocalizationService.GetFormatted(
                "TrayTooltipSpeaker",
                Math.Clamp(speakerVolumePercent, 0, 100)));
        }

        if (microphoneLocked)
        {
            lines.Add(LocalizationService.GetFormatted(
                "TrayTooltipMicrophone",
                Math.Clamp(microphoneVolumePercent, 0, 100)));
        }

        return lines.Count == 0
            ? LocalizationService.GetString("TrayTooltipUnlocked")
            : string.Join("\n", lines);
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Cleanup();
    }

    private void Cleanup()
    {
        if (_initialized && _windowHandle != IntPtr.Zero)
        {
            var data = CreateNotifyIconData();
            Shell_NotifyIconW(NIM_DELETE, ref data);
        }

        if (_disposeUnlockedIcon && _unlockedIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_unlockedIconHandle);
        }

        if (_disposeLockedIcon && _iconHandle != IntPtr.Zero)
        {
            DestroyIcon(_iconHandle);
        }

        _iconHandle = IntPtr.Zero;
        _unlockedIconHandle = IntPtr.Zero;
        _disposeLockedIcon = false;
        _disposeUnlockedIcon = false;

        if (_windowHandle != IntPtr.Zero)
        {
            DestroyWindow(_windowHandle);
            _windowHandle = IntPtr.Zero;
        }

        if (_windowClassAtom != 0)
        {
            UnregisterClassW(WindowClassName, GetModuleHandleW(null));
            _windowClassAtom = 0;
        }

        _initialized = false;
    }

    private NOTIFYICONDATAW CreateNotifyIconData()
    {
        return new NOTIFYICONDATAW
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATAW>(),
            hWnd = _windowHandle,
            uID = TrayIconId
        };
    }

    private IntPtr OnWindowMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return DefWindowProcW(hWnd, message, wParam, lParam);
            }
        }

        if (message == CallbackMessage)
        {
            switch (lParam.ToInt64())
            {
                case WM_LBUTTONUP:
                    ShowRequested?.Invoke();
                    break;
                case WM_RBUTTONUP:
                    try
                    {
                        ShowContextMenu();
                    }
                    catch
                    {
                        // Ignore shell menu failures; the app keeps running.
                    }

                    break;
            }

            return IntPtr.Zero;
        }

        return DefWindowProcW(hWnd, message, wParam, lParam);
    }

    private void ShowContextMenu()
    {
        bool speakerChecked;
        bool microphoneChecked;
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            speakerChecked = _speakerChecked;
            microphoneChecked = _microphoneChecked;
        }

        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            return;
        }

        try
        {
            InsertMenuW(menu, 0, MF_BYPOSITION | MF_STRING, new IntPtr(CommandShow), LocalizationService.GetString("TrayShow"));
            InsertMenuW(menu, 1, MF_BYPOSITION | MF_SEPARATOR, IntPtr.Zero, null);
            InsertMenuW(menu, 2, MF_BYPOSITION | MF_STRING | (speakerChecked ? MF_CHECKED : MF_UNCHECKED), new IntPtr(CommandSpeaker), LocalizationService.GetString("TrayLockSpeaker"));
            InsertMenuW(menu, 3, MF_BYPOSITION | MF_STRING | (microphoneChecked ? MF_CHECKED : MF_UNCHECKED), new IntPtr(CommandMicrophone), LocalizationService.GetString("TrayLockMicrophone"));
            InsertMenuW(menu, 4, MF_BYPOSITION | MF_SEPARATOR, IntPtr.Zero, null);
            InsertMenuW(menu, 5, MF_BYPOSITION | MF_STRING, new IntPtr(CommandExit), LocalizationService.GetString("TrayExit"));

            if (!GetCursorPos(out var point))
            {
                return;
            }

            var foreground = ForegroundWindow != IntPtr.Zero ? ForegroundWindow : _windowHandle;
            SetForegroundWindow(foreground);

            var command = TrackPopupMenuEx(
                menu,
                TPM_RETURNCMD,
                point.X,
                point.Y,
                hWnd: foreground,
                lptpm: IntPtr.Zero);

            switch (command)
            {
                case CommandShow:
                    ShowRequested?.Invoke();
                    break;
                case CommandSpeaker:
                    ToggleSpeakerRequested?.Invoke();
                    break;
                case CommandMicrophone:
                    ToggleMicrophoneRequested?.Invoke();
                    break;
                case CommandExit:
                    ExitRequested?.Invoke();
                    break;
            }

            PostMessageW(foreground, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSW
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)] public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATAW
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassW(ref WNDCLASSW lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClassW(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hInst, string lpszName, uint type, int cx, int cy, uint fuLoad);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr LoadIconW(IntPtr hInst, IntPtr lpIconName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATAW lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool InsertMenuW(
        IntPtr hMenu,
        uint uPosition,
        uint uFlags,
        IntPtr uIDNewItem,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpNewItem);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int TrackPopupMenuEx(
        IntPtr hMenu,
        uint uFlags,
        int x,
        int y,
        IntPtr hWnd,
        IntPtr lptpm);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyMenu(IntPtr hMenu);
}

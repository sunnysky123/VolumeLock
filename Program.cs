using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using WinRT;

namespace VolumeLock;

public class Program
{
    private const string SingleInstanceKey = "27506sunnysky.VolumeLock.SingleInstance";

    private static IntPtr _redirectEventHandle = IntPtr.Zero;
    private static int _activationRequested;

    [STAThread]
    public static int Main(string[] args)
    {
        ComWrappersSupport.InitializeComWrappers();

        var shouldStart = DecideRedirection();

        if (shouldStart)
        {
            Application.Start(p =>
            {
                var context = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());

                SynchronizationContext.SetSynchronizationContext(context);
                _ = new App();
            });
        }

        return 0;
    }

    public static bool ConsumePendingActivation()
    {
        return Interlocked.Exchange(ref _activationRequested, 0) != 0;
    }

    private static bool DecideRedirection()
    {
        var activationArgs = AppInstance.GetCurrent().GetActivatedEventArgs();
        var keyInstance = AppInstance.FindOrRegisterForKey(SingleInstanceKey);

        if (keyInstance.IsCurrent)
        {
            keyInstance.Activated += OnActivated;
            return true;
        }

        RedirectActivationTo(activationArgs, keyInstance);
        return false;
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        if (Application.Current is App app)
        {
            app.ShowMainWindowFromActivation();
        }
        else
        {
            Interlocked.Exchange(ref _activationRequested, 1);
        }
    }

    private static void RedirectActivationTo(AppActivationArguments args, AppInstance keyInstance)
    {
        _redirectEventHandle = CreateEvent(IntPtr.Zero, true, false, null);

        _ = Task.Run(() =>
        {
            keyInstance.RedirectActivationToAsync(args).AsTask().GetAwaiter().GetResult();
            SetEvent(_redirectEventHandle);
        });

        const uint CWMO_DEFAULT = 0;
        const uint INFINITE = 0xFFFFFFFF;

        _ = CoWaitForMultipleObjects(
            CWMO_DEFAULT,
            INFINITE,
            1,
            new[] { _redirectEventHandle },
            out _);

        try
        {
            var process = Process.GetProcessById((int)keyInstance.ProcessId);
            SetForegroundWindow(process.MainWindowHandle);
        }
        catch
        {
            // The original instance may have exited between redirect and foreground call.
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateEvent(
        IntPtr lpEventAttributes,
        bool bManualReset,
        bool bInitialState,
        string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetEvent(IntPtr hEvent);

    [DllImport("ole32.dll")]
    private static extern uint CoWaitForMultipleObjects(
        uint dwFlags,
        uint dwMilliseconds,
        ulong nHandles,
        IntPtr[] pHandles,
        out uint dwIndex);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);
}

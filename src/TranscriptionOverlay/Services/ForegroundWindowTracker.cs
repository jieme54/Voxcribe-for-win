using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace TranscriptionOverlay.Services;

public sealed class ForegroundWindowTracker : IDisposable
{
    private static readonly HashSet<string> IgnoredWindowClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "#32768",
        "NotifyIconOverflowWindow",
        "Shell_TrayWnd",
        "TrayNotifyWnd",
        "TopLevelWindowForOverflowXamlIsland",
        "Windows.UI.Input.InputSite.WindowClass",
        "Xaml_WindowedPopupClass",
    };

    private readonly uint _currentProcessId = (uint)Environment.ProcessId;
    private DispatcherTimer? _timer;
    private IntPtr _overlayHandle;

    public IntPtr LastExternalWindow { get; private set; }

    public void Start(Window window)
    {
        _overlayHandle = new WindowInteropHelper(window).Handle;

        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(150),
        };

        _timer.Tick += Timer_Tick;
        _timer.Start();
        CaptureForegroundWindow();
    }

    public bool FocusWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero)
        {
            return false;
        }

        if (Win32Native.IsIconic(handle))
        {
            Win32Native.ShowWindowAsync(handle, Win32Native.SW_RESTORE);
        }

        var currentThreadId = Win32Native.GetCurrentThreadId();
        var foregroundHandle = Win32Native.GetForegroundWindow();
        var foregroundThreadId = foregroundHandle == IntPtr.Zero
            ? 0
            : Win32Native.GetWindowThreadProcessId(foregroundHandle, out _);
        var targetThreadId = Win32Native.GetWindowThreadProcessId(handle, out _);

        var attachedToForeground = false;
        var attachedToTarget = false;

        try
        {
            if (foregroundThreadId != 0 && foregroundThreadId != currentThreadId)
            {
                attachedToForeground = Win32Native.AttachThreadInput(currentThreadId, foregroundThreadId, true);
            }

            if (targetThreadId != 0 && targetThreadId != currentThreadId)
            {
                attachedToTarget = Win32Native.AttachThreadInput(currentThreadId, targetThreadId, true);
            }

            Win32Native.BringWindowToTop(handle);
            Win32Native.SetForegroundWindow(handle);
            Win32Native.SetActiveWindow(handle);
            Win32Native.SetFocus(handle);

            if (Win32Native.GetForegroundWindow() == handle)
            {
                return true;
            }

            // Simulate a quick Alt tap to help Windows allow the foreground switch.
            Win32Native.SendAltTap();
            Win32Native.BringWindowToTop(handle);
            Win32Native.SetForegroundWindow(handle);
            Win32Native.SetActiveWindow(handle);
            Win32Native.SetFocus(handle);
            return Win32Native.GetForegroundWindow() == handle;
        }
        finally
        {
            if (attachedToTarget)
            {
                Win32Native.AttachThreadInput(currentThreadId, targetThreadId, false);
            }

            if (attachedToForeground)
            {
                Win32Native.AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        CaptureForegroundWindow();
    }

    private void CaptureForegroundWindow()
    {
        var handle = Win32Native.GetForegroundWindow();
        if (handle == IntPtr.Zero || handle == _overlayHandle || !IsTrackedExternalWindow(handle))
        {
            return;
        }

        LastExternalWindow = handle;
    }

    private bool IsTrackedExternalWindow(IntPtr handle)
    {
        Win32Native.GetWindowThreadProcessId(handle, out var processId);
        if (processId == _currentProcessId)
        {
            return false;
        }

        var className = Win32Native.GetWindowClassName(handle);
        return !string.IsNullOrWhiteSpace(className)
            && !IgnoredWindowClasses.Contains(className);
    }

    public void Dispose()
    {
        if (_timer is null)
        {
            return;
        }

        _timer.Stop();
        _timer.Tick -= Timer_Tick;
        _timer = null;
    }
}

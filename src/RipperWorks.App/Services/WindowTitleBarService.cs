using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RipperWorks.App.Services;

public static class WindowTitleBarService
{
    private const int ImmersiveDarkMode = 20;
    private const int ImmersiveDarkModeLegacy = 19;
    private static bool _initialized;
    private static bool _useDarkMode;

    public static void Initialize()
    {
        if (_initialized)
            return;
        _initialized = true;
        EventManager.RegisterClassHandler(
            typeof(Window),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(Window_OnLoaded));
    }

    public static void ApplyToAll(bool useDarkMode)
    {
        _useDarkMode = useDarkMode;
        var application = Application.Current;
        if (application is null)
            return;
        if (!application.Dispatcher.CheckAccess())
        {
            application.Dispatcher.Invoke(() => ApplyToAll(useDarkMode));
            return;
        }
        foreach (Window window in application.Windows)
            Apply(window, useDarkMode);
    }

    private static void Window_OnLoaded(
        object sender,
        RoutedEventArgs e)
    {
        if (sender is Window window)
            Apply(window, _useDarkMode);
    }

    private static void Apply(Window window, bool useDarkMode)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
            return;
        var enabled = useDarkMode ? 1 : 0;
        if (DwmSetWindowAttribute(
                handle,
                ImmersiveDarkMode,
                ref enabled,
                sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(
                handle,
                ImmersiveDarkModeLegacy,
                ref enabled,
                sizeof(int));
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr windowHandle,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}

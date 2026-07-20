using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace BE_Cruncher.Services;

/// <summary>
/// Tells DWM to render a window's native title bar (minimize/maximize/close, caption text) in dark
/// mode — WPF's own styling only reaches the client area, never the OS-drawn chrome, so without this
/// a dark-themed window still shows a stock white title bar.
/// </summary>
public static class DarkTitleBar
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmwaUseImmersiveDarkModeWin11 = 20;
    private const int DwmwaUseImmersiveDarkModeWin10 = 19;

    public static void Apply(Window window)
    {
        void ApplyNow()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;

            int enabled = 1;
            if (DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeWin11, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkModeWin10, ref enabled, sizeof(int));
        }

        if (window.IsLoaded)
            ApplyNow();
        else
            window.SourceInitialized += (_, _) => ApplyNow();
    }
}

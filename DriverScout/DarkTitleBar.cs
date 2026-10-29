using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DriverScout;

/// <summary>Тёмный системный заголовок окна, чтобы он не выбивался из тёмной темы.</summary>
public static class DarkTitleBar
{
    private const int DwmUseImmersiveDarkMode = 20;     // Windows 10 20H1+ и Windows 11
    private const int DwmUseImmersiveDarkModeOld = 19;  // ранние сборки Windows 10
    private const int DwmCaptionColor = 35;             // только Windows 11

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static void Apply(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        int on = 1;
        if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeOld, ref on, sizeof(int));
        int caption = 0x000F0E0E; // цвет фона окна в формате COLORREF (0x00BBGGRR)
        DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref caption, sizeof(int));
    }
}

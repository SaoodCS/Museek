using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Museek.Services;

internal static class WindowThemeService
{
    public static void Apply(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        Set(handle, 20, 1);
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        Set(handle, 35, ColorRef(((SolidColorBrush)window.Background).Color));
        Set(handle, 36, ColorRef(((SolidColorBrush)window.Foreground).Color));
        Set(handle, 34, ColorRef(Color.FromRgb(0x30, 0x35, 0x2D)));
    }

    private static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);
    private static void Set(IntPtr handle, int attribute, int value)
    {
        var result = DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        if (result < 0) Debug.WriteLine($"Caption attribute {attribute}: 0x{result:X8}.");
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}

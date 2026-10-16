using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AllStarsModManager.App;

/// <summary>Base class of every window the manager opens: applies the dark theme and asks Windows for a dark title bar.</summary>
public class ThemedWindow : Window
{
    public ThemedWindow()
    {
        Style = (Style)Application.Current.FindResource("AppWindow");
        Icon = AppIcon();
        SourceInitialized += (_, _) => TitleBarTheme.Apply(this);
    }

    static ImageSource? _icon;

    /// <summary>The star roundel from the theme, rendered once to a small bitmap for the title bar and taskbar button (no icon file is shipped).</summary>
    static ImageSource? AppIcon()
    {
        if (_icon is not null) return _icon;
        try
        {
            var logo = new ContentControl { Style = (Style)Application.Current.FindResource("LogoMark"), Width = 64, Height = 64 };
            logo.Measure(new Size(64, 64)); logo.Arrange(new Rect(0, 0, 64, 64)); logo.UpdateLayout();
            var bmp = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
            bmp.Render(logo); bmp.Freeze();
            return _icon = bmp;
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return null; }
    }

    /// <summary>Looks up a theme resource (brush, geometry, font) by key.</summary>
    public static T Res<T>(string key) => (T)Application.Current.FindResource(key);
}

/// <summary>
/// Uses the documented DWM window attributes to give the title bar the charcoal theme (dark mode on Windows 10 2004+, caption/text/border
/// colours on Windows 11). On older systems the calls simply fail and the normal title bar stays; nothing else depends on them.
/// </summary>
static class TitleBarTheme
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    const int UseImmersiveDarkMode = 20, BorderColor = 34, CaptionColor = 35, TextColor = 36;

    // COLORREF is 0x00BBGGRR
    static int Rgb(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    public static void Apply(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return;
            int on = 1, caption = Rgb(0x20, 0x20, 0x20), text = Rgb(0xF2, 0xF2, 0xF2), border = Rgb(0x4A, 0x4A, 0x4A);
            DwmSetWindowAttribute(h, UseImmersiveDarkMode, ref on, sizeof(int));
            DwmSetWindowAttribute(h, CaptionColor, ref caption, sizeof(int));
            DwmSetWindowAttribute(h, TextColor, ref text, sizeof(int));
            DwmSetWindowAttribute(h, BorderColor, ref border, sizeof(int));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }
}

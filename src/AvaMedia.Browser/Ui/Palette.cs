using Avalonia.Media;

namespace AvaMedia.Browser;

/// <summary>macOS 26 light palette shared by the remote console.</summary>
internal static class Palette
{
    public static readonly Color BlueColor = Color.Parse("#007AFF");
    public static readonly IBrush Page = new SolidColorBrush(Color.Parse("#F2F3F7"));
    public static readonly IBrush Card = Brushes.White;
    public static readonly IBrush Text = new SolidColorBrush(Color.Parse("#1D1D1F"));
    public static readonly IBrush Secondary = new SolidColorBrush(Color.Parse("#86868B"));
    public static readonly IBrush Tertiary = new SolidColorBrush(Color.Parse("#AEAEB2"));
    public static readonly IBrush Hairline = new SolidColorBrush(Color.Parse("#E5E5EA"));
    public static readonly IBrush Fill = new SolidColorBrush(Color.Parse("#F0F0F3"));
    public static readonly IBrush FillHover = new SolidColorBrush(Color.Parse("#E6E6EB"));
    public static readonly IBrush Blue = new SolidColorBrush(BlueColor);
    public static readonly IBrush BlueSoft = new SolidColorBrush(Color.Parse("#EAF3FF"));
    public static readonly IBrush Green = new SolidColorBrush(Color.Parse("#34C759"));
    public static readonly IBrush GreenText = new SolidColorBrush(Color.Parse("#1F9D3A"));
    public static readonly IBrush GreenSoft = new SolidColorBrush(Color.Parse("#E7F7EC"));
    public static readonly IBrush Orange = new SolidColorBrush(Color.Parse("#FF9500"));
    public static readonly IBrush OrangeSoft = new SolidColorBrush(Color.Parse("#FFF4E0"));
    public static readonly IBrush Red = new SolidColorBrush(Color.Parse("#FF3B30"));
    public static readonly IBrush RedSoft = new SolidColorBrush(Color.Parse("#FFECEB"));
    public static readonly IBrush Banner = new SolidColorBrush(Color.Parse("#FFF6E0"));
    public static readonly IBrush BannerText = new SolidColorBrush(Color.Parse("#8A5A00"));
    public static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#1C1C1E"));
}

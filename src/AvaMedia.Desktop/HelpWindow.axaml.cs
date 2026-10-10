using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using AvaMedia.Core;
using AvaMedia.Desktop.Controls;

namespace AvaMedia.Desktop;

public partial class HelpWindow : Window
{
    public HelpWindow()
    {
        InitializeComponent();
        WindowArtwork.SetKind(this, "info");
        FeatureList.Children.Add(FeatureCard(Catalog.Find("mp4"), "把视频转成 MP4、MKV 等格式，方便播放或分享。"));
        FeatureList.Children.Add(FeatureCard(Catalog.Find("video-compress"), "减小视频体积，可按画质压缩或指定文件大小。"));
        FeatureList.Children.Add(FeatureCard(Catalog.Find("clip"), "截取需要的片段，裁剪或旋转画面，每段单独保存。"));
        FeatureList.Children.Add(FeatureCard("音频", "转换格式、剪辑或合并声音，也可提取视频里的声音。", "audio"));
        FeatureList.Children.Add(FeatureCard("图片", "转换格式、压缩体积、缩放或旋转图片。", "image"));
        FeatureList.Children.Add(FeatureCard("文档", "拆分、合并或压缩 PDF，把图片或文字做成 PDF。", "document"));
        FeatureList.Children.Add(FeatureCard(Catalog.Find("download"), "粘贴视频链接，选择画质，下载视频或音频。"));
        FeatureList.Children.Add(FeatureCard("工具集", "自动标签分类、媒体整理，批量裁剪、旋转、重命名，生成截图，压缩或解压 ZIP。", "gear"));
        FeatureList.Children.Add(FeatureCard(Catalog.Find("player"), "播放视频和音乐，调整倍速、切换音轨或截图。"));
        FeatureList.Children.Add(FeatureCard("WiFi 传文件", "手机与电脑连同一 WiFi，扫码互传文件。", "wifi"));

        OtherFeatureList.Children.Add(FeatureCard(Catalog.Find("video-slim"), "先预估压缩后的大小，再批量压缩视频。"));
        OtherFeatureList.Children.Add(FeatureCard(Catalog.Find("join"), "把多段视频接成一段，或给视频配上独立音频。"));
        OtherFeatureList.Children.Add(FeatureCard(Catalog.Find("split"), "把视频里的声音或画面单独保存。"));
        OtherFeatureList.Children.Add(FeatureCard(Catalog.Find("auto-subtitle"), "把说话内容识别成字幕，保存字幕文件或带字幕的视频。"));
        OtherFeatureList.Children.Add(FeatureCard(Catalog.Find("voice-enhance"), "减弱背景噪声，让说话声更清楚。"));
        OtherFeatureList.Children.Add(FeatureCard(Catalog.Find("delogo"), "用周围像素填补水印选区，无法还原被遮挡的原画面。"));
        OtherFeatureList.Children.Add(FeatureCard(Catalog.Find("frames"), "按时间间隔，把视频画面保存为图片。"));
        OtherFeatureList.Children.Add(FeatureCard(Catalog.Find("repair"), "更换视频封装，不重新压缩；不能保证修复损坏文件。"));
        OtherFeatureList.Children.Add(FeatureCard("光盘", "把未加密的 VOB 视频转成普通视频，或把光盘数据复制为 ISO。", "disc"));
        OtherFeatureList.Children.Add(FeatureCard("PDF → DOCX / XLSX", "提取 PDF 里的文字，不保留原排版；扫描件需另行识别文字。", "document"));
    }

    private static Border FeatureCard(Feature feature, string description)
        => FeatureCard(feature.Label, description, feature.Icon);

    private static Border FeatureCard(string title, string description, string icon)
    {
        var heading = Ui.Text(title);
        heading.FontWeight = FontWeight.SemiBold;
        var text = new StackPanel { Spacing = 4 };
        text.Children.Add(heading);
        text.Children.Add(Ui.Text(description, "caption"));
        var layout = new Grid { ColumnDefinitions = new("40,*"), ColumnSpacing = 8 };
        Control artwork = icon == "wifi"
            ? new ActionIcon { Kind = "wifi", Width = 32, Height = 32, VerticalAlignment = VerticalAlignment.Top }
            : new FeatureIcon { Kind = icon, Label = "", Width = 40, Height = 36, VerticalAlignment = VerticalAlignment.Top };
        layout.Children.Add(artwork);
        Grid.SetColumn(text, 1);
        layout.Children.Add(text);
        return new Border { Child = layout, Classes = { "help-card" } };
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape) { e.Handled = true; Close(); }
    }

    private void CloseClick(object? sender, RoutedEventArgs e) => Close();
}

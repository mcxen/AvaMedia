using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using System.Globalization;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly IAppOptionsServices _services;
    private readonly bool _ownsServices;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initializing;
    private bool _applied;
    private readonly List<Func<object?>> _values=[];
    private object?[] _appliedValues=[];
    public event EventHandler? Applied;
    public SettingsWindow() : this(new AppSettings()) { }
    public SettingsWindow(AppSettings settings, IAppOptionsServices? services = null)
    {
        InitializeComponent(); _settings = settings; _services = services ?? new AppOptionsServices(); _ownsServices = services is null;
        Localization.Changed += LanguageChanged;
        Populate(settings);
        foreach (var input in new[] { OutputInput, FfmpegInput, FfprobeInput, YtdlpInput })
        { _values.Add(()=>input.Text); input.PropertyChanged += (_, args) => { if (args.Property == TextBox.TextProperty) MarkDirty(); }; }
        foreach (var input in new[] { AutoGpuInput, MultithreadInput, NotifyInput, ReducedMotionInput, OutputToSourceInput, AddSettingNameInput,
            ShutdownInput, OpenOutputInput, OperationSoundInput, CompleteSoundInput, ErrorSoundInput, ContextMenuInput, TrayInput, CloseToTrayInput, CheckUpdatesInput, AutoUpdateInput, SilentUpdateInput, BetaInput, AutoRepairModelInput })
        { _values.Add(()=>input.IsChecked); input.PropertyChanged += (_, args) => { if (args.Property == CheckBox.IsCheckedProperty) MarkDirty(); }; }
        foreach (var input in new[] { ThreadsInput, JpegQualityInput, WebpQualityInput, ParallelInput })
        { _values.Add(()=>input.Text); input.PropertyChanged += (_, args) => { if (args.Property == NumericUpDown.ValueProperty || args.Property==NumericUpDown.TextProperty) MarkDirty(); }; }
        MultithreadInput.PropertyChanged += (_, args) => { if (args.Property == CheckBox.IsCheckedProperty) ThreadsInput.IsEnabled = MultithreadInput.IsChecked == true; };
        AutoUpdateInput.IsCheckedChanged += (_, _) => SilentUpdateInput.IsEnabled = AutoUpdateInput.IsChecked == true;
        InitializeProviderManagement();
        _appliedValues=_values.Select(value=>value()).ToArray();
        InitializeModelManagement();
        InitializeWordLibraryManagement();
        ContextMenuInput.IsEnabled = _services.CanUseContextMenu; TrayInput.IsEnabled = CloseToTrayInput.IsEnabled = _services.CanUseTray;
        PlayerIntegrationRow.IsVisible = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();
        if (OperatingSystem.IsMacOS())
        {
            RegisterPlayerButton.Content = Localization.Text("注册播放打开方式");
            DefaultPlayerButton.Content = Localization.Text("打开应用位置");
        }
        ContextMenuInput.Content = OperatingSystem.IsMacOS() ? "添加到 Finder 快速操作" : "添加到系统上下文菜单";
        ToolTip.SetTip(ContextMenuInput, OperatingSystem.IsMacOS() ? "安装到当前用户的 Finder 服务；可在系统设置的扩展中管理。" : "添加当前用户的资源管理器菜单；Windows 11 可能位于“显示更多选项”。");
        if (!_services.CanUseTray) { ToolTip.SetTip(TrayInput, "当前环境不提供系统托盘，使用正常窗口最小化。"); ToolTip.SetTip(CloseToTrayInput, "当前环境不提供系统托盘，关闭窗口会退出应用。"); }
        Closed += (_, _) => { Localization.Changed -= LanguageChanged; _lifetime.Cancel(); _lifetime.Dispose(); if (_ownsServices) _services.Dispose(); };
        AddHandler(Button.ClickEvent,(_,_)=>{if(_settings.PlayOperationSound)_services.PlaySound(UiSound.Operation);},RoutingStrategies.Bubble);
    }

    public AppSettings ReadSettings()
    {
        var draft = _settings.Clone();
        draft.OutputFolder = OutputInput.Text?.Trim() ?? "";
        draft.FFmpegPath = MediaEngine.UsesBundledTools ? "" : FfmpegInput.Text?.Trim() ?? "";
        draft.FFprobePath = MediaEngine.UsesBundledTools ? "" : FfprobeInput.Text?.Trim() ?? "";
        draft.YtDlpPath = MediaEngine.UsesBundledTools ? "" : YtdlpInput.Text?.Trim() ?? "";
        draft.ParallelJobs = Number(ParallelInput, "同时执行任务数");
        draft.MultiThread = MultithreadInput.IsChecked == true; draft.CpuThreads = Number(ThreadsInput, "每个任务的线程数");
        draft.AutoDetectGpu = AutoGpuInput.IsChecked == true; draft.JpegQuality = Number(JpegQualityInput, "JPEG 质量"); draft.WebpQuality = Number(WebpQualityInput, "WebP 质量");
        draft.NotifyComplete = NotifyInput.IsChecked == true; draft.ReduceMotion = ReducedMotionInput.IsChecked == true;
        draft.OutputToSource = OutputToSourceInput.IsChecked == true; draft.AddSettingName = AddSettingNameInput.IsChecked == true;
        draft.ShutdownOnComplete = ShutdownInput.IsChecked == true; draft.OpenOutputFolderOnComplete = OpenOutputInput.IsChecked == true;
        draft.PlayOperationSound = OperationSoundInput.IsChecked == true; draft.PlayCompleteSound = CompleteSoundInput.IsChecked == true;
        draft.PlayErrorSound = ErrorSoundInput.IsChecked == true; draft.SystemContextMenu = ContextMenuInput.IsChecked == true;
        draft.MinimizeToTray = TrayInput.IsChecked == true; draft.CheckForUpdates = CheckUpdatesInput.IsChecked == true;
        draft.CloseToTray = CloseToTrayInput.IsChecked == true;
        draft.AutoUpdate = AutoUpdateInput.IsChecked == true; draft.SilentUpdate = SilentUpdateInput.IsChecked == true;
        draft.EnableBetaFeatures = BetaInput.IsChecked == true; draft.AutoDownloadRepairModel = AutoRepairModelInput.IsChecked == true;
        draft.OnlineAi = _providerDraft.Clone();
        SettingsPolicy.Validate(draft); draft.OutputFolder = Path.GetFullPath(draft.OutputFolder); return draft;
    }
    private int Number(NumericUpDown input, string label)
    {
        if (!decimal.TryParse(input.Text,NumberStyles.Integer,input.NumberFormat,out var value) || value<input.Minimum || value>input.Maximum)
        {
            SettingsTabs.SelectedItem = input.GetLogicalAncestors().OfType<TabItem>().First();
            input.Focus();
            throw new ArgumentException(Localization.Format($"{Localization.Key(label)}：请输入 {input.Minimum} 到 {input.Maximum} 之间的整数。"));
        }
        return decimal.ToInt32(value);
    }
    private void Populate(AppSettings source)
    {
        _initializing = true;
        ExternalToolPaths.IsVisible = !MediaEngine.UsesBundledTools;
        OutputInput.Text = source.OutputFolder; FfmpegInput.Text = source.FFmpegPath; FfprobeInput.Text = source.FFprobePath; YtdlpInput.Text = source.YtDlpPath;
        ParallelInput.Value = Math.Clamp(source.ParallelJobs, 1, 8); AutoGpuInput.IsChecked = source.AutoDetectGpu;
        MultithreadInput.IsChecked = source.MultiThread; ThreadsInput.Value = Math.Clamp(source.CpuThreads, 1, 16); ThreadsInput.IsEnabled = source.MultiThread;
        JpegQualityInput.Value = Math.Clamp(source.JpegQuality, 1, 100); WebpQualityInput.Value = Math.Clamp(source.WebpQuality, 1, 100);
        NotifyInput.IsChecked = source.NotifyComplete; ReducedMotionInput.IsChecked = source.ReduceMotion;
        OutputToSourceInput.IsChecked = source.OutputToSource; AddSettingNameInput.IsChecked = source.AddSettingName;
        ShutdownInput.IsChecked = source.ShutdownOnComplete; OpenOutputInput.IsChecked = source.OpenOutputFolderOnComplete;
        OperationSoundInput.IsChecked = source.PlayOperationSound; CompleteSoundInput.IsChecked = source.PlayCompleteSound;
        ErrorSoundInput.IsChecked = source.PlayErrorSound; ContextMenuInput.IsChecked = source.SystemContextMenu;
        TrayInput.IsChecked = source.MinimizeToTray; CheckUpdatesInput.IsChecked = source.CheckForUpdates;
        CloseToTrayInput.IsChecked = source.CloseToTray;
        AutoUpdateInput.IsChecked = source.AutoUpdate; SilentUpdateInput.IsChecked = source.SilentUpdate;
        BetaInput.IsChecked = source.EnableBetaFeatures; AutoRepairModelInput.IsChecked = source.AutoDownloadRepairModel;
        PopulateProviders(source.OnlineAi);
        SilentUpdateInput.IsEnabled = source.AutoUpdate;
        RuntimeInfo.Text = RuntimeDescription;
        StatusText.IsVisible = false; _initializing = false;
    }
    private static string RuntimeDescription => Localization.Format($"{AppIdentity.WindowTitle}\n.NET {Environment.Version}\n{System.Runtime.InteropServices.RuntimeInformation.OSDescription}\nCPU logical processors: {Environment.ProcessorCount}\nSettings: {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AvaMedia")}");
    private void LanguageChanged(object? sender, EventArgs e) => RuntimeInfo.Text = RuntimeDescription;
    private void MarkDirty() { if (!_initializing) { ApplyButton.IsEnabled = !_values.Select(value=>value()).SequenceEqual(_appliedValues); StatusText.IsVisible = false; } }
    private bool ApplyDraft()
    {
        try
        {
            var draft = ReadSettings(); var prior = _settings.Clone(); _settings.CopyFrom(draft);
            try { Applied?.Invoke(this, EventArgs.Empty); }
            catch { _settings.CopyFrom(prior); throw; }
            _applied = true; _appliedValues=_values.Select(value=>value()).ToArray(); ApplyButton.IsEnabled = false; StatusText.IsVisible = false; return true;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; StatusText.IsVisible = true;if(_settings.PlayErrorSound)_services.PlaySound(UiSound.Error); return false; }
    }
    private void ApplyClick(object? sender, RoutedEventArgs args) => ApplyDraft();
    private void OkClick(object? sender, RoutedEventArgs args) { if (ApplyDraft()) Close(true); }
    private void CancelClick(object? sender, RoutedEventArgs args) => Close(_applied);
    private void ResetClick(object? sender, RoutedEventArgs args) { Populate(new AppSettings { Theme = _settings.Theme, Language = _settings.Language }); MarkDirty(); }
    private async void HardwareTestClick(object? sender, RoutedEventArgs args)
    {
        HardwareTestButton.IsEnabled = false;
        try { await new HardwareTestWindow(FfmpegInput.Text?.Trim() ?? "").ShowDialog(this); }
        finally { HardwareTestButton.IsEnabled = true; }
    }
    private async void OutputBrowseClick(object? sender, RoutedEventArgs args) { if (await Ui.Folder(this, "选择输出文件夹") is { } path) OutputInput.Text = path; }
    private async Task Browse(TextBox input) { if ((await Ui.Pick(this, "选择可执行文件", false)).FirstOrDefault() is { } path) input.Text = path; }
    private async void FfmpegBrowseClick(object? sender, RoutedEventArgs args) => await Browse(FfmpegInput);
    private async void FfprobeBrowseClick(object? sender, RoutedEventArgs args) => await Browse(FfprobeInput);
    private async void YtdlpBrowseClick(object? sender, RoutedEventArgs args) => await Browse(YtdlpInput);
    private async void CheckUpdatesClick(object? sender, RoutedEventArgs args)
    {
        CheckUpdatesButton.IsEnabled = false;
        try { var result = await _services.CheckUpdatesAsync(_lifetime.Token); if (IsVisible) Notifications.UpdateNotifications.Show(this, result); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsVisible) Notifications.UpdateNotifications.Error(this, "版本检查失败", ex.Message); }
        finally { if (IsVisible) CheckUpdatesButton.IsEnabled = true; }
    }
    private async void RegisterPlayerClick(object? sender, RoutedEventArgs args)
    {
        RegisterPlayerButton.IsEnabled = false;
        try
        {
            if (OperatingSystem.IsMacOS()) await SystemPlayerIntegration.RegisterMacAsync(_lifetime.Token);
            else await Task.Run(() => SystemPlayerIntegration.RegisterWindows(SystemPlayerIntegration.ExecutablePath), _lifetime.Token);
            if (IsVisible) { StatusText.Text = Localization.Text(OperatingSystem.IsMacOS() ? "已注册播放打开方式。" : "已注册播放器，可在文件打开方式中选择。"); StatusText.IsVisible = true; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (IsVisible) { StatusText.Text = ex.Message; StatusText.IsVisible = true; } }
        finally { if (IsVisible) RegisterPlayerButton.IsEnabled = true; }
    }
    private async void DefaultPlayerClick(object? sender, RoutedEventArgs args)
    {
        try
        {
            if (OperatingSystem.IsMacOS()) SystemPlayerIntegration.RevealMacPlayer();
            else SystemPlayerIntegration.OpenDefaultApps();
        }
        catch (Exception ex) { await Ui.Message(this, "播放器设置失败", ex.Message); }
    }
}

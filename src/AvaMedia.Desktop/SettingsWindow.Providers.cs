using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using AvaMedia.Core;

namespace AvaMedia.Desktop;

public sealed partial class SettingsWindow
{
    private OnlineAiSettings _providerDraft = new();
    private string? _providerId;
    private bool _populatingProvider;
    private int _providerRevision;
    private CancellationTokenSource? _providerRequest;
    private OnlineAiOptions? SelectedProvider => _providerDraft.Providers.Find(p => p.Id == _providerId);
    private sealed record ProviderChoice(string Id, string Name);

    private void InitializeProviderManagement()
    {
        OnlineProviderInput.ItemTemplate = new FuncDataTemplate<ProviderChoice>((item, _) =>
        { var label = Ui.Text(item?.Name ?? ""); Localization.SetIsUserText(label, true); return label; });
        _values.Add(() => ProviderFingerprint());
        OnlineProviderInput.SelectionChanged += (_, _) =>
        {
            if (_populatingProvider) return;
            CancelProviderRequest(); _providerId = (OnlineProviderInput.SelectedItem as ProviderChoice)?.Id;
            PopulateProviderEditor();
        };
        foreach (var input in new[] { OnlineNameInput, OnlineEndpointInput, OnlineKeyInput })
            input.PropertyChanged += (_, change) =>
            { if (change.Property == TextBox.TextProperty) ProviderEdited(input != OnlineNameInput); };
        foreach (var input in new[] { OnlineTextModelInput, OnlineVisionModelInput })
            input.PropertyChanged += (_, change) => { if (change.Property == AutoCompleteBox.TextProperty) ProviderEdited(false); };
        OnlineEnabledInput.IsCheckedChanged += (_, _) => ProviderEdited(false);
        OnlineTokenInput.SelectionChanged += (_, _) => ProviderEdited(false);
        OnlineFormatInput.SelectionChanged += (_, _) => ProviderEdited(false);
        OnlineTimeoutInput.PropertyChanged += (_, change) =>
        { if (change.Property == NumericUpDown.ValueProperty || change.Property == NumericUpDown.TextProperty) ProviderEdited(false); };
        Closed += (_, _) => CancelProviderRequest();
    }

    private string ProviderFingerprint()
    {
        // Keys affect Apply state without becoming part of a serializable settings object.
        var text = JsonSerializer.Serialize(_providerDraft) + JsonSerializer.Serialize(_providerDraft.Providers.Select(p => p.ApiKey));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private void PopulateProviders(OnlineAiSettings source)
    {
        CancelProviderRequest(); _providerDraft = source.Clone();
        _providerId = _providerDraft.Providers.Any(p => p.Id == _providerDraft.DefaultProviderId)
            ? _providerDraft.DefaultProviderId : _providerDraft.Providers.FirstOrDefault()?.Id;
        RefreshProviderChoices(); PopulateProviderEditor();
    }

    private void RefreshProviderChoices()
    {
        _populatingProvider = true;
        try
        {
            var choices = _providerDraft.Providers.Select(p => new ProviderChoice(p.Id, p.Name)).ToArray();
            OnlineProviderInput.ItemsSource = choices;
            OnlineProviderInput.SelectedItem = choices.FirstOrDefault(p => p.Id == _providerId);
        }
        finally { _populatingProvider = false; }
    }

    private void PopulateProviderEditor()
    {
        _populatingProvider = true;
        try
        {
            var provider = SelectedProvider; OnlineProviderSection.IsVisible = provider is not null;
            DeleteProviderButton.IsEnabled = provider is not null; OnlineProviderInput.IsVisible = _providerDraft.Providers.Count != 0;
            if (provider is null) return;
            OnlineNameInput.Text = provider.Name; OnlineEndpointInput.Text = provider.Endpoint;
            OnlineKeyInput.Text = provider.ApiKey; OnlineKeyInput.PasswordChar = '●'; RevealProviderKeyButton.Content = Localization.Text("显示密钥");
            OnlineEnabledInput.IsChecked = provider.Enabled;
            SetProviderModels(provider);
            OnlineTokenInput.SelectedIndex = (int)provider.TokenLimit; OnlineFormatInput.SelectedIndex = (int)provider.ResponseFormat;
            OnlineTimeoutInput.Value = provider.TimeoutSeconds;
            ProviderStatus.IsVisible = false;
            ProviderDocsButton.IsVisible = OnlineAiPresets.All.Any(p => p.Id == provider.Preset && p.Documentation.Length != 0);
            ProviderSetupSection.IsVisible = OnlineAiPresets.All.Any(p => p.Id == provider.Preset && p.ApiKeyPage.Length != 0);
            RefreshProviderDefault();
        }
        finally { _populatingProvider = false; }
    }

    private void SetProviderModels(OnlineAiOptions provider)
    {
        OnlineTextModelInput.ItemsSource = provider.ModelIds; OnlineVisionModelInput.ItemsSource = provider.ModelIds;
        OnlineTextModelInput.Text = provider.TextModel; OnlineVisionModelInput.Text = provider.VisionModel;
        RefreshProviderLimits(provider);
    }

    private void RefreshProviderLimits(OnlineAiOptions provider)
    {
        var selected = new[] { provider.TextModel, provider.EffectiveVisionModel }.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal);
        var lines = selected.Select(id =>
        {
            var model = provider.ModelInfo.FirstOrDefault(model => model.Id == id);
            var parts = new List<string>();
            if (model?.ContextTokens is { } context) parts.Add(Localization.Format($"上下文 {context} token"));
            parts.Add(model?.MaxOutputTokens is { } output ? Localization.Format($"最大输出 {output} token") : Localization.Text("输出上限由供应商决定"));
            return id + " · " + Localization.Join(" · ", parts);
        }).ToArray();
        OnlineLimitsText.Text = string.Join(Environment.NewLine, lines); OnlineLimitsText.IsVisible = lines.Length > 0;
        Localization.SetIsUserText(OnlineLimitsText, true);
    }

    private void ProviderEdited(bool connectionChanged)
    {
        if (_populatingProvider || _initializing || SelectedProvider is not { } provider) return;
        CancelProviderRequest();
        provider.Name = OnlineNameInput.Text?.Trim() ?? ""; provider.Endpoint = OnlineEndpointInput.Text?.Trim() ?? "";
        provider.ApiKey = OnlineKeyInput.Text?.Trim() ?? ""; provider.Enabled = OnlineEnabledInput.IsChecked == true;
        provider.TextModel = OnlineTextModelInput.Text?.Trim() ?? ""; provider.VisionModel = OnlineVisionModelInput.Text?.Trim() ?? "";
        provider.TokenLimit = (OnlineAiTokenLimit)OnlineTokenInput.SelectedIndex;
        provider.ResponseFormat = (OnlineAiResponseFormat)OnlineFormatInput.SelectedIndex;
        provider.TimeoutSeconds = int.TryParse(OnlineTimeoutInput.Text, out var seconds) ? seconds : 0;
        if (connectionChanged)
        {
            provider.ModelIds = []; provider.ModelInfo = []; _populatingProvider = true;
            try { SetProviderModels(provider); } finally { _populatingProvider = false; }
        }
        if (_providerDraft.DefaultProviderId == provider.Id && !provider.Enabled)
            _providerDraft.DefaultProviderId = _providerDraft.Providers.FirstOrDefault(p => p.Enabled)?.Id ?? "";
        ProviderStatus.IsVisible = false; RefreshProviderLimits(provider); RefreshProviderChoices(); RefreshProviderDefault(); MarkDirty();
    }

    private void RefreshProviderDefault()
    {
        var provider = SelectedProvider;
        var isDefault = provider is not null && provider.Enabled && (_providerDraft.DefaultProviderId == provider.Id
            || _providerDraft.DefaultProviderId.Length == 0 && _providerDraft.Providers.FirstOrDefault(p => p.Enabled)?.Id == provider.Id);
        DefaultProviderButton.Content = Localization.Text(isDefault ? "默认供应商" : "设为默认");
        DefaultProviderButton.IsEnabled = provider?.Enabled == true && !isDefault;
    }

    private void AddProviderClick(object? sender, RoutedEventArgs args)
    {
        var menu = new ContextMenu(); var items = new List<MenuItem>();
        foreach (var preset in OnlineAiPresets.All)
        {
            var item = new MenuItem { Header = Localization.Text(preset.Name) };
            item.Click += (_, _) => AddProvider(preset.Id); items.Add(item);
        }
        menu.ItemsSource = items; menu.Open(AddProviderButton);
    }

    private void AddProvider(string preset)
    {
        if (_providerDraft.Providers.Count >= 32) { ShowProviderStatus("最多添加 32 个供应商。"); return; }
        CancelProviderRequest(); var provider = OnlineAiPresets.Create(preset); var name = new string(Localization.Text(provider.Name).AsSpan());
        provider.Name = name; var suffix = 2;
        while (_providerDraft.Providers.Any(p => string.Equals(p.Name, provider.Name, StringComparison.OrdinalIgnoreCase))) provider.Name = name + " " + suffix++;
        _providerDraft.Providers.Add(provider); _providerId = provider.Id;
        RefreshProviderChoices(); PopulateProviderEditor(); MarkDirty(); OnlineNameInput.Focus();
    }

    private void DeleteProviderClick(object? sender, RoutedEventArgs args)
    {
        if (SelectedProvider is not { } provider) return;
        CancelProviderRequest(); _providerDraft.Providers.Remove(provider);
        if (_providerDraft.DefaultProviderId == provider.Id) _providerDraft.DefaultProviderId = _providerDraft.Providers.FirstOrDefault(p => p.Enabled)?.Id ?? "";
        _providerId = _providerDraft.Providers.FirstOrDefault()?.Id;
        RefreshProviderChoices(); PopulateProviderEditor(); MarkDirty();
    }

    private void DefaultProviderClick(object? sender, RoutedEventArgs args)
    { if (SelectedProvider is not { Enabled: true } provider) return; _providerDraft.DefaultProviderId = provider.Id; RefreshProviderDefault(); MarkDirty(); }

    private void RevealProviderKeyClick(object? sender, RoutedEventArgs args)
    {
        var reveal = OnlineKeyInput.PasswordChar != '\0'; OnlineKeyInput.PasswordChar = reveal ? '\0' : '●';
        RevealProviderKeyButton.Content = Localization.Text(reveal ? "隐藏密钥" : "显示密钥");
    }

    private async void ProviderDocsClick(object? sender, RoutedEventArgs args)
    {
        if (OnlineAiPresets.All.FirstOrDefault(p => p.Id == SelectedProvider?.Preset)?.Documentation is not { Length: > 0 } url) return;
        await OpenProviderPage(url);
    }

    private async void ProviderApiKeyClick(object? sender, RoutedEventArgs args)
    {
        if (OnlineAiPresets.All.FirstOrDefault(p => p.Id == SelectedProvider?.Preset)?.ApiKeyPage is not { Length: > 0 } url) return;
        await OpenProviderPage(url);
    }

    private async Task OpenProviderPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception error) { await Ui.Message(this, "打开失败", error.Message); }
    }

    private void CancelProviderRequest()
    {
        _providerRevision++; _providerRequest?.Cancel(); _providerRequest = null;
        FetchProviderModelsButton.IsEnabled = true; FetchProviderModelsButton.Content = Localization.Text("连接并获取模型");
        CancelProviderModelsButton.IsVisible = false;
    }
    private void CancelProviderModelsClick(object? sender, RoutedEventArgs args) => _providerRequest?.Cancel();
    private void ShowProviderStatus(string text) { ProviderStatus.Text = Localization.Text(text); ProviderStatus.IsVisible = true; }

    private async void FetchProviderModelsClick(object? sender, RoutedEventArgs args)
    {
        if (SelectedProvider is not { } provider) return;
        CancelProviderRequest(); var snapshot = provider.Clone(); var revision = _providerRevision;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _providerRequest = cancellation;
        FetchProviderModelsButton.IsEnabled = false; FetchProviderModelsButton.Content = Localization.Text("连接中…");
        CancelProviderModelsButton.IsVisible = true; ProviderStatus.IsVisible = false;
        bool Current() => !cancellation.IsCancellationRequested && _providerRequest == cancellation && _providerId == snapshot.Id && revision == _providerRevision;
        try
        {
            using var client = new OnlineAiClient(snapshot); var models = await client.GetModelsAsync(cancellation.Token);
            foreach (var id in new[] { snapshot.TextModel, snapshot.EffectiveVisionModel }.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal))
            {
                var index = Array.FindIndex(models, model => model.Id == id);
                if (index >= 0 && models[index].HasLimits) continue;
                var detail = await client.GetModelInfoAsync(id, cancellation.Token);
                if (detail is null) continue;
                if (index >= 0) models[index] = detail; else models = [.. models, detail];
            }
            if (!Current()) return;
            provider.ModelIds = models.Select(model => model.Id).ToArray(); provider.ModelInfo = models; _populatingProvider = true;
            try { SetProviderModels(provider); } finally { _populatingProvider = false; }
            ProviderStatus.Text = Localization.Format($"已连接，读取 {models.Length} 个模型。"); ProviderStatus.IsVisible = true; MarkDirty();
        }
        catch (OperationCanceledException)
        { if (_providerRequest == cancellation && !_lifetime.IsCancellationRequested) ShowProviderStatus("已取消"); }
        catch (Exception error) { if (Current()) ShowProviderStatus(error.Message); }
        finally { if (_providerRequest == cancellation) CancelProviderRequest(); }
    }
}

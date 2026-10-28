using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveStream.Application.Abstractions;
using LiveStream.Extensions.Integrations;
using LiveStream.Application.Studio;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Integrations;

namespace LiveStream.Desktop.ViewModels;

public sealed partial class IntegrationsViewModel : ObservableObject
{
    private readonly StudioSession _session;
    private readonly IntegrationManager _manager;
    private readonly ISecretStore _secrets;

    public ObservableCollection<PlatformIntegrationRowViewModel> Platforms { get; } = [];

    [ObservableProperty] private PlatformIntegrationRowViewModel? _selectedPlatform;
    [ObservableProperty] private string _accountName = string.Empty;
    [ObservableProperty] private string _accountId = string.Empty;
    [ObservableProperty] private string _secretToken = string.Empty;
    [ObservableProperty] private bool _enableChat = true;
    [ObservableProperty] private bool _enableAlerts = true;
    [ObservableProperty] private bool _autoReconnect = true;
    [ObservableProperty] private string _statusMessage = string.Empty;
    [ObservableProperty] private bool _isBusy;

    public IntegrationsViewModel(StudioSession session, IntegrationManager manager, ISecretStore secrets)
    {
        _session = session;
        _manager = manager;
        _secrets = secrets;

        _manager.StatusChanged += (_, ev) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                var row = Platforms.FirstOrDefault(p => p.Platform == ev.Platform);
                if (row is not null)
                {
                    var updatedConfig = _session.Project.GetIntegration(ev.Platform) with { Status = ev.Status };
                    _session.Project.SetIntegration(updatedConfig);
                    row.Config = updatedConfig;
                }
            });
        };

        ReloadPlatforms();
    }

    public void ReloadPlatforms()
    {
        Platforms.Clear();
        foreach (var platform in new[] { StreamPlatform.YouTube, StreamPlatform.Twitch, StreamPlatform.TikTok, StreamPlatform.Facebook, StreamPlatform.Instagram })
        {
            var config = _session.Project.GetIntegration(platform);
            Platforms.Add(new PlatformIntegrationRowViewModel(config));
        }

        if (Platforms.Count > 0)
            SelectedPlatform = Platforms[0];
    }

    partial void OnSelectedPlatformChanged(PlatformIntegrationRowViewModel? value)
    {
        if (value is null) return;
        var c = value.Config;
        AccountName = c.AccountName;
        AccountId = c.AccountId;
        EnableChat = c.EnableChat;
        EnableAlerts = c.EnableAlerts;
        AutoReconnect = c.AutoReconnect;
        StatusMessage = c.ErrorMessage ?? string.Empty;

        // Carga la clave protegida
        _ = Task.Run(async () =>
        {
            var token = await _secrets.GetAsync(c.CredentialName, CancellationToken.None).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => SecretToken = token ?? string.Empty);
        });
    }

    [RelayCommand]
    public async Task SaveAndConnectAsync()
    {
        if (SelectedPlatform is null) return;
        IsBusy = true;
        StatusMessage = "Conectando…";

        var platform = SelectedPlatform.Platform;
        var current = _session.Project.GetIntegration(platform);
        var updated = current with
        {
            AccountName = AccountName.Trim(),
            AccountId = AccountId.Trim(),
            EnableChat = EnableChat,
            EnableAlerts = EnableAlerts,
            AutoReconnect = AutoReconnect,
            Enabled = true,
        };

        _session.Project.SetIntegration(updated);
        SelectedPlatform.Config = updated;

        if (!string.IsNullOrWhiteSpace(SecretToken))
            await _secrets.SetAsync(updated.CredentialName, SecretToken.Trim(), CancellationToken.None).ConfigureAwait(false);

        var success = await _manager.ConnectPlatformAsync(updated, CancellationToken.None).ConfigureAwait(false);
        IsBusy = false;
        StatusMessage = success ? "¡Conexión establecida con éxito!" : (_manager.GetConnector(platform)?.LastError ?? "Fallo al conectar.");
    }

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        if (SelectedPlatform is null) return;
        IsBusy = true;
        var platform = SelectedPlatform.Platform;
        await _manager.DisconnectPlatformAsync(platform, CancellationToken.None).ConfigureAwait(false);

        var current = _session.Project.GetIntegration(platform) with { Enabled = false, Status = IntegrationStatus.Disconnected };
        _session.Project.SetIntegration(current);
        SelectedPlatform.Config = current;

        IsBusy = false;
        StatusMessage = "Desconectado.";
    }
}

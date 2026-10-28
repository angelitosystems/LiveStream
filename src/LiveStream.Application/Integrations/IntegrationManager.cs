using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Application.Abstractions;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Integrations;

namespace LiveStream.Application.Integrations;

/// <summary>
/// Gestor centralizado de integraciones y conexiones con plataformas sociales.
/// Orquesta la autenticación segura mediante ISecretStore, la recolección de eventos y el chat unificado.
/// </summary>
public sealed class IntegrationManager : IAsyncDisposable
{
    private readonly ISecretStore _secretStore;
    private readonly ConcurrentDictionary<StreamPlatform, IPlatformConnector> _connectors = new();

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<AlertEvent>? AlertReceived;
    public event EventHandler<(StreamPlatform Platform, IntegrationStatus Status)>? StatusChanged;
    public event EventHandler<StreamPlatform>? ChatCleared;

    public IntegrationManager(ISecretStore secretStore)
    {
        _secretStore = secretStore;

        Register(new TwitchConnector());
        Register(new YouTubeConnector());
        Register(new TikTokConnector());
        Register(new FacebookConnector());
        Register(new InstagramConnector());
    }

    private void Register(IPlatformConnector connector)
    {
        _connectors[connector.Platform] = connector;

        connector.MessageReceived += (_, msg) => MessageReceived?.Invoke(this, msg);
        connector.AlertReceived += (_, alert) => AlertReceived?.Invoke(this, alert);
        connector.StatusChanged += (_, status) => StatusChanged?.Invoke(this, (connector.Platform, status));
        connector.ChatCleared += (_, _) => ChatCleared?.Invoke(this, connector.Platform);
    }

    public IPlatformConnector? GetConnector(StreamPlatform platform) =>
        _connectors.TryGetValue(platform, out var c) ? c : null;

    public IReadOnlyCollection<IPlatformConnector> AllConnectors => _connectors.Values.ToList();

    public async Task<bool> ConnectPlatformAsync(PlatformIntegrationConfig config, CancellationToken ct = default)
    {
        if (!_connectors.TryGetValue(config.Platform, out var connector))
            return false;

        var token = !string.IsNullOrWhiteSpace(config.CredentialName)
            ? await _secretStore.GetAsync(config.CredentialName, ct).ConfigureAwait(false)
            : null;

        return await connector.ConnectAsync(config, token, ct).ConfigureAwait(false);
    }

    public async Task DisconnectPlatformAsync(StreamPlatform platform, CancellationToken ct = default)
    {
        if (_connectors.TryGetValue(platform, out var connector))
            await connector.DisconnectAsync(ct).ConfigureAwait(false);
    }

    public async Task ConnectAllEnabledAsync(IEnumerable<PlatformIntegrationConfig> configs, CancellationToken ct = default)
    {
        foreach (var config in configs.Where(c => c.Enabled))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ConnectPlatformAsync(config, ct).ConfigureAwait(false);
            }
            catch { }
        }
    }

    public async Task DisconnectAllAsync(CancellationToken ct = default)
    {
        foreach (var connector in _connectors.Values)
        {
            try
            {
                await connector.DisconnectAsync(ct).ConfigureAwait(false);
            }
            catch { }
        }
    }

    /// <summary>
    /// Inyecta un mensaje de prueba local para validación y previsualización de overlays sin requerir un directo activo.
    /// </summary>
    public void SimulateMessage(ChatMessage message) => MessageReceived?.Invoke(this, message);

    /// <summary>
    /// Inyecta una alerta de prueba local para previsualizar animaciones, fuentes y sonidos.
    /// </summary>
    public void SimulateAlert(AlertEvent alert) => AlertReceived?.Invoke(this, alert);

    public async ValueTask DisposeAsync()
    {
        await DisconnectAllAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (var connector in _connectors.Values)
            await connector.DisposeAsync().ConfigureAwait(false);
        _connectors.Clear();
    }
}

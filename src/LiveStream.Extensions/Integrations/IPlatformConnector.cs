using System;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Integrations;

namespace LiveStream.Extensions.Integrations;

/// <summary>
/// Contrato común para conectores de plataformas sociales y de streaming.
/// Cada conector se encarga de la conexión oficial, autenticación y normalización de eventos y chat.
/// </summary>
public interface IPlatformConnector : IAsyncDisposable
{
    StreamPlatform Platform { get; }
    IntegrationStatus Status { get; }
    string? LastError { get; }
    DateTime? LastConnected { get; }

    event EventHandler<ChatMessage>? MessageReceived;
    event EventHandler<AlertEvent>? AlertReceived;
    event EventHandler<IntegrationStatus>? StatusChanged;
    event EventHandler? ChatCleared;

    Task<bool> ConnectAsync(PlatformIntegrationConfig config, string? secretToken, CancellationToken ct);
    Task DisconnectAsync(CancellationToken ct);
    Task<bool> ValidateApiAsync(PlatformIntegrationConfig config, string? secretToken, CancellationToken ct);
}

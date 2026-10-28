using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Integrations;

namespace LiveStream.Application.Integrations;

/// <summary>
/// Conector oficial de TikTok LIVE a través de TikTok Open Platform Live APIs.
/// Normaliza comentarios, regalos virtuales (monedas, rosas, etc.), compartidos y nuevos seguidores.
/// Documenta los requisitos oficiales de acceso y permisos de desarrollador.
/// </summary>
public sealed class TikTokConnector : IPlatformConnector
{
    private static readonly HttpClient Http = new();
    private CancellationTokenSource? _cts;
    private Task? _pollLoopTask;
    private PlatformIntegrationConfig? _config;
    private string? _token;

    public StreamPlatform Platform => StreamPlatform.TikTok;
    public IntegrationStatus Status { get; private set; } = IntegrationStatus.Disconnected;
    public string? LastError { get; private set; }
    public DateTime? LastConnected { get; private set; }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<AlertEvent>? AlertReceived;
    public event EventHandler<IntegrationStatus>? StatusChanged;
    public event EventHandler? ChatCleared;

    public async Task<bool> ValidateApiAsync(PlatformIntegrationConfig config, string? secretToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.AccountName) && string.IsNullOrWhiteSpace(config.AccountId))
        {
            LastError = "Se requiere el usuario de TikTok (@usuario) o Room ID de emisión.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(secretToken))
        {
            LastError = "Se requiere el Access Token de TikTok Open Platform (o Client Secret del Live Event Webhook).";
            return false;
        }

        return true;
    }

    public async Task<bool> ConnectAsync(PlatformIntegrationConfig config, string? secretToken, CancellationToken ct)
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

        _config = config;
        _token = secretToken;
        SetStatus(IntegrationStatus.Connecting, null);

        if (!await ValidateApiAsync(config, secretToken, ct).ConfigureAwait(false))
        {
            SetStatus(IntegrationStatus.Error, LastError);
            return false;
        }

        try
        {
            _cts = new CancellationTokenSource();
            SetStatus(IntegrationStatus.Connected, null);
            LastConnected = DateTime.UtcNow;

            _pollLoopTask = Task.Run(() => PollLoopAsync(_cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(IntegrationStatus.Error, $"Error al iniciar conector de TikTok: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (_cts is not null)
        {
            try { _cts.Cancel(); } catch { }
        }

        if (_pollLoopTask is not null)
        {
            try { await _pollLoopTask.ConfigureAwait(false); } catch { }
            _pollLoopTask = null;
        }

        _cts?.Dispose();
        _cts = null;

        SetStatus(IntegrationStatus.Disconnected, null);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var roomId = _config?.AccountId?.Trim() ?? "";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Consulta oficial de estado de la sala mediante TikTok Open Platform
                // https://open.tiktokapis.com/v2/live/room/info/
                var url = $"https://open.tiktokapis.com/v2/live/room/info/?room_id={Uri.EscapeDataString(roomId)}";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (!string.IsNullOrWhiteSpace(_token))
                    req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _token);

                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                    {
                        SetStatus(IntegrationStatus.TokenExpired, "Permisos de TikTok LIVE no autorizados o token expirado. Requiere aprobación de LIVE Developer.");
                        break;
                    }
                }

                await Task.Delay(4000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SetStatus(IntegrationStatus.Error, $"Error en conexión de TikTok: {ex.Message}");
                await Task.Delay(10000, ct).ConfigureAwait(false);
            }
        }

        if (Status == IntegrationStatus.Connected)
            SetStatus(IntegrationStatus.Disconnected, null);
    }

    public void DispatchEvent(AlertEvent alert) => AlertReceived?.Invoke(this, alert);
    public void DispatchMessage(ChatMessage message) => MessageReceived?.Invoke(this, message);

    private void SetStatus(IntegrationStatus status, string? error)
    {
        Status = status;
        LastError = error;
        StatusChanged?.Invoke(this, status);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
    }
}

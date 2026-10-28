using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Integrations;

namespace LiveStream.Extensions.Integrations;

/// <summary>
/// Conector de Instagram Live mediante Instagram Graph API para creadores.
/// Requiere cuenta profesional de Instagram conectada a Meta for Developers y sesión de Instagram Live Producer.
/// </summary>
public sealed class InstagramConnector : IPlatformConnector
{
    private static readonly HttpClient Http = new();
    private CancellationTokenSource? _cts;
    private Task? _pollLoopTask;
    private PlatformIntegrationConfig? _config;
    private string? _token;

    public StreamPlatform Platform => StreamPlatform.Instagram;
    public IntegrationStatus Status { get; private set; } = IntegrationStatus.Disconnected;
    public string? LastError { get; private set; }
    public DateTime? LastConnected { get; private set; }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<AlertEvent>? AlertReceived;
    public event EventHandler<IntegrationStatus>? StatusChanged;
    public event EventHandler? ChatCleared;

    public async Task<bool> ValidateApiAsync(PlatformIntegrationConfig config, string? secretToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.AccountId))
        {
            LastError = "Se requiere el ID de contenido en vivo (Live Media ID) de Instagram.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(secretToken))
        {
            LastError = "Se requiere el User Access Token de Instagram Graph API con permisos instagram_manage_comments.";
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
            SetStatus(IntegrationStatus.Error, $"Error al conectar con Instagram: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (_cts is not null)
        {
            try { _cts.Cancel(); } catch (Exception ex) { AppLog.Warn("integraciones", $"Desconectar Instagram: {ex.Message}"); }
        }

        if (_pollLoopTask is not null)
        {
            try { await _pollLoopTask.ConfigureAwait(false); } catch (Exception ex) { AppLog.Warn("integraciones", $"Desconectar Instagram: {ex.Message}"); }
            _pollLoopTask = null;
        }

        _cts?.Dispose();
        _cts = null;

        SetStatus(IntegrationStatus.Disconnected, null);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var mediaId = _config?.AccountId?.Trim() ?? "";
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"https://graph.facebook.com/v19.0/{Uri.EscapeDataString(mediaId)}/comments?fields=id,text,username,timestamp&access_token={Uri.EscapeDataString(_token ?? "")}";
                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                    {
                        SetStatus(IntegrationStatus.TokenExpired, "Permisos de Instagram insuficientes o token expirado. Requiere cuenta profesional y permiso instagram_manage_comments.");
                        break;
                    }
                    await Task.Delay(10000, ct).ConfigureAwait(false);
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                {
                    foreach (var comment in data.EnumerateArray())
                    {
                        var commentId = comment.TryGetProperty("id", out var idp) ? idp.GetString() ?? "" : "";
                        var text = comment.TryGetProperty("text", out var tp) ? tp.GetString() ?? "" : "";
                        var username = comment.TryGetProperty("username", out var unp) ? unp.GetString() ?? "Usuario Instagram" : "Usuario Instagram";

                        var chatMsg = new ChatMessage(
                            Id: commentId,
                            Platform: StreamPlatform.Instagram,
                            AuthorName: username,
                            AuthorId: username,
                            Message: text,
                            Timestamp: DateTime.UtcNow);

                        MessageReceived?.Invoke(this, chatMsg);
                    }
                }

                await Task.Delay(4000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SetStatus(IntegrationStatus.Error, $"Error al obtener comentarios de Instagram: {ex.Message}");
                await Task.Delay(10000, ct).ConfigureAwait(false);
            }
        }

        if (Status == IntegrationStatus.Connected)
            SetStatus(IntegrationStatus.Disconnected, null);
    }

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

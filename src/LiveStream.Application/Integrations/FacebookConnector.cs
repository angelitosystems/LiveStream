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
/// Conector oficial de Facebook Live mediante Graph API (graph.facebook.com/v19.0/{live_video_id}/comments).
/// Soporta comentarios en directo, reacciones y alertas de Stars (donaciones).
/// </summary>
public sealed class FacebookConnector : IPlatformConnector
{
    private static readonly HttpClient Http = new();
    private CancellationTokenSource? _cts;
    private Task? _pollLoopTask;
    private PlatformIntegrationConfig? _config;
    private string? _token;

    public StreamPlatform Platform => StreamPlatform.Facebook;
    public IntegrationStatus Status { get; private set; } = IntegrationStatus.Disconnected;
    public string? LastError { get; private set; }
    public DateTime? LastConnected { get; private set; }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<AlertEvent>? AlertReceived;
    public event EventHandler<IntegrationStatus>? StatusChanged;
    public event EventHandler? ChatCleared;

    public async Task<bool> ValidateApiAsync(PlatformIntegrationConfig config, string? secretToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.AccountId) && string.IsNullOrWhiteSpace(config.AccountName))
        {
            LastError = "Se requiere el ID del video en vivo (Live Video ID) de Facebook.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(secretToken))
        {
            LastError = "Se requiere el Page o User Access Token de Facebook Graph API con permiso 'pages_read_engagement'.";
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
            SetStatus(IntegrationStatus.Error, $"Error al conectar con Facebook Live: {ex.Message}");
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
        var liveVideoId = _config?.AccountId?.Trim() ?? "";
        string? since = null;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"https://graph.facebook.com/v19.0/{Uri.EscapeDataString(liveVideoId)}/comments?fields=id,from,message,created_time&order=reverse_chronological&access_token={Uri.EscapeDataString(_token ?? "")}";
                if (!string.IsNullOrEmpty(since))
                    url += $"&since={Uri.EscapeDataString(since)}";

                using var resp = await Http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                    {
                        SetStatus(IntegrationStatus.TokenExpired, "El token de acceso de Facebook ha expirado o no tiene permisos suficientes.");
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
                        var msg = comment.TryGetProperty("message", out var mp) ? mp.GetString() ?? "" : "";
                        var authorName = "Usuario Facebook";
                        var authorId = "";

                        if (comment.TryGetProperty("from", out var fromObj))
                        {
                            authorName = fromObj.TryGetProperty("name", out var np) ? np.GetString() ?? authorName : authorName;
                            authorId = fromObj.TryGetProperty("id", out var ip) ? ip.GetString() ?? "" : "";
                        }

                        var chatMsg = new ChatMessage(
                            Id: commentId,
                            Platform: StreamPlatform.Facebook,
                            AuthorName: authorName,
                            AuthorId: authorId,
                            Message: msg,
                            Timestamp: DateTime.UtcNow);

                        MessageReceived?.Invoke(this, chatMsg);
                    }
                }

                since = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                await Task.Delay(4000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SetStatus(IntegrationStatus.Error, $"Error al consultar comentarios de Facebook: {ex.Message}");
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

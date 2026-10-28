using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Integrations;

namespace LiveStream.Extensions.Integrations;

/// <summary>
/// Conector oficial de YouTube Live a través de la API oficial YouTube Data v3 (liveChat/messages).
/// Procesa mensajes del chat en directo, Super Chats, Super Stickers y altas de miembros patrocinadores.
/// </summary>
public sealed class YouTubeConnector : IPlatformConnector
{
    private static readonly HttpClient Http = new();
    private CancellationTokenSource? _cts;
    private Task? _pollLoopTask;
    private PlatformIntegrationConfig? _config;
    private string? _token;

    public StreamPlatform Platform => StreamPlatform.YouTube;
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
            LastError = "Se requiere el ID de emisión en directo (Live Video ID) o Live Chat ID de YouTube.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(secretToken))
        {
            LastError = "Se requiere la API Key o Token OAuth de YouTube Data API v3.";
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
            SetStatus(IntegrationStatus.Error, $"Error al iniciar conector de YouTube: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (_cts is not null)
        {
            try { _cts.Cancel(); } catch (Exception ex) { AppLog.Warn("integraciones", $"YouTube: {ex.Message}"); }
        }

        if (_pollLoopTask is not null)
        {
            try { await _pollLoopTask.ConfigureAwait(false); } catch (Exception ex) { AppLog.Warn("integraciones", $"YouTube: {ex.Message}"); }
            _pollLoopTask = null;
        }

        _cts?.Dispose();
        _cts = null;

        SetStatus(IntegrationStatus.Disconnected, null);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        var liveChatId = _config?.AccountId?.Trim() ?? "";
        var nextPageToken = "";
        var pollingDelayMs = 3500;

        // Si no se proporcionó el liveChatId directo sino el ID de video, se resuelve primero
        if (!liveChatId.StartsWith("EI", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(_token))
        {
            var resolvedChatId = await ResolveLiveChatIdAsync(liveChatId, _token, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(resolvedChatId))
                liveChatId = resolvedChatId;
        }

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var url = $"https://www.googleapis.com/youtube/v3/liveChat/messages?liveChatId={Uri.EscapeDataString(liveChatId)}&part=id,snippet,authorDetails";
                if (!string.IsNullOrEmpty(nextPageToken))
                    url += $"&pageToken={Uri.EscapeDataString(nextPageToken)}";

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (_token is { Length: > 0 })
                {
                    if (_token.StartsWith("AIza", StringComparison.OrdinalIgnoreCase))
                        req.RequestUri = new Uri(url + $"&key={_token}");
                    else
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                }

                using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode)
                {
                    var errorBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if ((int)resp.StatusCode == 401 || (int)resp.StatusCode == 403)
                    {
                        SetStatus(IntegrationStatus.TokenExpired, $"Token de YouTube inválido o cuota excedida: {resp.StatusCode}");
                        break;
                    }
                    SetStatus(IntegrationStatus.Error, $"Error YouTube API: {resp.StatusCode} - {errorBody}");
                    await Task.Delay(10000, ct).ConfigureAwait(false);
                    continue;
                }

                var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.TryGetProperty("pollingIntervalMillis", out var intervalProp) && intervalProp.TryGetInt32(out var interval))
                    pollingDelayMs = Math.Max(2000, interval);

                if (root.TryGetProperty("nextPageToken", out var tokenProp))
                    nextPageToken = tokenProp.GetString() ?? "";

                if (root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                        ProcessYouTubeMessage(item);
                }

                await Task.Delay(pollingDelayMs, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                SetStatus(IntegrationStatus.Error, $"Excepción en sondeo de YouTube: {ex.Message}");
                await Task.Delay(8000, ct).ConfigureAwait(false);
            }
        }

        if (Status == IntegrationStatus.Connected)
            SetStatus(IntegrationStatus.Disconnected, null);
    }

    private void ProcessYouTubeMessage(JsonElement item)
    {
        var id = item.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
        if (!item.TryGetProperty("snippet", out var snippet))
            return;

        var type = snippet.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? "textMessageEvent" : "textMessageEvent";
        var authorDetails = item.TryGetProperty("authorDetails", out var ad) ? ad : default;

        var authorName = authorDetails.ValueKind == JsonValueKind.Object && authorDetails.TryGetProperty("displayName", out var nameProp)
            ? nameProp.GetString() ?? "Usuario YouTube"
            : "Usuario YouTube";

        var authorId = authorDetails.ValueKind == JsonValueKind.Object && authorDetails.TryGetProperty("channelId", out var chProp)
            ? chProp.GetString() ?? authorName
            : authorName;

        var avatarUrl = authorDetails.ValueKind == JsonValueKind.Object && authorDetails.TryGetProperty("profileImageUrl", out var picProp)
            ? picProp.GetString()
            : null;

        var isOwner = authorDetails.ValueKind == JsonValueKind.Object && authorDetails.TryGetProperty("isChatOwner", out var ownProp) && ownProp.GetBoolean();
        var isMod = authorDetails.ValueKind == JsonValueKind.Object && authorDetails.TryGetProperty("isChatModerator", out var modProp) && modProp.GetBoolean();
        var isSponsor = authorDetails.ValueKind == JsonValueKind.Object && authorDetails.TryGetProperty("isChatSponsor", out var spProp) && spProp.GetBoolean();
        var isVerified = authorDetails.ValueKind == JsonValueKind.Object && authorDetails.TryGetProperty("isVerified", out var vProp) && vProp.GetBoolean();

        var badges = new List<ChatBadge>();
        if (isOwner) badges.Add(new ChatBadge(BadgeKind.Broadcaster, "Propietario"));
        if (isMod) badges.Add(new ChatBadge(BadgeKind.Moderator, "Moderador"));
        if (isSponsor) badges.Add(new ChatBadge(BadgeKind.Member, "Miembro"));
        if (isVerified) badges.Add(new ChatBadge(BadgeKind.Verified, "Verificado"));

        var messageText = "";
        decimal? donationAmount = null;
        string? currency = null;
        var isDonation = false;

        if (type == "textMessageEvent" && snippet.TryGetProperty("textMessageDetails", out var textDetails))
        {
            messageText = textDetails.TryGetProperty("messageText", out var mt) ? mt.GetString() ?? "" : "";
        }
        else if (type == "superChatEvent" && snippet.TryGetProperty("superChatDetails", out var scDetails))
        {
            isDonation = true;
            messageText = scDetails.TryGetProperty("userComment", out var uc) ? uc.GetString() ?? "" : "";
            currency = scDetails.TryGetProperty("currency", out var currProp) ? currProp.GetString() : "USD";
            if (scDetails.TryGetProperty("amountMicros", out var amProp) && amProp.TryGetInt64(out var micros))
                donationAmount = micros / 1_000_000m;

            AlertReceived?.Invoke(this, new AlertEvent(
                Id: id,
                Platform: StreamPlatform.YouTube,
                Kind: AlertKind.SuperChat,
                Username: authorName,
                Timestamp: DateTime.UtcNow,
                AvatarUrl: avatarUrl,
                Amount: donationAmount,
                Currency: currency,
                Message: messageText,
                Priority: AlertPriority.High));
        }
        else if (type == "superStickerEvent")
        {
            isDonation = true;
            messageText = "Super Sticker";
            AlertReceived?.Invoke(this, new AlertEvent(
                Id: id,
                Platform: StreamPlatform.YouTube,
                Kind: AlertKind.SuperChat,
                Username: authorName,
                Timestamp: DateTime.UtcNow,
                AvatarUrl: avatarUrl,
                Message: "Super Sticker",
                Priority: AlertPriority.High));
        }
        else if (type == "newSponsorEvent")
        {
            AlertReceived?.Invoke(this, new AlertEvent(
                Id: id,
                Platform: StreamPlatform.YouTube,
                Kind: AlertKind.Subscription,
                Username: authorName,
                Timestamp: DateTime.UtcNow,
                AvatarUrl: avatarUrl,
                Tier: "Miembro",
                Priority: AlertPriority.High));
        }

        var msg = new ChatMessage(
            Id: id,
            Platform: StreamPlatform.YouTube,
            AuthorName: authorName,
            AuthorId: authorId,
            Message: messageText,
            Timestamp: DateTime.UtcNow,
            AvatarUrl: avatarUrl,
            Badges: badges,
            IsModerator: isMod || isOwner,
            IsSubscriber: isSponsor,
            IsDonation: isDonation,
            DonationAmount: donationAmount,
            Currency: currency,
            IsHighlighted: isDonation);

        MessageReceived?.Invoke(this, msg);
    }

    private async Task<string?> ResolveLiveChatIdAsync(string videoId, string token, CancellationToken ct)
    {
        try
        {
            var url = $"https://www.googleapis.com/youtube/v3/videos?id={Uri.EscapeDataString(videoId)}&part=liveStreamingDetails";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (token.StartsWith("AIza", StringComparison.OrdinalIgnoreCase))
                req.RequestUri = new Uri(url + $"&key={token}");
            else
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("items", out var items) && items.GetArrayLength() > 0)
            {
                var first = items[0];
                if (first.TryGetProperty("liveStreamingDetails", out var lsd) &&
                    lsd.TryGetProperty("activeLiveChatId", out var alc))
                {
                    return alc.GetString();
                }
            }
        }
        catch (Exception ex) { AppLog.Warn("integraciones", $"YouTube: {ex.Message}"); }
        return null;
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

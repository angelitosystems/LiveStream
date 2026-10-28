using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Integrations;

namespace LiveStream.Extensions.Integrations;

/// <summary>
/// Conector oficial de Twitch a través de WebSockets IRC (wss://irc-ws.chat.twitch.tv:443).
/// Soporta chat unificado, suscripciones, resubs, raids, bits/donaciones y moderación.
/// Funciona con token OAuth o en modo anónimo (justinfan) para canales públicos.
/// </summary>
public sealed class TwitchConnector : IPlatformConnector
{
    private const string WsEndpoint = "wss://irc-ws.chat.twitch.tv:443";
    private readonly object _lock = new();

    private ClientWebSocket? _ws;
    private CancellationTokenSource? _cts;
    private Task? _readLoopTask;
    private PlatformIntegrationConfig? _config;
    private string? _token;

    public StreamPlatform Platform => StreamPlatform.Twitch;
    public IntegrationStatus Status { get; private set; } = IntegrationStatus.Disconnected;
    public string? LastError { get; private set; }
    public DateTime? LastConnected { get; private set; }

    public event EventHandler<ChatMessage>? MessageReceived;
    public event EventHandler<AlertEvent>? AlertReceived;
    public event EventHandler<IntegrationStatus>? StatusChanged;
    public event EventHandler? ChatCleared;

    public async Task<bool> ValidateApiAsync(PlatformIntegrationConfig config, string? secretToken, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(config.AccountName))
        {
            LastError = "Se requiere el nombre del canal de Twitch.";
            return false;
        }

        // Twitch IRC permite lectura de chat pública anónima o autenticada con token OAuth.
        return true;
    }

    public async Task<bool> ConnectAsync(PlatformIntegrationConfig config, string? secretToken, CancellationToken ct)
    {
        await DisconnectAsync(CancellationToken.None).ConfigureAwait(false);

        _config = config;
        _token = secretToken;
        SetStatus(IntegrationStatus.Connecting, null);

        var channel = config.AccountName.Trim().ToLowerInvariant().TrimStart('#');
        if (string.IsNullOrWhiteSpace(channel))
        {
            SetStatus(IntegrationStatus.Error, "Nombre de canal no configurado.");
            return false;
        }

        try
        {
            _cts = new CancellationTokenSource();
            _ws = new ClientWebSocket();
            await _ws.ConnectAsync(new Uri(WsEndpoint), ct).ConfigureAwait(false);

            var nick = !string.IsNullOrWhiteSpace(config.AccountId) ? config.AccountId.ToLowerInvariant() : $"justinfan{Random.Shared.Next(10000, 99999)}";
            var pass = !string.IsNullOrWhiteSpace(secretToken) ? (secretToken.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase) ? secretToken : $"oauth:{secretToken}") : "SCHMOOPIE";

            await SendRawAsync("CAP REQ :twitch.tv/tags twitch.tv/commands twitch.tv/membership", _cts.Token).ConfigureAwait(false);
            await SendRawAsync($"PASS {pass}", _cts.Token).ConfigureAwait(false);
            await SendRawAsync($"NICK {nick}", _cts.Token).ConfigureAwait(false);
            await SendRawAsync($"JOIN #{channel}", _cts.Token).ConfigureAwait(false);

            SetStatus(IntegrationStatus.Connected, null);
            LastConnected = DateTime.UtcNow;

            _readLoopTask = Task.Run(() => ReadLoopAsync(_cts.Token));
            return true;
        }
        catch (Exception ex)
        {
            SetStatus(IntegrationStatus.Error, $"Fallo de conexión a Twitch: {ex.Message}");
            return false;
        }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        if (_cts is not null)
        {
            try { _cts.Cancel(); } catch (Exception ex) { AppLog.Warn("integraciones", $"Desconectar Twitch: {ex.Message}"); }
        }

        if (_ws is not null)
        {
            try
            {
                if (_ws.State == WebSocketState.Open)
                    await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Desconectado", ct).ConfigureAwait(false);
            }
            catch (Exception ex) { AppLog.Warn("integraciones", $"Desconectar Twitch: {ex.Message}"); }
            finally
            {
                _ws.Dispose();
                _ws = null;
            }
        }

        if (_readLoopTask is not null)
        {
            try { await _readLoopTask.ConfigureAwait(false); } catch (Exception ex) { AppLog.Warn("integraciones", $"Desconectar Twitch: {ex.Message}"); }
            _readLoopTask = null;
        }

        _cts?.Dispose();
        _cts = null;

        SetStatus(IntegrationStatus.Disconnected, null);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        var ms = new MemoryStream();

        try
        {
            while (!ct.IsCancellationRequested && _ws is { State: WebSocketState.Open })
            {
                var result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                ms.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    var text = Encoding.UTF8.GetString(ms.ToArray());
                    ms.SetLength(0);

                    var lines = text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines)
                        await ProcessIrcLineAsync(line, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetStatus(IntegrationStatus.Error, $"Error en stream de Twitch: {ex.Message}");
            if (_config?.AutoReconnect == true && !ct.IsCancellationRequested)
                _ = Task.Run(async () =>
                {
                    await Task.Delay(5000, CancellationToken.None);
                    if (_config is not null)
                        await ConnectAsync(_config, _token, CancellationToken.None);
                });
            return;
        }

        if (Status == IntegrationStatus.Connected)
            SetStatus(IntegrationStatus.Disconnected, null);
    }

    private async Task ProcessIrcLineAsync(string line, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        if (line.StartsWith("PING", StringComparison.OrdinalIgnoreCase))
        {
            var rest = line.Length > 4 ? line[4..] : "";
            await SendRawAsync($"PONG{rest}", ct).ConfigureAwait(false);
            return;
        }

        // Formato IRC con etiquetas: @tags :prefix COMMAND params :trailing
        var (tags, command, trailing, rawPrefix) = ParseIrc(line);

        if (command == "PRIVMSG")
        {
            var author = tags.GetValueOrDefault("display-name") ?? ExtractNick(rawPrefix);
            var userId = tags.GetValueOrDefault("user-id") ?? author;
            var badges = ParseBadges(tags.GetValueOrDefault("badges"));
            var isMod = tags.GetValueOrDefault("mod") == "1" || badges.Exists(b => b.Kind == BadgeKind.Moderator || b.Kind == BadgeKind.Broadcaster);
            var isSub = badges.Exists(b => b.Kind == BadgeKind.Subscriber);
            var isHighlight = tags.GetValueOrDefault("msg-id") == "highlighted-message";

            decimal? donationAmount = null;
            string? currency = null;
            var isDonation = false;

            if (tags.TryGetValue("bits", out var bitsStr) && int.TryParse(bitsStr, out var bits) && bits > 0)
            {
                isDonation = true;
                donationAmount = (decimal)bits;
                currency = "Bits";

                AlertReceived?.Invoke(this, new AlertEvent(
                    Id: Guid.NewGuid().ToString("N"),
                    Platform: StreamPlatform.Twitch,
                    Kind: AlertKind.Donation,
                    Username: author,
                    Timestamp: DateTime.UtcNow,
                    Amount: donationAmount,
                    Currency: currency,
                    Message: trailing,
                    Priority: bits >= 1000 ? AlertPriority.High : AlertPriority.Normal,
                    RawPayload: line));
            }

            var msg = new ChatMessage(
                Id: tags.GetValueOrDefault("id") ?? Guid.NewGuid().ToString("N"),
                Platform: StreamPlatform.Twitch,
                AuthorName: author,
                AuthorId: userId,
                Message: trailing,
                Timestamp: DateTime.UtcNow,
                Badges: badges,
                IsModerator: isMod,
                IsSubscriber: isSub,
                IsDonation: isDonation,
                DonationAmount: donationAmount,
                Currency: currency,
                IsHighlighted: isHighlight,
                RawData: line);

            MessageReceived?.Invoke(this, msg);
        }
        else if (command == "USERNOTICE")
        {
            var msgId = tags.GetValueOrDefault("msg-id");
            var author = tags.GetValueOrDefault("display-name") ?? tags.GetValueOrDefault("login") ?? "Espectador";

            if (msgId is "sub" or "resub" or "subgift")
            {
                var months = int.TryParse(tags.GetValueOrDefault("msg-param-cumulative-months") ?? tags.GetValueOrDefault("msg-param-months"), out var m) ? m : 1;
                var plan = tags.GetValueOrDefault("msg-param-sub-plan-name") ?? tags.GetValueOrDefault("msg-param-sub-plan") ?? "Tier 1";

                AlertReceived?.Invoke(this, new AlertEvent(
                    Id: Guid.NewGuid().ToString("N"),
                    Platform: StreamPlatform.Twitch,
                    Kind: AlertKind.Subscription,
                    Username: author,
                    Timestamp: DateTime.UtcNow,
                    Tier: plan,
                    Months: months,
                    Message: trailing,
                    Priority: AlertPriority.High,
                    RawPayload: line));
            }
            else if (msgId == "raid")
            {
                var viewers = int.TryParse(tags.GetValueOrDefault("msg-param-viewerCount"), out var v) ? v : 0;
                AlertReceived?.Invoke(this, new AlertEvent(
                    Id: Guid.NewGuid().ToString("N"),
                    Platform: StreamPlatform.Twitch,
                    Kind: AlertKind.Raid,
                    Username: author,
                    Timestamp: DateTime.UtcNow,
                    Viewers: viewers,
                    Message: trailing,
                    Priority: AlertPriority.Urgent,
                    RawPayload: line));
            }
        }
        else if (command == "CLEARCHAT")
        {
            ChatCleared?.Invoke(this, EventArgs.Empty);
        }
    }

    public static (Dictionary<string, string> Tags, string Command, string Trailing, string Prefix) ParseIrc(string raw)
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var idx = 0;

        if (raw.StartsWith('@'))
        {
            var space = raw.IndexOf(' ');
            if (space > 1)
            {
                var tagsPart = raw[1..space];
                foreach (var tag in tagsPart.Split(';'))
                {
                    var eq = tag.IndexOf('=');
                    if (eq > 0)
                        tags[tag[..eq]] = tag[(eq + 1)..];
                }
                idx = space + 1;
            }
        }

        while (idx < raw.Length && raw[idx] == ' ')
            idx++;

        var prefix = "";
        if (idx < raw.Length && raw[idx] == ':')
        {
            var space = raw.IndexOf(' ', idx);
            if (space > idx)
            {
                prefix = raw[(idx + 1)..space];
                idx = space + 1;
            }
        }

        while (idx < raw.Length && raw[idx] == ' ')
            idx++;

        var nextSpace = raw.IndexOf(' ', idx);
        var command = nextSpace > idx ? raw[idx..nextSpace] : raw[idx..];

        var trailing = "";
        var colon = raw.IndexOf(" :", idx, StringComparison.Ordinal);
        if (colon >= 0)
            trailing = raw[(colon + 2)..];

        return (tags, command.Trim().ToUpperInvariant(), trailing, prefix);
    }

    private static string ExtractNick(string prefix)
    {
        var exclamation = prefix.IndexOf('!');
        return exclamation > 0 ? prefix[..exclamation] : (prefix.Length > 0 ? prefix : "Usuario");
    }

    public static List<ChatBadge> ParseBadges(string? badgesStr)
    {
        var result = new List<ChatBadge>();
        if (string.IsNullOrWhiteSpace(badgesStr))
            return result;

        foreach (var badge in badgesStr.Split(','))
        {
            var parts = badge.Split('/');
            var name = parts[0].ToLowerInvariant();
            switch (name)
            {
                case "broadcaster":
                    result.Add(new ChatBadge(BadgeKind.Broadcaster, "Streamer"));
                    break;
                case "moderator":
                    result.Add(new ChatBadge(BadgeKind.Moderator, "Mod"));
                    break;
                case "subscriber":
                    result.Add(new ChatBadge(BadgeKind.Subscriber, "Sub"));
                    break;
                case "vip":
                    result.Add(new ChatBadge(BadgeKind.Vip, "VIP"));
                    break;
                case "partner":
                case "verified":
                    result.Add(new ChatBadge(BadgeKind.Verified, "Verificado"));
                    break;
            }
        }
        return result;
    }

    private async Task SendRawAsync(string message, CancellationToken ct)
    {
        if (_ws is not { State: WebSocketState.Open })
            return;
        var bytes = Encoding.UTF8.GetBytes(message + "\r\n");
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
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

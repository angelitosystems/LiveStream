using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Application.Graphics;
using LiveStream.Application.Studio;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Graphics;

namespace LiveStream.Extensions.Chat;

/// <summary>
/// Gestiona la superposición de chat en directo sobre el lienzo de emisión (a través de la capa de gráficos
/// o como vista previa interactiva). Controla la duración, rotación, salida automática y temas visuales.
/// </summary>
public sealed class ChatOverlayService : IDisposable
{
    private readonly StudioSession _session;
    private readonly UnifiedChatService _chat;
    private readonly object _lock = new();
    private readonly List<ChatMessage> _activeOverlayMessages = [];
    private readonly System.Timers.Timer _expiryTimer;

    public event EventHandler<IReadOnlyList<ChatMessage>>? OverlayMessagesChanged;

    public ChatOverlayService(StudioSession session, UnifiedChatService chat)
    {
        _session = session;
        _chat = chat;
        _expiryTimer = new System.Timers.Timer(1000);
        _expiryTimer.Elapsed += (_, _) => PruneExpiredMessages();
    }

    /// <summary>Empieza a seguir el chat (lo llama la extensión «chat» al activarse).</summary>
    public void Start()
    {
        _chat.MessageAdded += OnMessageAdded;
        _chat.ModerationToggled += OnModerationToggled;
        _chat.ChatReset += OnChatReset;
        _expiryTimer.Start();
    }

    public void Stop()
    {
        _chat.MessageAdded -= OnMessageAdded;
        _chat.ModerationToggled -= OnModerationToggled;
        _chat.ChatReset -= OnChatReset;
        _expiryTimer.Stop();
    }

    private void OnMessageAdded(object? sender, ChatMessage msg) => OnMessageAdded(msg);

    private void OnModerationToggled(object? sender, ChatMessage msg) => OnModerationToggled(msg);

    private void OnChatReset(object? sender, EventArgs e) => ClearOverlay();

    public ChatOverlayStyle Style
    {
        get => _session.Project.ChatOverlay;
        set => _session.Project.ChatOverlay = value ?? new ChatOverlayStyle();
    }

    public IReadOnlyList<ChatMessage> ActiveMessages
    {
        get
        {
            lock (_lock)
                return _activeOverlayMessages.ToList();
        }
    }

    private void OnMessageAdded(ChatMessage msg)
    {
        if (Style.OnlyShowModerated && !msg.IsModerated)
            return;

        PushMessage(msg);
    }

    private void OnModerationToggled(ChatMessage msg)
    {
        if (!Style.OnlyShowModerated) return;

        if (msg.IsModerated)
            PushMessage(msg);
        else
            RemoveMessage(msg.Id);
    }

    public void PushMessage(ChatMessage msg)
    {
        lock (_lock)
        {
            _activeOverlayMessages.RemoveAll(m => m.Id == msg.Id);
            _activeOverlayMessages.Add(msg);

            var max = Math.Clamp(Style.MaxVisibleMessages, 1, 10);
            while (_activeOverlayMessages.Count > max)
                _activeOverlayMessages.RemoveAt(0);
        }

        NotifyAndSyncToGraphics();
    }

    public void RemoveMessage(string messageId)
    {
        lock (_lock)
        {
            _activeOverlayMessages.RemoveAll(m => m.Id == messageId);
        }
        NotifyAndSyncToGraphics();
    }

    public void ClearOverlay()
    {
        lock (_lock)
        {
            _activeOverlayMessages.Clear();
        }
        NotifyAndSyncToGraphics();
    }

    private void PruneExpiredMessages()
    {
        var durationSec = Style.MessageDurationSec;
        if (durationSec <= 0)
            return;

        var cutoff = DateTime.UtcNow.AddSeconds(-durationSec);
        bool changed;

        lock (_lock)
        {
            var before = _activeOverlayMessages.Count;
            _activeOverlayMessages.RemoveAll(m => m.Timestamp < cutoff);
            changed = _activeOverlayMessages.Count != before;
        }

        if (changed)
            NotifyAndSyncToGraphics();
    }

    private void NotifyAndSyncToGraphics()
    {
        IReadOnlyList<ChatMessage> snapshot;
        lock (_lock)
            snapshot = _activeOverlayMessages.ToList();

        OverlayMessagesChanged?.Invoke(this, snapshot);

        // Si existe un gráfico de tipo ChatOverlay en la capa de gráficos, sincroniza sus líneas en vivo
        SyncLiveGraphics(snapshot);
    }

    private void SyncLiveGraphics(IReadOnlyList<ChatMessage> messages)
    {
        _session.OnOwnerThread("Sincronizar chat en pantalla", () =>
        {
            var chatGraphic = _session.Project.Graphics.FirstOrDefault(g => g.Kind == GraphicKind.ChatOverlay);
            if (chatGraphic is null)
                return Task.CompletedTask;

            var fields = new Dictionary<string, string>(chatGraphic.Fields);
            for (var i = 1; i <= 4; i++)
            {
                var lineIdx = messages.Count - i;
                fields[$"line{i}"] = lineIdx >= 0
                    ? $"{messages[lineIdx].AuthorName}: {messages[lineIdx].Message}"
                    : "";
            }

            var updated = chatGraphic with { Fields = fields };
            _session.Project.ReplaceGraphic(updated);
            return Task.CompletedTask;
        });
    }

    public void Dispose()
    {
        _expiryTimer.Dispose();
    }
}

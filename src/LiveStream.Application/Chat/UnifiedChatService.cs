using System;
using System.Collections.Generic;
using System.Linq;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;

namespace LiveStream.Application.Chat;

/// <summary>
/// Servicio central de chat unificado: almacena el búfer de mensajes, aplica filtros por plataforma,
/// usuario y palabras, gestiona la moderación y selección de mensajes para la superposición en directo,
/// y se coordina con el servicio de saludos automáticos.
/// </summary>
public sealed class UnifiedChatService
{
    private readonly object _lock = new();
    private readonly List<ChatMessage> _messages = [];
    private readonly HashSet<string> _moderatedMessageIds = [];
    private readonly GreetingService _greetings = new();
    private ChatFilter _filter = new();
    private int _maxCapacity = 200;
    private bool _isAutoScrollPaused;

    public event EventHandler<ChatMessage>? MessageAdded;
    public event EventHandler? ChatReset;
    public event EventHandler? FilterChanged;
    public event EventHandler<ChatMessage>? ModerationToggled;
    public event EventHandler<AlertEvent>? AutoGreetingTriggered;

    public UnifiedChatService(int maxCapacity = 200)
    {
        _maxCapacity = Math.Max(10, maxCapacity);
        _greetings.FirstTimeGreetingTriggered += (_, alert) => AutoGreetingTriggered?.Invoke(this, alert);
    }

    public int MaxCapacity
    {
        get => _maxCapacity;
        set
        {
            lock (_lock)
            {
                _maxCapacity = Math.Max(10, value);
                TrimBuffer();
            }
        }
    }

    public bool IsAutoScrollPaused
    {
        get => _isAutoScrollPaused;
        set => _isAutoScrollPaused = value;
    }

    public ChatFilter Filter
    {
        get => _filter;
        set
        {
            _filter = value ?? new ChatFilter();
            FilterChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public IReadOnlyList<ChatMessage> AllMessages
    {
        get
        {
            lock (_lock)
                return _messages.ToList();
        }
    }

    public IReadOnlyList<ChatMessage> FilteredMessages
    {
        get
        {
            lock (_lock)
                return _messages.Where(m => _filter.Matches(m)).ToList();
        }
    }

    public void AddMessage(ChatMessage message)
    {
        var isFirst = _greetings.CheckAndTrack(message, out var greetingAlert);
        if (isFirst)
            message = message with { IsFirstTimeChatter = true };

        // Detección de palabras clave destacadas
        if (_filter.IsHighlightedWord(message.Message))
            message = message with { IsHighlighted = true };

        lock (_lock)
        {
            _messages.Add(message);
            TrimBuffer();
        }

        MessageAdded?.Invoke(this, message);
    }

    public void ToggleModerated(string messageId)
    {
        ChatMessage? updated = null;
        lock (_lock)
        {
            var idx = _messages.FindIndex(m => m.Id == messageId);
            if (idx >= 0)
            {
                var current = _messages[idx];
                var newState = !current.IsModerated;
                updated = current with { IsModerated = newState };
                _messages[idx] = updated;

                if (newState)
                    _moderatedMessageIds.Add(messageId);
                else
                    _moderatedMessageIds.Remove(messageId);
            }
        }

        if (updated is not null)
            ModerationToggled?.Invoke(this, updated);
    }

    public bool IsModerated(string messageId)
    {
        lock (_lock)
            return _moderatedMessageIds.Contains(messageId);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _messages.Clear();
            _moderatedMessageIds.Clear();
        }
        ChatReset?.Invoke(this, EventArgs.Empty);
    }

    private void TrimBuffer()
    {
        if (_messages.Count > _maxCapacity)
        {
            var excess = _messages.Count - _maxCapacity;
            _messages.RemoveRange(0, excess);
        }
    }
}

using System;
using System.Collections.Concurrent;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Entities;

namespace LiveStream.Application.Chat;

/// <summary>
/// Identifica a los espectadores que interactúan por primera vez en el directo
/// y genera automáticamente un evento de saludo/bienvenida configurable.
/// </summary>
public sealed class GreetingService
{
    private readonly ConcurrentDictionary<string, byte> _knownAuthors = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<AlertEvent>? FirstTimeGreetingTriggered;

    public bool CheckAndTrack(ChatMessage message, out AlertEvent? greetingAlert)
    {
        greetingAlert = null;
        var key = $"{message.Platform}:{message.AuthorId}";

        if (_knownAuthors.TryAdd(key, 0))
        {
            greetingAlert = new AlertEvent(
                Id: Guid.NewGuid().ToString("N"),
                Platform: message.Platform,
                Kind: AlertKind.Greeting,
                Username: message.AuthorName,
                Timestamp: DateTime.UtcNow,
                AvatarUrl: message.AvatarUrl,
                Message: message.Message,
                Priority: AlertPriority.Normal);

            FirstTimeGreetingTriggered?.Invoke(this, greetingAlert);
            return true;
        }

        return false;
    }

    public void Reset() => _knownAuthors.Clear();
}

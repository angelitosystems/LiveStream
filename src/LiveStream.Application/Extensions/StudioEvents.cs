using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;

namespace LiveStream.Application.Extensions;

// Contratos de eventos entre extensiones. Son estables: se añaden campos opcionales, no se cambian los existentes.

/// <summary>Mensaje de chat ya normalizado (filtros y saludos aplicados) que entra al estudio. Lo publica «chat».</summary>
public sealed record ChatMessageReceived(ChatMessage Message);

/// <summary>Evento de plataforma (seguidor, suscripción, donación, raid…). Lo publica «chat» al recibirlo de un conector.</summary>
public sealed record PlatformAlertReceived(AlertEvent Alert);

/// <summary>Una funcionalidad pide mostrar una alerta (p. ej. el saludo al primer mensaje). Lo atiende «alerts».</summary>
public sealed record AlertRequested(AlertEvent Alert);

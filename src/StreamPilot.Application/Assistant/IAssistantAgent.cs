namespace StreamPilot.Application.Assistant;

/// <summary>Lo que va pasando mientras el asistente atiende un mensaje (para mostrarlo en directo).</summary>
public abstract record AssistantEvent;

/// <summary>El asistente pide ejecutar una herramienta.</summary>
public sealed record ToolCallStarted(string ToolName) : AssistantEvent;

/// <summary>Una herramienta terminó; Summary es la línea legible para el usuario.</summary>
public sealed record ToolCallFinished(string ToolName, string Summary, bool IsError) : AssistantEvent;

/// <summary>Texto de respuesta del asistente.</summary>
public sealed record AssistantText(string Text) : AssistantEvent;

/// <summary>Asistente conversacional que opera el estudio con herramientas tipadas.</summary>
public interface IAssistantAgent
{
    /// <summary>Atiende un mensaje del usuario: puede llamar a varias herramientas antes de responder.</summary>
    Task SendAsync(string userMessage, Action<AssistantEvent> onEvent, CancellationToken ct);

    /// <summary>Empieza una conversación nueva (olvida el historial).</summary>
    void Reset();
}

/// <summary>Error del proveedor del modelo con un mensaje apto para el usuario.</summary>
public sealed class AssistantUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

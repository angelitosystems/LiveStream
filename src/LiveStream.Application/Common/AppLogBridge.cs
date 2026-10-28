using System;

namespace LiveStream.Application;

/// <summary>
/// Puente desacoplado de logging para la capa de Aplicación.
/// Permite registrar diagnósticos sin crear dependencias circulares con Infraestructura.
/// </summary>
public static class AppLog
{
    public static Action<string, string>? InfoAction { get; set; }
    public static Action<string, string>? WarnAction { get; set; }
    public static Action<string, string, Exception?>? ErrorAction { get; set; }

    public static void Info(string category, string message) => InfoAction?.Invoke(category, message);
    public static void Warn(string category, string message) => WarnAction?.Invoke(category, message);
    public static void Error(string category, string message, Exception? ex = null) => ErrorAction?.Invoke(category, message, ex);
}

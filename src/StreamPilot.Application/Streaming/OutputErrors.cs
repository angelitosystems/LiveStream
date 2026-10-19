using StreamPilot.Application.Abstractions;

namespace StreamPilot.Application.Streaming;

/// <summary>Mensajes legibles para los códigos de detención de salidas de libobs (OBS_OUTPUT_*).</summary>
public static class OutputErrors
{
    public static string Describe(OutputStopInfo stop)
    {
        var reason = stop.Code switch
        {
            -1 => "Ruta o URL de servidor inválida.",
            -2 => "No se pudo conectar con el servidor. Revisa la URL, el firewall y tu conexión.",
            -3 => "El servidor rechazó la transmisión. Revisa que la clave de transmisión sea correcta y esté activa.",
            -4 => "Error de la salida.",
            -5 => "Se perdió la conexión con el servidor y no se pudo recuperar.",
            -6 => "El servidor no admite la configuración enviada (códec o formato).",
            -7 => "No queda espacio en disco.",
            -8 => "Error del codificador. Prueba otro codificador o un bitrate menor.",
            _ => $"Error desconocido ({stop.Code}).",
        };
        return string.IsNullOrWhiteSpace(stop.Message) ? reason : $"{reason} Detalle: {stop.Message}";
    }
}

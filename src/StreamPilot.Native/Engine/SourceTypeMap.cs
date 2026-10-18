using StreamPilot.Domain.Entities;

namespace StreamPilot.Native.Engine;

/// <summary>Traducción de tipos de fuente del dominio a ids de libobs en Windows.</summary>
internal static class SourceTypeMap
{
    public static string ToObsId(SourceKind kind) => kind switch
    {
        SourceKind.DisplayCapture => "monitor_capture",
        SourceKind.GameCapture => "game_capture",
        SourceKind.WindowCapture => "window_capture",
        SourceKind.VideoCaptureDevice => "dshow_input",
        SourceKind.AudioInput => "wasapi_input_capture",
        SourceKind.AudioOutput => "wasapi_output_capture",
        SourceKind.Image => "image_source",
        SourceKind.Text => "text_gdiplus",
        SourceKind.Browser => "browser_source",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Tipo de fuente no soportado."),
    };
}

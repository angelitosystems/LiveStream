using LiveStream.Domain.Entities;

namespace LiveStream.Native.Engine;

/// <summary>Traducción de tipos de fuente del dominio a ids de libobs 32 en Windows.</summary>
public static class SourceTypeMap
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
        SourceKind.Text => "text_gdiplus_v3",
        SourceKind.Browser => "browser_source",
        SourceKind.Media => "ffmpeg_source",
        SourceKind.ColorSource => "color_source_v3",
        SourceKind.ImageSlideshow => "slideshow_v2",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Tipo de fuente no soportado."),
    };

    /// <summary>Tipos que el runtime actual puede crear. obs-browser (CEF) aún no se distribuye.</summary>
    public static bool IsAvailable(SourceKind kind) => kind != SourceKind.Browser;
}

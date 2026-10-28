using LiveStream.Domain.Entities;

namespace LiveStream.Domain.Compositions;

/// <summary>
/// Configuración de composición dual (Horizontal 16:9 y Vertical 9:16 simultáneos).
/// Permite mantener composiciones independientes para cada formato reutilizando fuentes comunes.
/// </summary>
public sealed record DualCompositionSettings(
    CompositionFormat ActiveFormat = CompositionFormat.Horizontal,
    CanvasSettings? HorizontalCanvas = null,
    CanvasSettings? VerticalCanvas = null,
    bool SyncAlerts = true,
    bool SyncChat = true,
    Guid? HorizontalSceneId = null,
    Guid? VerticalSceneId = null,
    bool EnableSimultaneousOutput = true)
{
    public CanvasSettings EffectiveHorizontalCanvas => HorizontalCanvas ?? new CanvasSettings(1920, 1080, 30);
    public CanvasSettings EffectiveVerticalCanvas => VerticalCanvas ?? new CanvasSettings(1080, 1920, 30);

    public CanvasSettings CanvasFor(CompositionFormat format) =>
        format == CompositionFormat.Vertical ? EffectiveVerticalCanvas : EffectiveHorizontalCanvas;
}

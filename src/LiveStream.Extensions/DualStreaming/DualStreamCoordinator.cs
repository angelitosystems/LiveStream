using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Application.Abstractions;
using LiveStream.Application.Streaming;
using LiveStream.Application.Studio;
using LiveStream.Domain.Compositions;
using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.Extensions.DualStreaming;

/// <summary>
/// Coordinador de emisión y composición dual simultánea (Horizontal 16:9 y Vertical 9:16).
/// Permite mantener composiciones independientes para cada formato reutilizando fuentes comunes
/// (cámara, captura de ventana/pantalla, audio) sin duplicar captura física de hardware,
/// y emitir concurrentemente a destinos horizontales y verticales.
/// </summary>
public sealed class DualStreamCoordinator
{
    private readonly StudioSession _session;

    public event EventHandler<CompositionFormat>? ActiveFormatChanged;

    public DualStreamCoordinator(StudioSession session)
    {
        _session = session;
    }

    public StudioProject Project => _session.Project;
    public DualCompositionSettings Settings => Project.DualComposition;

    public CompositionFormat ActiveFormat => Settings.ActiveFormat;

    /// <summary>
    /// Cambia el formato de composición activo para edición en pantalla (Horizontal 16:9 o Vertical 9:16).
    /// Actualiza el lienzo y notifica a los controladores de vista previa para adaptar el encuadre.
    /// </summary>
    public async Task SwitchFormatAsync(CompositionFormat format, CancellationToken ct = default)
    {
        if (format == CompositionFormat.Both || Settings.ActiveFormat == format)
            return;

        var targetCanvas = Settings.CanvasFor(format);
        Project.Canvas = targetCanvas;
        Project.DualComposition = Settings with { ActiveFormat = format };

        // Si existe una escena asociada al formato, la activa
        if (format == CompositionFormat.Vertical && Settings.VerticalSceneId.HasValue &&
            Project.Scenes.Any(s => s.Id == Settings.VerticalSceneId.Value))
        {
            Project.ActivateScene(Settings.VerticalSceneId.Value);
        }
        else if (format == CompositionFormat.Horizontal && Settings.HorizontalSceneId.HasValue &&
            Project.Scenes.Any(s => s.Id == Settings.HorizontalSceneId.Value))
        {
            Project.ActivateScene(Settings.HorizontalSceneId.Value);
        }

        ActiveFormatChanged?.Invoke(this, format);
        await _session.SaveAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Filtra los destinos según el formato objetivo (Horizontal, Vertical o Ambos).
    /// </summary>
    public IEnumerable<StreamDestination> GetDestinationsFor(CompositionFormat format)
    {
        return Project.Destinations.Where(d => d.Enabled && (d.TargetFormat == CompositionFormat.Both || d.TargetFormat == format));
    }

    /// <summary>
    /// Inicia la emisión únicamente para los destinos horizontales.
    /// </summary>
    public async Task<IReadOnlyList<StudioSession.DestinationStartResult>> StartHorizontalOnlyAsync(CancellationToken ct = default)
    {
        var targets = GetDestinationsFor(CompositionFormat.Horizontal).ToList();
        return await StartFilteredDestinationsAsync(targets, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inicia la emisión únicamente para los destinos verticales (TikTok, Instagram, etc.).
    /// </summary>
    public async Task<IReadOnlyList<StudioSession.DestinationStartResult>> StartVerticalOnlyAsync(CancellationToken ct = default)
    {
        var targets = GetDestinationsFor(CompositionFormat.Vertical).ToList();
        return await StartFilteredDestinationsAsync(targets, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Inicia la emisión concurrente a TODOS los destinos habilitados (horizontal + vertical simultáneos).
    /// </summary>
    public async Task<IReadOnlyList<StudioSession.DestinationStartResult>> StartSimultaneousAsync(CancellationToken ct = default)
    {
        return await _session.StartStreamAsync(ct).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<StudioSession.DestinationStartResult>> StartFilteredDestinationsAsync(
        IReadOnlyList<StreamDestination> targets, CancellationToken ct)
    {
        var results = new List<StudioSession.DestinationStartResult>();
        foreach (var d in targets)
        {
            if (_session.Destinations.IsLive(d.Id))
                continue;

            try
            {
                await _session.StartDestinationAsync(d.Id, ct).ConfigureAwait(false);
                results.Add(new StudioSession.DestinationStartResult(d, null));
            }
            catch (Exception ex)
            {
                results.Add(new StudioSession.DestinationStartResult(d, ex.Message));
            }
        }

        return results;
    }

    /// <summary>
    /// Crea una escena vertical a partir de una escena horizontal, adaptando proporcionalmente
    /// las fuentes compartidas (cámaras y capturas) al espacio vertical (9:16).
    /// </summary>
    public Scene CreateVerticalCompositionFrom(Scene horizontalScene, string newSceneName = "Escena Vertical (9:16)")
    {
        var verticalScene = Project.AddScene(Project.NextSceneName(newSceneName));
        var vCanvas = Settings.EffectiveVerticalCanvas;

        foreach (var item in horizontalScene.Items)
        {
            var source = Project.GetSource(item.SourceId);

            // Reencuadre adaptativo según el tipo de fuente
            var newTransform = AdaptTransformToVertical(item.Transform, source.Kind, vCanvas);
            var newItem = verticalScene.AddItem(source.Id, newTransform);
            newItem.Visible = item.Visible;
            newItem.Locked = item.Locked;
        }

        Project.DualComposition = Settings with { VerticalSceneId = verticalScene.Id };
        return verticalScene;
    }

    public static Transform AdaptTransformToVertical(Transform sourceTransform, SourceKind kind, CanvasSettings vCanvas)
    {
        // En un lienzo 1080x1920:
        // Las cámaras de video suelen encuadrarse arriba o centradas ocupando el ancho completo.
        if (kind is SourceKind.VideoCaptureDevice or SourceKind.WindowCapture or SourceKind.DisplayCapture or SourceKind.GameCapture)
        {
            // Escala para ocupar el ancho de 1080 manteniendo proporción
            return new Transform(
                X: 0,
                Y: Math.Max(0, (vCanvas.Height - (1080 * 9 / 16)) / 3), // Tercio superior
                ScaleX: 1.0,
                ScaleY: 1.0,
                RotationDegrees: 0);
        }

        return sourceTransform;
    }
}

using LiveStream.Application.Commands;
using LiveStream.Application.Graphics;
using LiveStream.Application.Sources;
using LiveStream.Application.Studio;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.Application.Scenes;

/// <summary>Plantillas de escena preconfiguradas que el asistente de creación ofrece.</summary>
public enum SceneTemplateKind { Empty, Camera, Gameplay, Presentation, Interview, Fullscreen, Overlays }

public sealed record SceneTemplateOption(SceneTemplateKind Kind, string Name, string Hint)
{
    public override string ToString() => Name;
}

/// <summary>
/// Creación de escenas con plantilla: la escena se crea con <see cref="CreateSceneCommand"/> y después se rellena
/// con los comandos normales de fuentes (y de gráficos), de modo que todo queda en el historial de deshacer. El
/// ajuste a pantalla se calcula con el tamaño real que informe el motor, igual que el asistente.
/// </summary>
public static class SceneTemplates
{
    public static readonly IReadOnlyList<SceneTemplateOption> Options =
    [
        new(SceneTemplateKind.Empty, "Escena vacía", "Empieza de cero; añade fuentes con el botón +."),
        new(SceneTemplateKind.Camera, "Cámara", "Una cámara o capturadora a pantalla completa."),
        new(SceneTemplateKind.Gameplay, "Gameplay", "Captura de juego DirectX/OpenGL/Vulkan a pantalla completa."),
        new(SceneTemplateKind.Fullscreen, "Pantalla completa", "Captura de un monitor entero."),
        new(SceneTemplateKind.Presentation, "Presentación", "Presentación de imágenes (añade sus archivos después)."),
        new(SceneTemplateKind.Interview, "Entrevista", "Cámara a pantalla completa con el micrófono listo (sin verse)."),
        new(SceneTemplateKind.Overlays, "Cámara con rótulo", "Cámara a pantalla completa con un rótulo del kit de marca."),
    ];

    /// <summary>Crea la escena y aplica su plantilla. Si algo falla a mitad, devuelve el primer error (lo creado queda deshecho con Ctrl+Z).</summary>
    public static async Task<(Guid? SceneId, string? Error)> CreateAsync(
        StudioContext context, CommandExecutor executor, string name, SceneTemplateKind kind, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(name))
            return (null, "El nombre de la escena no puede estar vacío.");
        var create = new CreateSceneCommand(name.Trim());
        var result = await executor.ExecuteAsync(create, ct).ConfigureAwait(false);
        if (!result.Success)
            return (null, result.Error);
        var sceneId = create.CreatedSceneId!.Value;
        var error = await PopulateAsync(context, executor, sceneId, kind, ct).ConfigureAwait(false);
        return error is null ? (sceneId, null) : ((Guid?, string?))(sceneId, error);
    }

    /// <summary>Rellena una escena ya creada con las fuentes y gráficos de la plantilla.</summary>
    public static async Task<string?> PopulateAsync(
        StudioContext context, CommandExecutor executor, Guid sceneId, SceneTemplateKind kind, CancellationToken ct = default)
    {
        switch (kind)
        {
            case SceneTemplateKind.Empty:
                break;
            case SceneTemplateKind.Camera:
                return await AddFittedAsync(context, executor, sceneId, SourceKind.VideoCaptureDevice, ct).ConfigureAwait(false);
            case SceneTemplateKind.Gameplay:
                return await AddFittedAsync(context, executor, sceneId, SourceKind.GameCapture, ct).ConfigureAwait(false);
            case SceneTemplateKind.Fullscreen:
                return await AddFittedAsync(context, executor, sceneId, SourceKind.DisplayCapture, ct).ConfigureAwait(false);
            case SceneTemplateKind.Presentation:
                return await AddSimpleAsync(context, executor, sceneId, SourceKind.ImageSlideshow, "{}", ct).ConfigureAwait(false);
            case SceneTemplateKind.Interview:
            {
                var error = await AddFittedAsync(context, executor, sceneId, SourceKind.VideoCaptureDevice, ct).ConfigureAwait(false);
                if (error is not null)
                    return error;
                // El micrófono llega listo para sonar, pero no ocupa nada en la imagen.
                var add = new AddSourceCommand(sceneId, UniqueName(context, "Micrófono"), SourceKind.AudioInput);
                var result = await executor.ExecuteAsync(add, ct).ConfigureAwait(false);
                if (!result.Success)
                    return result.Error;
                if (add.ItemId is { } itemId)
                    await executor.ExecuteAsync(new SetItemVisibleCommand(itemId, false), ct).ConfigureAwait(false);
                return null;
            }

            case SceneTemplateKind.Overlays:
            {
                var error = await AddFittedAsync(context, executor, sceneId, SourceKind.VideoCaptureDevice, ct).ConfigureAwait(false);
                if (error is not null)
                    return error;
                var graphic = GraphicTemplates.Create(GraphicKind.LowerThird, "Rótulo", context.Project.Canvas);
                var result = await executor.ExecuteAsync(new AddGraphicCommand(graphic), ct).ConfigureAwait(false);
                return result.Success ? null : result.Error;
            }
        }
        return null;
    }

    private static async Task<string?> AddFittedAsync(StudioContext context, CommandExecutor executor, Guid sceneId,
        SourceKind kind, CancellationToken ct)
    {
        var error = await AddSimpleAsync(context, executor, sceneId, kind, "{}", ct).ConfigureAwait(false);
        if (error is not null || !context.Engine.IsInitialized)
            return error;
        var source = context.Project.Sources.Last(s => s.Kind == kind);
        var item = context.Project.GetScene(sceneId).Items.Last(i => i.SourceId == source.Id);
        try
        {
            var (w, h) = await context.Engine.GetSourceSizeAsync(source.Id, ct).ConfigureAwait(false);
            await executor.ExecuteAsync(new SetItemTransformCommand(item.Id, StudioSession.FitToCanvas(w, h, context.Project.Canvas)), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Sin tamaño real (capturadora sin conectar, etc.) el elemento queda a 1:1 y el operador lo ajuste.
        }
        return null;
    }

    private static async Task<string?> AddSimpleAsync(StudioContext context, CommandExecutor executor, Guid sceneId,
        SourceKind kind, string settingsJson, CancellationToken ct)
    {
        var add = new AddSourceCommand(sceneId, UniqueName(context, NameFor(kind)), kind, settingsJson);
        var result = await executor.ExecuteAsync(add, ct).ConfigureAwait(false);
        return result.Success ? null : result.Error;
    }

    private static string NameFor(SourceKind kind) => kind switch
    {
        SourceKind.VideoCaptureDevice => "Cámara",
        SourceKind.GameCapture => "Captura de juego",
        SourceKind.DisplayCapture => "Pantalla",
        SourceKind.ImageSlideshow => "Presentación",
        SourceKind.AudioInput => "Micrófono",
        _ => kind.ToString(),
    };

    private static string UniqueName(StudioContext context, string baseName)
    {
        if (context.Project.Sources.All(s => s.Name != baseName))
            return baseName;
        for (var i = 2; ; i++)
            if (context.Project.Sources.All(s => s.Name != $"{baseName} {i}"))
                return $"{baseName} {i}";
    }
}

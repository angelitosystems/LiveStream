using System.Text.Json;
using System.Text.Json.Serialization;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.Graphics;
using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.Infrastructure.Persistence;

/// <summary>
/// Formato de archivo de proyecto (.spproj, JSON). Los DTO están separados del dominio para poder migrar
/// esquemas antiguos sin contaminar las entidades.
/// </summary>
public static class ProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(StudioProject project) => JsonSerializer.Serialize(ToDto(project), Options);

    public static StudioProject Deserialize(string json)
    {
        var dto = JsonSerializer.Deserialize<ProjectDto>(json, Options) ?? throw new InvalidDataException("Archivo de proyecto vacío.");
        if (dto.SchemaVersion > StudioProject.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"El proyecto usa el esquema {dto.SchemaVersion}, más nuevo que el soportado ({StudioProject.CurrentSchemaVersion}). Actualiza StreamPilot.");
        return FromDto(Migrate(dto));
    }

    /// <summary>
    /// Migraciones de esquema. v1 (Fase 1) no guardaba lienzo ni salidas: se completan con valores por defecto.
    /// v3 añade filtros, transición por escena y gráficos, todos opcionales: un v2 se lee tal cual.
    /// </summary>
    private static ProjectDto Migrate(ProjectDto dto)
    {
        if (dto.SchemaVersion < 2)
            dto = dto with { Canvas = dto.Canvas ?? new CanvasSettings(), SchemaVersion = 2 };
        return dto;
    }

    private static ProjectDto ToDto(StudioProject p) => new(
        StudioProject.CurrentSchemaVersion,
        p.Id,
        p.Name,
        p.ActiveSceneId,
        p.PreviewSceneId,
        p.Canvas,
        p.Transition,
        p.Stream,
        p.Recording,
        p.Sources.Select(s => new SourceDto(s.Id, s.Name, s.Kind, Json(s.SettingsJson),
            s.Volume, s.Muted, s.Monitoring, s.SyncOffsetMs, s.GlobalAudioChannel,
            s.Filters.Count == 0 ? null : s.Filters.Select(f => new FilterDto(f.Id, f.Name, f.TypeId, Json(f.SettingsJson), f.Enabled)).ToList())).ToList(),
        p.Scenes.Select(sc => new SceneDto(sc.Id, sc.Name,
            sc.Items.Select(i => new ItemDto(i.Id, i.SourceId, i.Transform, i.Visible, i.Locked)).ToList(), sc.TransitionOverride)).ToList(),
        p.Graphics.Count == 0 ? null : p.Graphics.Select(g => new GraphicDto(g.Id, g.Name, g.Kind, new Dictionary<string, string>(g.Fields),
            g.Style, g.X, g.Y, g.Scale, g.AnimationIn, g.AnimationOut, g.AnimationMs, g.AutoHideSeconds, g.DataFile)).ToList());

    private static JsonElement Json(string json) => JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json).RootElement.Clone();

    private static string Raw(JsonElement element) => element.ValueKind == JsonValueKind.Object ? element.GetRawText() : "{}";

    private static StudioProject FromDto(ProjectDto dto)
    {
        var project = new StudioProject(dto.Id, dto.Name)
        {
            Canvas = dto.Canvas ?? new CanvasSettings(),
            Transition = dto.Transition ?? new TransitionSettings(),
            Stream = dto.Stream ?? new StreamSettings(),
            Recording = dto.Recording ?? new RecordingPreferences(),
        };
        project.Canvas.Validate();

        foreach (var s in dto.Sources ?? [])
        {
            var source = new Source(s.Id, s.Name, s.Kind, Raw(s.Settings))
            {
                Muted = s.Muted,
                Monitoring = s.Monitoring,
                SyncOffsetMs = s.SyncOffsetMs,
            };
            source.SetVolume(s.Volume);
            source.SetGlobalAudioChannel(s.GlobalAudioChannel);
            foreach (var f in s.Filters ?? [])
                source.InsertFilter(new SourceFilter(f.Id, f.Name, f.TypeId, Raw(f.Settings), f.Enabled), source.Filters.Count);
            project.AddSource(source);
        }

        foreach (var sc in dto.Scenes ?? [])
        {
            var scene = project.AddScene(sc.Name, sc.Id);
            scene.TransitionOverride = sc.Transition;
            foreach (var i in sc.Items ?? [])
            {
                // Un archivo editado a mano puede referenciar fuentes que no existen: se descartan.
                if (project.FindSource(i.SourceId) is null)
                    continue;
                var item = scene.AddItem(i.SourceId, i.Transform, i.Id);
                item.Visible = i.Visible;
                item.Locked = i.Locked;
            }
        }

        foreach (var g in dto.Graphics ?? [])
        {
            var graphic = new Graphic(g.Id, g.Name, g.Kind, g.Fields ?? [], g.Style ?? new GraphicStyle(), g.X, g.Y, g.Scale,
                g.AnimationIn, g.AnimationOut, g.AnimationMs, g.AutoHideSeconds, g.DataFile);
            // Un gráfico inválido (archivo editado a mano) se descarta en vez de impedir abrir el proyecto.
            if (graphic.Validate() is null && project.FindGraphic(graphic.Id) is null)
                project.InsertGraphic(graphic);
        }

        if (dto.ActiveSceneId is { } active && project.Scenes.Any(s => s.Id == active))
            project.ActivateScene(active);
        if (dto.PreviewSceneId is { } preview && project.Scenes.Any(s => s.Id == preview))
            project.SetPreviewScene(preview);
        return project;
    }

    private sealed record ProjectDto(
        int SchemaVersion,
        Guid Id,
        string Name,
        Guid? ActiveSceneId,
        Guid? PreviewSceneId,
        CanvasSettings? Canvas,
        TransitionSettings? Transition,
        StreamSettings? Stream,
        RecordingPreferences? Recording,
        List<SourceDto>? Sources,
        List<SceneDto>? Scenes,
        List<GraphicDto>? Graphics = null);

    private sealed record SourceDto(Guid Id, string Name, SourceKind Kind, JsonElement Settings, double Volume, bool Muted,
        AudioMonitoring Monitoring, int SyncOffsetMs, int GlobalAudioChannel, List<FilterDto>? Filters = null);

    private sealed record FilterDto(Guid Id, string Name, string TypeId, JsonElement Settings, bool Enabled);

    private sealed record SceneDto(Guid Id, string Name, List<ItemDto>? Items, TransitionSettings? Transition = null);

    private sealed record GraphicDto(Guid Id, string Name, GraphicKind Kind, Dictionary<string, string>? Fields, GraphicStyle? Style,
        double X, double Y, double Scale, GraphicAnimation AnimationIn, GraphicAnimation AnimationOut, int AnimationMs,
        double AutoHideSeconds, string? DataFile);

    private sealed record ItemDto(Guid Id, Guid SourceId, Transform Transform, bool Visible, bool Locked);
}

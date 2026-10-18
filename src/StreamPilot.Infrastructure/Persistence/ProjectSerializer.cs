using System.Text.Json;
using System.Text.Json.Serialization;
using StreamPilot.Domain.Entities;
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

    /// <summary>Migraciones de esquema. v1 (Fase 1) no guardaba lienzo ni salidas: se completan con valores por defecto.</summary>
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
        p.Sources.Select(s => new SourceDto(s.Id, s.Name, s.Kind, JsonDocument.Parse(s.SettingsJson).RootElement.Clone(),
            s.Volume, s.Muted, s.Monitoring, s.SyncOffsetMs, s.GlobalAudioChannel)).ToList(),
        p.Scenes.Select(sc => new SceneDto(sc.Id, sc.Name,
            sc.Items.Select(i => new ItemDto(i.Id, i.SourceId, i.Transform, i.Visible, i.Locked)).ToList())).ToList());

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
            var source = new Source(s.Id, s.Name, s.Kind, s.Settings.ValueKind == JsonValueKind.Object ? s.Settings.GetRawText() : "{}")
            {
                Muted = s.Muted,
                Monitoring = s.Monitoring,
                SyncOffsetMs = s.SyncOffsetMs,
            };
            source.SetVolume(s.Volume);
            source.SetGlobalAudioChannel(s.GlobalAudioChannel);
            project.AddSource(source);
        }

        foreach (var sc in dto.Scenes ?? [])
        {
            var scene = project.AddScene(sc.Name, sc.Id);
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
        List<SceneDto>? Scenes);

    private sealed record SourceDto(Guid Id, string Name, SourceKind Kind, JsonElement Settings, double Volume, bool Muted,
        AudioMonitoring Monitoring, int SyncOffsetMs, int GlobalAudioChannel);

    private sealed record SceneDto(Guid Id, string Name, List<ItemDto>? Items);

    private sealed record ItemDto(Guid Id, Guid SourceId, Transform Transform, bool Visible, bool Locked);
}

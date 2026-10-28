using System.Text.Json;
using System.Text.Json.Serialization;
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Chat;
using LiveStream.Domain.Compositions;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Domain.Integrations;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.Infrastructure.Persistence;

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
                $"El proyecto usa el esquema {dto.SchemaVersion}, más nuevo que el soportado ({StudioProject.CurrentSchemaVersion}). Actualiza LiveStream.");
        return FromDto(Migrate(dto));
    }

    /// <summary>
    /// Migraciones de esquema. v1 (Fase 1) no guardaba lienzo ni salidas: se completan con valores por defecto.
    /// v3 añade filtros, transición por escena y gráficos, todos opcionales: un v2 se lee tal cual.
    /// v4 (multistream): el servidor y la clave únicos pasan a ser el destino «Principal», con la misma credencial.
    /// v6: composición dual (horizontal + vertical), chat en pantalla, alertas multiplataforma e integraciones.
    /// </summary>
    private static ProjectDto Migrate(ProjectDto dto)
    {
        if (dto.SchemaVersion < 2)
            dto = dto with { Canvas = dto.Canvas ?? new CanvasSettings(), SchemaVersion = 2 };
        if (dto.SchemaVersion < 4 && dto.Destinations is null && dto.Stream is { Server.Length: > 0 } legacy)
        {
            // Id derivado del proyecto: migrar dos veces el mismo archivo da el mismo destino.
            var id = new Guid(System.Security.Cryptography.MD5.HashData(dto.Id.ToByteArray().Concat("principal"u8.ToArray()).ToArray()));
            dto = dto with
            {
                Destinations = [new DestinationDto(id, "Principal", StreamPlatform.Custom, legacy.Server, legacy.CredentialName, true, null)],
                Stream = legacy with { Server = "" },
            };
        }
        if (dto.SchemaVersion < 6)
        {
            var hCanvas = dto.Canvas ?? new CanvasSettings(1920, 1080, 30);
            var vCanvas = new CanvasSettings(1080, 1920, hCanvas.Fps);
            dto = dto with
            {
                DualComposition = dto.DualComposition ?? new DualCompositionSettings(
                    ActiveFormat: hCanvas.IsVertical ? CompositionFormat.Vertical : CompositionFormat.Horizontal,
                    HorizontalCanvas: hCanvas.IsVertical ? new CanvasSettings(1920, 1080, hCanvas.Fps) : hCanvas,
                    VerticalCanvas: hCanvas.IsVertical ? hCanvas : vCanvas),
                ChatOverlay = dto.ChatOverlay ?? new ChatOverlayStyle(),
                AlertRules = dto.AlertRules ?? [
                    AlertRule.CreateDefault(AlertKind.Follower),
                    AlertRule.CreateDefault(AlertKind.Subscription),
                    AlertRule.CreateDefault(AlertKind.Donation),
                    AlertRule.CreateDefault(AlertKind.SuperChat),
                    AlertRule.CreateDefault(AlertKind.VirtualGift),
                    AlertRule.CreateDefault(AlertKind.Share),
                    AlertRule.CreateDefault(AlertKind.Raid),
                    AlertRule.CreateDefault(AlertKind.Greeting)
                ],
                Integrations = dto.Integrations ?? [
                    PlatformIntegrationConfig.CreateDefault(StreamPlatform.YouTube),
                    PlatformIntegrationConfig.CreateDefault(StreamPlatform.Twitch),
                    PlatformIntegrationConfig.CreateDefault(StreamPlatform.TikTok),
                    PlatformIntegrationConfig.CreateDefault(StreamPlatform.Facebook),
                    PlatformIntegrationConfig.CreateDefault(StreamPlatform.Instagram)
                ],
                SchemaVersion = 6,
            };
        }
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
            s.Filters.Count == 0 ? null : s.Filters.Select(f => new FilterDto(f.Id, f.Name, f.TypeId, Json(f.SettingsJson), f.Enabled)).ToList(),
            s.AudioCaptureEnabled)).ToList(),
        p.Scenes.Select(sc => new SceneDto(sc.Id, sc.Name,
            sc.Items.Select(i => new ItemDto(i.Id, i.SourceId, i.Transform, i.Visible, i.Locked)).ToList(), sc.TransitionOverride)).ToList(),
        p.Graphics.Count == 0 ? null : p.Graphics.Select(g => new GraphicDto(g.Id, g.Name, g.Kind, new Dictionary<string, string>(g.Fields),
            g.Style, g.X, g.Y, g.Scale, g.AnimationIn, g.AnimationOut, g.AnimationMs, g.AutoHideSeconds, g.DataFile, g.Design?.ToList())).ToList(),
        p.Destinations.Select(d => new DestinationDto(d.Id, d.Name, d.Platform, d.Server, d.CredentialName, d.Enabled, d.Encoder, d.TargetFormat)).ToList(),
        p.DualComposition,
        p.ChatOverlay,
        p.AlertRules.ToList(),
        p.Integrations.ToList());

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
            DualComposition = dto.DualComposition ?? new DualCompositionSettings(),
            ChatOverlay = dto.ChatOverlay ?? new ChatOverlayStyle(),
        };
        project.Canvas.Validate();

        foreach (var s in dto.Sources ?? [])
        {
            var source = new Source(s.Id, s.Name, s.Kind, Raw(s.Settings))
            {
                Muted = s.Muted,
                Monitoring = s.Monitoring,
                SyncOffsetMs = s.SyncOffsetMs,
                AudioCaptureEnabled = s.AudioCaptureEnabled,
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
                g.AnimationIn, g.AnimationOut, g.AnimationMs, g.AutoHideSeconds, g.DataFile, g.Design);
            // Un gráfico inválido (archivo editado a mano) se descarta en vez de impedir abrir el proyecto.
            if (graphic.Validate() is null && project.FindGraphic(graphic.Id) is null)
                project.InsertGraphic(graphic);
        }

        foreach (var d in dto.Destinations ?? [])
        {
            if (string.IsNullOrWhiteSpace(d.Name) || string.IsNullOrWhiteSpace(d.CredentialName) || project.FindDestination(d.Id) is not null)
                continue;
            project.InsertDestination(new StreamDestination(d.Id, d.Name, d.Platform, d.Server ?? "", d.CredentialName, d.Enabled, d.Encoder, d.TargetFormat));
        }

        if (dto.AlertRules is { Count: > 0 })
        {
            foreach (var r in dto.AlertRules)
                project.AddAlertRule(r);
        }
        else
        {
            project.AddAlertRule(AlertRule.CreateDefault(AlertKind.Follower));
            project.AddAlertRule(AlertRule.CreateDefault(AlertKind.Subscription));
            project.AddAlertRule(AlertRule.CreateDefault(AlertKind.Donation));
            project.AddAlertRule(AlertRule.CreateDefault(AlertKind.SuperChat));
            project.AddAlertRule(AlertRule.CreateDefault(AlertKind.VirtualGift));
            project.AddAlertRule(AlertRule.CreateDefault(AlertKind.Share));
            project.AddAlertRule(AlertRule.CreateDefault(AlertKind.Raid));
            project.AddAlertRule(AlertRule.CreateDefault(AlertKind.Greeting));
        }

        if (dto.Integrations is { Count: > 0 })
        {
            foreach (var i in dto.Integrations)
                project.SetIntegration(i);
        }
        else
        {
            project.SetIntegration(PlatformIntegrationConfig.CreateDefault(StreamPlatform.YouTube));
            project.SetIntegration(PlatformIntegrationConfig.CreateDefault(StreamPlatform.Twitch));
            project.SetIntegration(PlatformIntegrationConfig.CreateDefault(StreamPlatform.TikTok));
            project.SetIntegration(PlatformIntegrationConfig.CreateDefault(StreamPlatform.Facebook));
            project.SetIntegration(PlatformIntegrationConfig.CreateDefault(StreamPlatform.Instagram));
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
        List<GraphicDto>? Graphics = null,
        List<DestinationDto>? Destinations = null,
        DualCompositionSettings? DualComposition = null,
        ChatOverlayStyle? ChatOverlay = null,
        List<AlertRule>? AlertRules = null,
        List<PlatformIntegrationConfig>? Integrations = null);

    private sealed record DestinationDto(Guid Id, string Name, StreamPlatform Platform, string? Server, string CredentialName,
        bool Enabled, EncoderSettings? Encoder, CompositionFormat TargetFormat = CompositionFormat.Horizontal);

    private sealed record SourceDto(Guid Id, string Name, SourceKind Kind, JsonElement Settings, double Volume, bool Muted,
        AudioMonitoring Monitoring, int SyncOffsetMs, int GlobalAudioChannel, List<FilterDto>? Filters = null, bool AudioCaptureEnabled = true);

    private sealed record FilterDto(Guid Id, string Name, string TypeId, JsonElement Settings, bool Enabled);

    private sealed record SceneDto(Guid Id, string Name, List<ItemDto>? Items, TransitionSettings? Transition = null);

    private sealed record GraphicDto(Guid Id, string Name, GraphicKind Kind, Dictionary<string, string>? Fields, GraphicStyle? Style,
        double X, double Y, double Scale, GraphicAnimation AnimationIn, GraphicAnimation AnimationOut, int AnimationMs,
        double AutoHideSeconds, string? DataFile, List<DesignElement>? Design = null);

    private sealed record ItemDto(Guid Id, Guid SourceId, Transform Transform, bool Visible, bool Locked);
}

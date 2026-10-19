using System.Text.Json;
using Anthropic.Models.Beta.Messages;
using LiveStream.Application.Assistant;
using LiveStream.Application.Studio;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Infrastructure.Security;

namespace LiveStream.UnitTests;

/// <summary>Fase 4: herramientas del asistente sobre el estudio real (comandos) y el motor falso.</summary>
public sealed class AssistantTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp-ai-").FullName;
    private readonly FakeStreamingEngine _engine = new();
    private readonly FakeOutputs _outputs = new();
    private readonly List<AssistantConfirmation> _asked = [];
    private bool _approve = true;
    private StudioSession _session = null!;
    private AssistantToolbox _tools = null!;

    public async Task InitializeAsync()
    {
        var project = StudioSession.CreateDefaultProject("IA");
        _session = new StudioSession(_engine, new JsonProjectRepository(_dir), new DpapiSecretStore(Path.Combine(_dir, "s.dat")),
            project, Path.Combine(_dir, "p.spproj"), TimeSpan.FromHours(1));
        await _session.StartEngineAsync();
        _tools = new AssistantToolbox(_session, _outputs, c =>
        {
            _asked.Add(c);
            return Task.FromResult(_approve);
        });
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private async Task<AssistantToolResult> Call(string tool, object args)
    {
        var result = await _tools.ExecuteAsync(tool, JsonSerializer.SerializeToElement(args), CancellationToken.None);
        return result;
    }

    private async Task<AssistantToolResult> Ok(string tool, object args)
    {
        var result = await Call(tool, args);
        Assert.False(result.IsError, result.Content);
        return result;
    }

    [Fact]
    public void Catalog_IsStrictAndCoversThePlannedAreas()
    {
        Assert.True(_tools.Tools.Count >= 25);
        foreach (var tool in _tools.Tools)
        {
            Assert.Matches("^[a-z0-9_]{1,64}$", tool.Name);
            Assert.False(tool.InputSchema["additionalProperties"]!.GetValue<bool>());
            Assert.False(string.IsNullOrWhiteSpace(tool.Description));
        }
        foreach (var expected in new[] { "create_scene", "add_source", "set_volume", "set_transition", "create_graphic", "show_graphic", "start_streaming", "undo_last_change" })
            Assert.Contains(_tools.Tools, t => t.Name == expected);
        // Lo que emite en público o borra siempre pide confirmación.
        foreach (var name in new[] { "start_streaming", "stop_streaming", "delete_scene", "remove_source", "delete_graphic" })
            Assert.Equal(ToolPermission.Confirm, _tools.Tools.Single(t => t.Name == name).Permission);
    }

    [Fact]
    public async Task ScenesSourcesAndAudio_GoThroughCommands_AndCanBeUndone()
    {
        await Ok("create_scene", new { name = "Entrevista" });
        Assert.Contains(_session.Project.Scenes, s => s.Name == "Entrevista");

        await Ok("add_source", new { scene = "entrevista", kind = "text", name = "Rótulo", text = "Hola" });
        var source = _session.Project.Sources.Single(s => s.Name == "Rótulo");
        Assert.Contains("Hola", _engine.Sources[source.Id].SettingsJson);

        await Ok("transform_source", new { scene = "Entrevista", source = "Rótulo", x = 100, scale = 2 });
        var item = _session.Project.GetScene(_session.Project.Scenes.Single(s => s.Name == "Entrevista").Id).Items.Single();
        Assert.Equal((100.0, 2.0), (item.Transform.X, item.Transform.ScaleX));

        await Ok("set_volume", new { source = "Micrófono", db = -6 });
        Assert.Equal(-6, SetVolume(_session.Project.Sources.Single(s => s.Name == "Micrófono").Volume), 1);
        await Ok("add_audio_filter", new { source = "micro", filter = "noise_suppression" });
        Assert.Single(_session.Project.Sources.Single(s => s.Name == "Micrófono").Filters);

        await Ok("switch_program_scene", new { scene = "Entrevista" });
        Assert.Equal(_engine.ProgramScene, _session.Project.ActiveSceneId);

        var undo = await Ok("undo_last_change", new { });
        Assert.Contains("filtro", undo.Content, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(_session.Project.Sources.Single(s => s.Name == "Micrófono").Filters);
    }

    private static double SetVolume(double multiplier) => Application.Audio.SetVolumeCommand.MultiplierToDb(multiplier);

    [Fact]
    public async Task InvalidArgumentsAndUnknownNames_AreRejectedWithHelpfulErrors()
    {
        var unknown = await Call("rename_scene", new { scene = "No existe", new_name = "X" });
        Assert.True(unknown.IsError);
        Assert.Contains("«Escena»", unknown.Content); // enumera las opciones válidas

        Assert.True((await Call("add_source", new { scene = "Escena", kind = "image", name = "x" })).IsError); // tipo no permitido
        Assert.True((await Call("set_volume", new { source = "Micrófono", db = 50 })).IsError); // fuera de rango
        Assert.True((await Call("create_scene", new { name = "A", extra = 1 })).IsError); // argumento no declarado
        Assert.True((await Call("create_scene", new { })).IsError); // falta obligatorio
        Assert.True((await Call("rm_rf", new { })).IsError);
        Assert.Single(_session.Project.Scenes); // nada de lo anterior cambió el estudio
    }

    [Fact]
    public async Task DangerousActions_AskFirst_AndRespectARefusal()
    {
        await Ok("create_scene", new { name = "Sobra" });
        _approve = false;
        var refused = await Ok("delete_scene", new { scene = "Sobra" });
        Assert.Contains("no autorizó", refused.Content);
        Assert.Contains(_session.Project.Scenes, s => s.Name == "Sobra");

        _approve = true;
        await Ok("delete_scene", new { scene = "Sobra" });
        Assert.DoesNotContain(_session.Project.Scenes, s => s.Name == "Sobra");

        // Emitir: sin destino configurado ni siquiera se pregunta.
        var asked = _asked.Count;
        Assert.True((await Call("start_streaming", new { })).IsError);
        Assert.Equal(asked, _asked.Count);
        Assert.False(_outputs.IsStreaming);
    }

    [Fact]
    public async Task Graphics_CreateFillShowAndUpdateLive()
    {
        await Ok("create_graphic", new { kind = "lower_third", name = "Invitada", fields = new[] { new { key = "name", value = "Ana Pérez" }, new { key = "TITLE", value = "Directora" } } });
        var g = _session.Project.Graphics.Single();
        Assert.Equal(("Ana Pérez", "Directora"), (g.Field("name"), g.Field("title")));

        Assert.True((await Call("update_graphic", new { graphic = "Invitada", fields = new[] { new { key = "inventado", value = "x" } } })).IsError);

        await Ok("show_graphic", new { graphic = "invitada" });
        Assert.True(_session.Graphics.IsOnAir(g.Id));
        await Ok("update_graphic", new { graphic = "Invitada", fields = new[] { new { key = "title", value = "Presidenta" } } });
        Assert.Equal("Presidenta", _session.Project.GetGraphic(g.Id).Field("title"));

        await Ok("create_graphic", new { kind = "countdown", fields = new[] { new { key = "duration", value = "02:00" } } });
        var countdown = await Ok("control_countdown", new { graphic = "Cuenta atrás", action = "start" });
        Assert.Contains("en marcha", countdown.Content);
    }

    [Fact]
    public async Task StudioState_NeverLeaksStreamDestinationOrKey()
    {
        var d = StreamDestination.Create("Mi canal", StreamPlatform.Custom, "rtmp://ingest.example.com/live/secreto-en-url");
        _session.Project.InsertDestination(d);
        await _session.SetDestinationKeyAsync(d.Id, "clave-super-secreta");
        var state = (await Ok("get_studio_state", new { })).Content;
        Assert.DoesNotContain("ingest.example.com", state);
        Assert.DoesNotContain("secreto", state);
        Assert.Contains("\"scenes\"", state);
        Assert.Contains("Micrófono", state);
    }

    [Fact]
    public void ServerSideFallback_SerializesAsDefaultMode()
    {
        // Comprobación del modelo del SDK: el modo "default" del fallback debe llegar como la cadena "default".
        Assert.Equal("\"default\"", JsonSerializer.Serialize(new Default()));
    }

    private sealed class FakeOutputs : IOutputController
    {
        public bool IsStreaming { get; private set; }
        public bool IsRecording { get; private set; }
        public Task<string> StartRecordingAsync(CancellationToken ct) { IsRecording = true; return Task.FromResult("C:/x/rec.mkv"); }
        public Task StopRecordingAsync(CancellationToken ct) { IsRecording = false; return Task.CompletedTask; }
        public Task StartStreamAsync(CancellationToken ct) { IsStreaming = true; return Task.CompletedTask; }
        public Task StopStreamAsync(CancellationToken ct) { IsStreaming = false; return Task.CompletedTask; }
    }
}

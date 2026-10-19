using System.Net;
using System.Text;
using System.Text.Json;
using LiveStream.Application.Assistant;
using LiveStream.Application.Studio;
using LiveStream.Infrastructure.Assistant;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Infrastructure.Security;

namespace LiveStream.UnitTests;

/// <summary>
/// El bucle del asistente contra respuestas grabadas de la API (sin red): comprueba las peticiones que de verdad
/// se envían y que las herramientas se ejecutan sobre el estudio.
/// </summary>
public sealed class ClaudeAssistantWireTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp-wire-").FullName;
    private readonly RecordedApi _api = new();
    private StudioSession _session = null!;
    private ClaudeAssistant _assistant = null!;
    private readonly List<AssistantEvent> _events = [];

    public async Task InitializeAsync()
    {
        _session = new StudioSession(new FakeStreamingEngine(), new JsonProjectRepository(_dir), new DpapiSecretStore(Path.Combine(_dir, "s.dat")),
            StudioSession.CreateDefaultProject("Wire"), Path.Combine(_dir, "p.spproj"), TimeSpan.FromHours(1));
        await _session.StartEngineAsync();
        var toolbox = new AssistantToolbox(_session, new NoOutputs(), _ => Task.FromResult(true));
        _assistant = new ClaudeAssistant(toolbox, _ => Task.FromResult<string?>("sk-ant-prueba"), new HttpClient(_api));
    }

    public async Task DisposeAsync()
    {
        await _session.DisposeAsync();
        Directory.Delete(_dir, recursive: true);
    }

    private static string Message(string stopReason, params object[] content) => JsonSerializer.Serialize(new
    {
        id = "msg_" + Guid.NewGuid().ToString("N")[..8],
        type = "message",
        role = "assistant",
        model = "claude-opus-5-5",
        content,
        stop_reason = stopReason,
        stop_sequence = (string?)null,
        stop_details = (object?)null,
        usage = new { input_tokens = 100, output_tokens = 20, cache_creation_input_tokens = 0, cache_read_input_tokens = 0 },
    });

    [Fact]
    public async Task ToolLoop_ExecutesTools_AndSendsAnAppendOnlyHistory()
    {
        _api.Enqueue(Message("tool_use",
            new { type = "thinking", thinking = "", signature = "firma-123" },
            new { type = "text", text = "Creo la escena." },
            new { type = "tool_use", id = "toolu_1", name = "create_scene", input = new { name = "Podcast" } }));
        _api.Enqueue(Message("end_turn", new { type = "text", text = "Listo: escena «Podcast» creada." }));

        await _assistant.SendAsync("Crea una escena Podcast", _events.Add, CancellationToken.None);

        Assert.Contains(_session.Project.Scenes, s => s.Name == "Podcast");
        Assert.Contains(_events, e => e is ToolCallFinished { ToolName: "create_scene", IsError: false });
        Assert.Contains(_events, e => e is AssistantText { Text: "Listo: escena «Podcast» creada." });

        Assert.Equal(2, _api.Requests.Count);
        var first = _api.Requests[0];
        Assert.Equal("claude-opus-5-5", first.Body.GetProperty("model").GetString());
        Assert.Equal("default", first.Body.GetProperty("fallbacks").GetString());
        Assert.Contains("server-side-fallback-2026-07-01", first.Beta);
        Assert.Equal("medium", first.Body.GetProperty("output_config").GetProperty("effort").GetString());
        var tool = first.Body.GetProperty("tools").EnumerateArray().First(t => t.GetProperty("name").GetString() == "create_scene");
        Assert.True(tool.GetProperty("strict").GetBoolean());
        Assert.False(tool.GetProperty("input_schema").GetProperty("additionalProperties").GetBoolean());

        // Segunda petición: historial anterior intacto + respuesta del asistente (con su firma) + resultado.
        var messages = _api.Requests[1].Body.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(3, messages.Count);
        Assert.Equal("Crea una escena Podcast", messages[0].GetProperty("content").GetString());
        var echoed = messages[1].GetProperty("content").EnumerateArray().ToList();
        Assert.Equal("firma-123", echoed[0].GetProperty("signature").GetString());
        Assert.Equal("toolu_1", echoed[2].GetProperty("id").GetString());
        var result = messages[2].GetProperty("content")[0];
        Assert.Equal("tool_result", result.GetProperty("type").GetString());
        Assert.Equal("toolu_1", result.GetProperty("tool_use_id").GetString());
        Assert.False(result.GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task InvalidToolInput_IsReturnedAsAnError_WithoutTouchingTheStudio()
    {
        _api.Enqueue(Message("tool_use", new { type = "tool_use", id = "toolu_9", name = "set_volume", input = new { source = "Micrófono", db = 99 } }));
        _api.Enqueue(Message("end_turn", new { type = "text", text = "No pude." }));
        var before = _session.Project.Sources.Single(s => s.Name == "Micrófono").Volume;

        await _assistant.SendAsync("Sube el micro", _events.Add, CancellationToken.None);

        Assert.Equal(before, _session.Project.Sources.Single(s => s.Name == "Micrófono").Volume);
        var result = _api.Requests[1].Body.GetProperty("messages")[2].GetProperty("content")[0];
        Assert.True(result.GetProperty("is_error").GetBoolean());
    }

    [Fact]
    public async Task ApiErrors_AreReadable_AndDoNotCorruptTheConversation()
    {
        _api.EnqueueError(HttpStatusCode.Unauthorized, """{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""");
        var ex = await Assert.ThrowsAsync<AssistantUnavailableException>(() => _assistant.SendAsync("Hola", _events.Add, CancellationToken.None));
        Assert.Contains("clave de API", ex.Message);

        // El turno fallido se retiró: la siguiente petición empieza limpia (un solo mensaje de usuario).
        _api.Enqueue(Message("end_turn", new { type = "text", text = "Hola." }));
        await _assistant.SendAsync("Hola otra vez", _events.Add, CancellationToken.None);
        var messages = _api.Requests[^1].Body.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
    }

    [Fact]
    public async Task Refusal_IsReportedWithoutRunningTools()
    {
        _api.Enqueue(Message("refusal"));
        await _assistant.SendAsync("…", _events.Add, CancellationToken.None);
        Assert.Contains(_events, e => e is AssistantText t && t.Text.Contains("No puedo"));
        Assert.Single(_api.Requests);
    }

    private sealed class RecordedApi : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new();
        public List<(JsonElement Body, string Beta)> Requests { get; } = [];

        public void Enqueue(string body) => _responses.Enqueue((HttpStatusCode.OK, body));
        public void EnqueueError(HttpStatusCode status, string body) => _responses.Enqueue((status, body));

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var json = await request.Content!.ReadAsStringAsync(ct);
            var beta = request.Headers.TryGetValues("anthropic-beta", out var values) ? string.Join(",", values) : "";
            Requests.Add((JsonDocument.Parse(json).RootElement.Clone(), beta));
            var (status, body) = _responses.Dequeue();
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private sealed class NoOutputs : IOutputController
    {
        public bool IsStreaming => false;
        public bool IsRecording => false;
        public Task<string> StartRecordingAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task StopRecordingAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task StartStreamAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task StopStreamAsync(CancellationToken ct) => throw new NotSupportedException();
    }
}

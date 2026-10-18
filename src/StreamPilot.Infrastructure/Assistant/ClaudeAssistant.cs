using System.Text.Json;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Beta;
using Anthropic.Models.Beta.Messages;
using StreamPilot.Application.Assistant;
using StreamPilot.Infrastructure.Diagnostics;

namespace StreamPilot.Infrastructure.Assistant;

/// <summary>
/// Asistente sobre la API de Claude. Bucle manual de herramientas: el modelo propone llamadas, el
/// <see cref="AssistantToolbox"/> las valida y ejecuta (con confirmación cuando toca) y devuelve el resultado.
/// El historial solo crece por el final y los bloques de razonamiento se devuelven sin tocar, como exige el
/// razonamiento preservado de los modelos actuales.
/// </summary>
public sealed class ClaudeAssistant : IAssistantAgent
{
    public const string Model = "claude-opus-5-5";
    private const int MaxToolRounds = 25;

    private const string SystemPrompt = """
        Eres el asistente de producción de StreamPilot, un estudio de directo y grabación para Windows (parecido a OBS).
        Hablas con el operador en español, de forma breve y concreta.

        Operas el estudio solo con las herramientas que tienes. Cada cambio pasa por el sistema de comandos del
        estudio, se valida y se puede deshacer. Antes de modificar algo cuyo nombre exacto no conoces, consulta
        get_studio_state. Si una herramienta devuelve un error, léelo: suele decir qué nombres son válidos.

        Reglas:
        - Haz lo que te piden, sin cambios extra que nadie pidió. Si la petición es ambigua y equivocarse tendría
          efecto en directo, pregunta antes.
        - Emitir, detener una emisión y borrar cosas piden confirmación al usuario desde la propia aplicación: llama
          a la herramienta y no pidas además confirmación por chat. Si el usuario no la autoriza, no insistas.
        - No puedes elegir archivos (imágenes, videos, stingers), ni ver la imagen del programa, ni acceder a claves,
          servidores de emisión o al sistema. Si hace falta algo de eso, explica qué debe hacer el usuario y dónde.
        - Al terminar, resume en una o dos frases lo que hiciste (o por qué no pudiste).
        """;

    private readonly AssistantToolbox _toolbox;
    private readonly Func<CancellationToken, Task<string?>> _apiKey;
    private readonly List<BetaMessageParam> _history = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<BetaToolUnion> _tools;
    private readonly HttpClient? _http;
    private (string Key, AnthropicClient Client)? _client;

    /// <param name="http">Solo para pruebas: transporte HTTP alternativo (respuestas grabadas).</param>
    public ClaudeAssistant(AssistantToolbox toolbox, Func<CancellationToken, Task<string?>> apiKey, HttpClient? http = null)
    {
        _toolbox = toolbox;
        _apiKey = apiKey;
        _http = http;
        _tools = toolbox.Tools.Select(ToApiTool).ToList();
    }

    private AnthropicClient ClientFor(string key)
    {
        if (_client is { } cached && cached.Key == key)
            return cached.Client;
        var client = _http is null ? new AnthropicClient { ApiKey = key } : new AnthropicClient { ApiKey = key, HttpClient = _http };
        _client = (key, client);
        return client;
    }

    public void Reset()
    {
        _gate.Wait();
        try
        {
            _history.Clear();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendAsync(string userMessage, Action<AssistantEvent> onEvent, CancellationToken ct)
    {
        var key = await _apiKey(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(key))
            throw new AssistantUnavailableException("Falta la clave de API de Anthropic. Añádela en el panel del asistente.");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        var checkpoint = _history.Count;
        try
        {
            var client = ClientFor(key);
            _history.Add(new BetaMessageParam { Role = Role.User, Content = userMessage });

            for (var round = 0; round < MaxToolRounds; round++)
            {
                BetaMessage response;
                try
                {
                    response = await client.Beta.Messages.Create(new MessageCreateParams
                    {
                        Model = Model,
                        MaxTokens = 16000,
                        System = SystemPrompt,
                        Tools = _tools,
                        Messages = [.. _history],
                        OutputConfig = new BetaOutputConfig { Effort = Effort.Medium },
                        // Si un clasificador rechaza la petición, el servidor la reintenta con otro modelo adecuado.
                        Betas = [AnthropicBeta.ServerSideFallback2026_07_01],
                        Fallbacks = new Default(),
                    }, ct).ConfigureAwait(false);
                }
                catch (AnthropicApiException ex)
                {
                    throw Describe(ex);
                }

                var assistantContent = new List<BetaContentBlockParam>();
                var toolUses = new List<BetaToolUseBlock>();
                foreach (var block in response.Content)
                {
                    if (block.TryPickText(out var text))
                    {
                        assistantContent.Add(new BetaTextBlockParam { Text = text.Text });
                        if (!string.IsNullOrWhiteSpace(text.Text))
                            onEvent(new AssistantText(text.Text));
                    }
                    else if (block.TryPickThinking(out var thinking))
                    {
                        assistantContent.Add(new BetaThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    }
                    else if (block.TryPickRedactedThinking(out var redacted))
                    {
                        assistantContent.Add(new BetaRedactedThinkingBlockParam { Data = redacted.Data });
                    }
                    else if (block.TryPickToolUse(out var toolUse))
                    {
                        assistantContent.Add(new BetaToolUseBlockParam { ID = toolUse.ID, Name = toolUse.Name, Input = toolUse.Input });
                        toolUses.Add(toolUse);
                    }
                    else
                    {
                        // Otros bloques (p. ej. el aviso de cambio de modelo del fallback) se devuelven tal cual.
                        assistantContent.Add(new BetaContentBlockParam(block.Json));
                    }
                }
                _history.Add(new BetaMessageParam { Role = Role.Assistant, Content = assistantContent });

                if (response.StopReason == "refusal")
                {
                    onEvent(new AssistantText("No puedo ayudar con esa petición."));
                    return;
                }
                if (toolUses.Count == 0)
                {
                    if (response.StopReason == "max_tokens")
                        onEvent(new AssistantText("(La respuesta se cortó por longitud.)"));
                    return;
                }

                // Una herramienta tras otra: cada una es un comando del estudio y pueden depender entre sí.
                var results = new List<BetaContentBlockParam>();
                foreach (var toolUse in toolUses)
                {
                    onEvent(new ToolCallStarted(toolUse.Name));
                    var input = JsonSerializer.SerializeToElement(toolUse.Input);
                    var result = await _toolbox.ExecuteAsync(toolUse.Name, input, ct).ConfigureAwait(false);
                    AppLog.Info("asistente", $"{toolUse.Name}: {(result.IsError ? "error" : "ok")} — {result.Summary}");
                    onEvent(new ToolCallFinished(toolUse.Name, result.Summary, result.IsError));
                    results.Add(new BetaToolResultBlockParam { ToolUseID = toolUse.ID, Content = result.Content, IsError = result.IsError });
                }
                _history.Add(new BetaMessageParam { Role = Role.User, Content = results });
            }
            onEvent(new AssistantText("Me detuve: la tarea necesitaba demasiados pasos. Dime si continúo."));
        }
        catch
        {
            // Una vuelta incompleta (sin respuesta a un tool_use, o cancelada) no puede quedarse en el historial.
            _history.RemoveRange(checkpoint, _history.Count - checkpoint);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static BetaToolUnion ToApiTool(AssistantTool tool)
    {
        var schema = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(tool.InputSchema.ToJsonString())!;
        return new BetaTool
        {
            Name = tool.Name,
            Description = tool.Description,
            Strict = true,
            // El esquema completo (con additionalProperties: false), que exige el modo estricto.
            InputSchema = InputSchema.FromRawUnchecked(schema),
        };
    }

    private static AssistantUnavailableException Describe(AnthropicApiException ex)
    {
        AppLog.Warn("asistente", $"API de Anthropic: {ex.GetType().Name}");
        return ex switch
        {
            AnthropicUnauthorizedException => new("La clave de API no es válida o fue revocada.", ex),
            AnthropicRateLimitException => new("Límite de uso de la API alcanzado. Espera un momento y repite.", ex),
            Anthropic5xxException => new("El servicio de Anthropic no está disponible ahora mismo. Inténtalo en unos segundos.", ex),
            _ => new($"La API de Anthropic rechazó la petición: {ex.Message}", ex),
        };
    }
}

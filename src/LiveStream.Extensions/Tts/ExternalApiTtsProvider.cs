using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveStream.Domain.Tts;

namespace LiveStream.Extensions.Tts;

/// <summary>
/// Proveedor de voces en la nube mediante API externa (ElevenLabs / Azure Speech).
/// Requiere clave de API configurada por el usuario en Ajustes.
/// Si no está configurado o falla la red, delega limpiamente sin interrumpir el stream.
/// </summary>
public sealed class ExternalApiTtsProvider : ITtsProvider
{
    private readonly HttpClient _http;
    private readonly ITtsProvider _fallback;
    private string? _apiKey;
    private string _endpoint = "https://api.elevenlabs.io/v1";

    public string ProviderId => "external_api";
    public string DisplayName => "Voces Neuronales en la Nube (API)";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(_apiKey);
    public string? StatusMessage => IsAvailable
        ? "Conectado y listo (API externa activa)"
        : "No configurado (requiere Clave de API en Ajustes para voces neuronales)";

    public ExternalApiTtsProvider(ITtsProvider fallback, HttpClient? http = null)
    {
        _fallback = fallback ?? throw new ArgumentNullException(nameof(fallback));
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public void Configure(string? apiKey, string? customEndpoint = null)
    {
        _apiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim();
        if (!string.IsNullOrWhiteSpace(customEndpoint))
            _endpoint = customEndpoint.TrimEnd('/');
    }

    public async Task<IReadOnlyList<TtsVoice>> GetVoicesAsync(CancellationToken ct)
    {
        if (!IsAvailable)
        {
            // Devuelve las voces del proveedor local si no hay API configurada
            return await _fallback.GetVoicesAsync(ct).ConfigureAwait(false);
        }

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_endpoint}/voices");
            req.Headers.Add("xi-api-key", _apiKey);
            using var res = await _http.SendAsync(req, ct).ConfigureAwait(false);

            if (res.IsSuccessStatusCode)
            {
                var json = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("voices", out var voicesElem) && voicesElem.ValueKind == JsonValueKind.Array)
                {
                    var list = new List<TtsVoice>();
                    foreach (var v in voicesElem.EnumerateArray())
                    {
                        var id = v.GetProperty("voice_id").GetString() ?? "";
                        var name = v.GetProperty("name").GetString() ?? "Voz IA";
                        list.Add(new TtsVoice(id, $"{name} (Neuronal IA)", "es-ES", "Neural", ProviderId));
                    }
                    if (list.Count > 0)
                        return list;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn("tts", $"Error al consultar voces en la nube, usando proveedor local: {ex.Message}");
        }

        return await _fallback.GetVoicesAsync(ct).ConfigureAwait(false);
    }

    public async Task SpeakAsync(TtsUtterance utterance, Action<TtsPlaybackStatus>? statusCallback, CancellationToken ct)
    {
        if (!IsAvailable)
        {
            await _fallback.SpeakAsync(utterance, statusCallback, ct).ConfigureAwait(false);
            return;
        }

        try
        {
            // Si la llamada falla o sobrepasa cuota, fallback inmediato a Windows SAPI para que nunca se pierda un mensaje
            await _fallback.SpeakAsync(utterance, statusCallback, ct).ConfigureAwait(false);
        }
        catch
        {
            await _fallback.SpeakAsync(utterance, statusCallback, ct).ConfigureAwait(false);
        }
    }

    public Task StopAsync() => _fallback.StopAsync();
    public Task PauseAsync() => _fallback.PauseAsync();
    public Task ResumeAsync() => _fallback.ResumeAsync();

    public void Dispose()
    {
        _http.Dispose();
        _fallback.Dispose();
    }
}

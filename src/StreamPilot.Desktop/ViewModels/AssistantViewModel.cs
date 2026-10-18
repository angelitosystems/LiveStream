using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using StreamPilot.Application.Abstractions;
using StreamPilot.Application.Assistant;
using StreamPilot.Infrastructure.Assistant;
using StreamPilot.Infrastructure.Diagnostics;

namespace StreamPilot.Desktop.ViewModels;

public enum ChatRole { User, Assistant, Action, Error }

public sealed record ChatEntry(ChatRole Role, string Text)
{
    public bool IsUser => Role == ChatRole.User;
    public bool IsAssistant => Role == ChatRole.Assistant;
    public bool IsAction => Role == ChatRole.Action;
    public bool IsError => Role == ChatRole.Error;
}

/// <summary>
/// Panel del asistente. La clave de API se guarda cifrada con DPAPI (o se toma de ANTHROPIC_API_KEY) y nunca
/// se muestra ni se registra. Las acciones sensibles se confirman con un diálogo de la propia aplicación.
/// </summary>
public sealed partial class AssistantViewModel : ObservableObject
{
    public const string ApiKeySecret = "anthropic-api-key";

    private readonly MainViewModel _main;
    private readonly ISecretStore _secrets;
    private readonly ClaudeAssistant _agent;
    private CancellationTokenSource? _cts;

    public AssistantViewModel(MainViewModel main, ISecretStore secrets)
    {
        _main = main;
        _secrets = secrets;
        var toolbox = new AssistantToolbox(main.Session, main, ConfirmAsync, main.Optimizer.Report);
        _agent = new ClaudeAssistant(toolbox, GetApiKeyAsync);
        _ = RefreshKeyStateAsync();
    }

    public ObservableCollection<ChatEntry> Messages { get; } = [];

    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _input = "";
    [ObservableProperty] private bool _hasApiKey;
    [ObservableProperty] private bool _hasStoredKey;
    [ObservableProperty] private string _apiKeyInput = "";
    [ObservableProperty] private string _keySource = "";

    public string ModelName => ClaudeAssistant.Model;

    /// <summary>Se añadió un mensaje (para desplazar el chat al final).</summary>
    public event EventHandler? MessageAdded;

    private async Task<string?> GetApiKeyAsync(CancellationToken ct)
    {
        var stored = await _secrets.GetAsync(ApiKeySecret, ct).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(stored) ? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") : stored;
    }

    private async Task RefreshKeyStateAsync()
    {
        var stored = await _secrets.GetAsync(ApiKeySecret, CancellationToken.None);
        var env = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            HasApiKey = !string.IsNullOrWhiteSpace(stored) || env;
            HasStoredKey = !string.IsNullOrWhiteSpace(stored);
            KeySource = !string.IsNullOrWhiteSpace(stored) ? "Clave guardada (cifrada para tu usuario de Windows)."
                : env ? "Usando la variable de entorno ANTHROPIC_API_KEY." : "";
        });
    }

    [RelayCommand]
    private void Toggle() => IsOpen = !IsOpen;

    [RelayCommand]
    private async Task SaveApiKey()
    {
        var key = ApiKeyInput.Trim();
        ApiKeyInput = "";
        if (key.Length < 20)
        {
            Add(ChatRole.Error, "Esa clave no parece válida. Las claves de Anthropic empiezan por «sk-ant-».");
            return;
        }
        await _secrets.SetAsync(ApiKeySecret, key, CancellationToken.None);
        await RefreshKeyStateAsync();
    }

    [RelayCommand]
    private async Task RemoveApiKey()
    {
        if (!await _main.Dialogs.ConfirmAsync("Quitar clave", "¿Borrar la clave de API guardada en este equipo?", "Borrar", destructive: true))
            return;
        await _secrets.DeleteAsync(ApiKeySecret, CancellationToken.None);
        await RefreshKeyStateAsync();
    }

    [RelayCommand]
    private void NewConversation()
    {
        if (IsBusy)
            return;
        _agent.Reset();
        Messages.Clear();
    }

    [RelayCommand]
    public void Cancel() => _cts?.Cancel();

    [RelayCommand]
    private async Task Send()
    {
        var text = Input.Trim();
        if (text.Length == 0 || IsBusy)
            return;
        Input = "";
        Add(ChatRole.User, text);
        IsBusy = true;
        _cts = new CancellationTokenSource();
        try
        {
            await _agent.SendAsync(text, OnEvent, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            Add(ChatRole.Error, "Cancelado. Los cambios ya hechos siguen en el estudio (puedes deshacerlos con Ctrl+Z).");
        }
        catch (AssistantUnavailableException ex)
        {
            Add(ChatRole.Error, ex.Message);
        }
        catch (Exception ex)
        {
            AppLog.Error("asistente", "Fallo inesperado", ex);
            Add(ChatRole.Error, $"Error inesperado: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    /// <summary>Atajo de las sugerencias del panel vacío.</summary>
    [RelayCommand]
    private Task Suggest(string text)
    {
        Input = text;
        return Send();
    }

    private void OnEvent(AssistantEvent e) => Dispatcher.UIThread.Post(() =>
    {
        switch (e)
        {
            case AssistantText t:
                Add(ChatRole.Assistant, t.Text);
                break;
            case ToolCallFinished f:
                Add(f.IsError ? ChatRole.Error : ChatRole.Action, f.IsError ? $"✗ {f.Summary}" : $"✓ {f.Summary}");
                break;
        }
    });

    private void Add(ChatRole role, string text)
    {
        Messages.Add(new ChatEntry(role, text));
        MessageAdded?.Invoke(this, EventArgs.Empty);
    }

    private Task<bool> ConfirmAsync(AssistantConfirmation c) =>
        Dispatcher.UIThread.InvokeAsync(() => _main.Dialogs.ConfirmAsync(c.Title, c.Message, c.ConfirmText,
            destructive: c.ConfirmText is "Eliminar" or "Quitar" or "Detener"));
}

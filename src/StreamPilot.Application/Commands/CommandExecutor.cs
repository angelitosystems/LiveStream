namespace StreamPilot.Application.Commands;

public sealed record CommandResult(bool Success, string? Error)
{
    public static CommandResult Ok { get; } = new(true, null);
    public static CommandResult Fail(string error) => new(false, error);
}

/// <summary>
/// Ejecuta comandos de uno en uno (nunca en paralelo) y mantiene el historial Undo/Redo.
/// Es el único punto de entrada para mutar el estudio: UI, asistente IA y optimizador pasan por aquí.
/// </summary>
public sealed class CommandExecutor(StudioContext context)
{
    public const int MaxHistory = 200;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly LinkedList<IReversibleStudioCommand> _undo = new();
    private readonly Stack<IReversibleStudioCommand> _redo = new();

    public StudioContext Context => context;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public string? UndoDescription => _undo.Last?.Value.Description;
    public string? RedoDescription => _redo.TryPeek(out var c) ? c.Description : null;

    /// <summary>Se ejecutó un comando nuevo con éxito.</summary>
    public event EventHandler<IStudioCommand>? CommandExecuted;

    /// <summary>El estado del proyecto cambió (ejecución, deshacer o rehacer). Útil para autoguardado y refresco de UI.</summary>
    public event EventHandler? StateChanged;

    public Task<CommandResult> ExecuteAsync(IStudioCommand command, CancellationToken ct = default) =>
        RunExclusiveAsync(async () =>
        {
            if (command.Validate(context) is { } error)
                return CommandResult.Fail(error);

            await command.ExecuteAsync(context, ct).ConfigureAwait(false);

            if (command is IReversibleStudioCommand reversible)
            {
                _undo.AddLast(reversible);
                if (_undo.Count > MaxHistory)
                    _undo.RemoveFirst();
                _redo.Clear();
            }

            CommandExecuted?.Invoke(this, command);
            return CommandResult.Ok;
        }, ct);

    public Task<CommandResult> UndoAsync(CancellationToken ct = default) =>
        RunExclusiveAsync(async () =>
        {
            if (_undo.Last is not { } node)
                return CommandResult.Fail("No hay nada que deshacer.");
            _undo.RemoveLast();
            await node.Value.UndoAsync(context, ct).ConfigureAwait(false);
            _redo.Push(node.Value);
            return CommandResult.Ok;
        }, ct);

    public Task<CommandResult> RedoAsync(CancellationToken ct = default) =>
        RunExclusiveAsync(async () =>
        {
            if (!_redo.TryPop(out var command))
                return CommandResult.Fail("No hay nada que rehacer.");
            await command.ExecuteAsync(context, ct).ConfigureAwait(false);
            _undo.AddLast(command);
            return CommandResult.Ok;
        }, ct);

    /// <summary>Vacía el historial (al abrir otro proyecto).</summary>
    public void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
    }

    private async Task<CommandResult> RunExclusiveAsync(Func<Task<CommandResult>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        CommandResult result;
        try
        {
            result = await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = CommandResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }

        if (result.Success)
            StateChanged?.Invoke(this, EventArgs.Empty);
        return result;
    }
}

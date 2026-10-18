namespace StreamPilot.Application.Commands;

public sealed record CommandResult(bool Success, string? Error)
{
    public static CommandResult Ok { get; } = new(true, null);
    public static CommandResult Fail(string error) => new(false, error);
}

/// <summary>
/// Ejecuta comandos de uno en uno (nunca en paralelo) y mantiene el historial Undo/Redo.
/// Es el único punto de entrada para mutar el estudio.
/// </summary>
public sealed class CommandExecutor(StudioContext context)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Stack<IReversibleStudioCommand> _undo = new();
    private readonly Stack<IReversibleStudioCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public event EventHandler<IStudioCommand>? CommandExecuted;

    public Task<CommandResult> ExecuteAsync(IStudioCommand command, CancellationToken ct = default) =>
        RunExclusiveAsync(async () =>
        {
            if (command.Validate(context) is { } error)
                return CommandResult.Fail(error);

            await command.ExecuteAsync(context, ct).ConfigureAwait(false);

            if (command is IReversibleStudioCommand reversible)
            {
                _undo.Push(reversible);
                _redo.Clear();
            }

            CommandExecuted?.Invoke(this, command);
            return CommandResult.Ok;
        }, ct);

    public Task<CommandResult> UndoAsync(CancellationToken ct = default) =>
        RunExclusiveAsync(async () =>
        {
            if (!_undo.TryPop(out var command))
                return CommandResult.Fail("No hay nada que deshacer.");
            await command.UndoAsync(context, ct).ConfigureAwait(false);
            _redo.Push(command);
            return CommandResult.Ok;
        }, ct);

    public Task<CommandResult> RedoAsync(CancellationToken ct = default) =>
        RunExclusiveAsync(async () =>
        {
            if (!_redo.TryPop(out var command))
                return CommandResult.Fail("No hay nada que rehacer.");
            await command.ExecuteAsync(context, ct).ConfigureAwait(false);
            _undo.Push(command);
            return CommandResult.Ok;
        }, ct);

    private async Task<CommandResult> RunExclusiveAsync(Func<Task<CommandResult>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return CommandResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }
}

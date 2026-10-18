using StreamPilot.Application.Audio;
using StreamPilot.Application.Commands;
using StreamPilot.Application.Scenes;
using StreamPilot.Application.Sources;
using StreamPilot.Domain.Entities;
using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.UnitTests;

public sealed class CommandExecutorTests
{
    private readonly FakeStreamingEngine _engine = new();
    private readonly StudioProject _project = new(Guid.NewGuid(), "Test");
    private readonly CommandExecutor _executor;

    public CommandExecutorTests() => _executor = new CommandExecutor(new StudioContext(_project, _engine));

    private async Task<Guid> CreateSceneAsync(string name)
    {
        var command = new CreateSceneCommand(name);
        Assert.True((await _executor.ExecuteAsync(command)).Success);
        return command.CreatedSceneId!.Value;
    }

    [Fact]
    public async Task CreateScene_AddsToDomainAndEngineWithSameId_AndFirstGoesToProgram()
    {
        var id = await CreateSceneAsync("Juego");

        Assert.Equal(id, Assert.Single(_project.Scenes).Id);
        Assert.Equal("Juego", _engine.Scenes[id]);
        Assert.Equal(id, _project.ActiveSceneId);
        Assert.Equal(id, _engine.ProgramScene);
    }

    [Fact]
    public async Task CreateScene_RejectsDuplicateNameWithoutTouchingEngine()
    {
        await CreateSceneAsync("Juego");

        var result = await _executor.ExecuteAsync(new CreateSceneCommand("juego"));

        Assert.False(result.Success);
        Assert.Single(_project.Scenes);
        Assert.Single(_engine.Scenes);
    }

    [Fact]
    public async Task Rename_CanBeUndoneAndRedone()
    {
        var id = await CreateSceneAsync("A");

        await _executor.ExecuteAsync(new RenameSceneCommand(id, "B"));
        Assert.Equal("B", _engine.Scenes[id]);

        Assert.True((await _executor.UndoAsync()).Success);
        Assert.Equal("A", _project.GetScene(id).Name);
        Assert.Equal("A", _engine.Scenes[id]);

        Assert.True((await _executor.RedoAsync()).Success);
        Assert.Equal("B", _project.GetScene(id).Name);
    }

    [Fact]
    public async Task RemoveScene_RejectsProgramScene_AndUndoRestoresItemsInEngine()
    {
        var program = await CreateSceneAsync("Programa");
        var other = await CreateSceneAsync("Otra");
        var add = new AddSourceCommand(other, "Cámara", SourceKind.VideoCaptureDevice);
        await _executor.ExecuteAsync(add);

        Assert.False((await _executor.ExecuteAsync(new RemoveSceneCommand(program))).Success);

        Assert.True((await _executor.ExecuteAsync(new RemoveSceneCommand(other))).Success);
        Assert.False(_engine.Scenes.ContainsKey(other));
        Assert.Empty(_engine.Items);

        Assert.True((await _executor.UndoAsync()).Success);
        Assert.Equal(1, _project.IndexOfScene(other));
        Assert.True(_engine.Items.ContainsKey(add.ItemId!.Value));
    }

    [Fact]
    public async Task DuplicateScene_SharesSourcesInsteadOfDuplicatingCaptures()
    {
        var scene = await CreateSceneAsync("Base");
        var add = new AddSourceCommand(scene, "Pantalla", SourceKind.DisplayCapture, transform: new Transform(10, 20, 0.5, 0.5, 0));
        await _executor.ExecuteAsync(add);

        var duplicate = new DuplicateSceneCommand(scene);
        Assert.True((await _executor.ExecuteAsync(duplicate)).Success);

        var copy = _project.GetScene(duplicate.CreatedSceneId!.Value);
        Assert.Equal("Base 2", copy.Name);
        Assert.Single(_project.Sources); // misma fuente
        Assert.Equal(add.SourceId, Assert.Single(copy.Items).SourceId);
        Assert.Equal(new Transform(10, 20, 0.5, 0.5, 0), _engine.Transforms[copy.Items[0].Id]);
        Assert.Equal(2, _engine.Items.Count);
    }

    [Fact]
    public async Task AddSource_RollsBackDomainWhenEngineRejects()
    {
        var scene = await CreateSceneAsync("S");

        var result = await _executor.ExecuteAsync(new AddSourceCommand(scene, "falla", SourceKind.Image));

        Assert.False(result.Success);
        Assert.Empty(_project.Sources);
        Assert.Empty(_project.GetScene(scene).Items);
        Assert.False(_executor.CanUndo && _executor.UndoDescription!.Contains("falla"));
    }

    [Fact]
    public async Task RemoveItem_RemovesOrphanSource_AndUndoRestoresBoth()
    {
        var scene = await CreateSceneAsync("S");
        var add = new AddSourceCommand(scene, "Logo", SourceKind.Image);
        await _executor.ExecuteAsync(add);

        await _executor.ExecuteAsync(new RemoveItemCommand(add.ItemId!.Value));
        Assert.Empty(_project.Sources);
        Assert.Empty(_engine.Sources);

        await _executor.UndoAsync();
        Assert.Single(_project.Sources);
        Assert.True(_engine.Sources.ContainsKey(add.SourceId!.Value));
        Assert.True(_engine.Items.ContainsKey(add.ItemId!.Value));
    }

    [Fact]
    public async Task RemoveItem_KeepsSourceUsedInAnotherScene()
    {
        var a = await CreateSceneAsync("A");
        var b = await CreateSceneAsync("B");
        var add = new AddSourceCommand(a, "Cámara", SourceKind.VideoCaptureDevice);
        await _executor.ExecuteAsync(add);
        await _executor.ExecuteAsync(new AddExistingSourceCommand(b, add.SourceId!.Value));

        await _executor.ExecuteAsync(new RemoveItemCommand(add.ItemId!.Value));

        Assert.Single(_project.Sources);
        Assert.Single(_engine.Items);
    }

    [Fact]
    public async Task TransformGesture_UndoReturnsToGestureStart()
    {
        var scene = await CreateSceneAsync("S");
        var add = new AddSourceCommand(scene, "Cam", SourceKind.VideoCaptureDevice);
        await _executor.ExecuteAsync(add);
        var item = add.ItemId!.Value;
        var start = _project.FindItem(item).Item.Transform;

        // El editor mueve el motor en vivo durante el arrastre y emite un único comando al soltar.
        var end = start.WithPosition(300, 200);
        await _executor.ExecuteAsync(new SetItemTransformCommand(item, end, previous: start));
        Assert.Equal(end, _engine.Transforms[item]);

        await _executor.UndoAsync();
        Assert.Equal(start, _project.FindItem(item).Item.Transform);
        Assert.Equal(start, _engine.Transforms[item]);
    }

    [Fact]
    public async Task Transform_RejectsZeroScaleAndNegativeCrop()
    {
        var scene = await CreateSceneAsync("S");
        var add = new AddSourceCommand(scene, "Cam", SourceKind.VideoCaptureDevice);
        await _executor.ExecuteAsync(add);

        Assert.False((await _executor.ExecuteAsync(new SetItemTransformCommand(add.ItemId!.Value, new Transform(0, 0, 0, 1, 0)))).Success);
        Assert.False((await _executor.ExecuteAsync(new SetItemTransformCommand(add.ItemId!.Value, Transform.Identity with { CropLeft = -1 }))).Success);
    }

    [Fact]
    public async Task VisibilityAndOrder_AreReversible()
    {
        var scene = await CreateSceneAsync("S");
        var first = new AddSourceCommand(scene, "Fondo", SourceKind.Image);
        var second = new AddSourceCommand(scene, "Texto", SourceKind.Text);
        await _executor.ExecuteAsync(first);
        await _executor.ExecuteAsync(second);

        await _executor.ExecuteAsync(new SetItemVisibleCommand(first.ItemId!.Value, false));
        Assert.False(_engine.Visible[first.ItemId!.Value]);
        await _executor.ExecuteAsync(new MoveItemCommand(second.ItemId!.Value, 0));
        Assert.Equal(second.ItemId, _project.GetScene(scene).Items[0].Id);

        await _executor.UndoAsync();
        await _executor.UndoAsync();
        Assert.Equal(first.ItemId, _project.GetScene(scene).Items[0].Id);
        Assert.True(_project.GetScene(scene).Items[0].Visible);
    }

    [Fact]
    public async Task Volume_IsAppliedButNotUndoable()
    {
        var scene = await CreateSceneAsync("S");
        var add = new AddSourceCommand(scene, "Mic", SourceKind.AudioInput);
        await _executor.ExecuteAsync(add);
        var undoBefore = _executor.UndoDescription;

        Assert.True((await _executor.ExecuteAsync(new SetVolumeCommand(add.SourceId!.Value, 0.5))).Success);

        Assert.Equal(0.5, _engine.Volumes[add.SourceId!.Value]);
        Assert.Equal(undoBefore, _executor.UndoDescription);
        Assert.False((await _executor.ExecuteAsync(new SetVolumeCommand(add.SourceId!.Value, 25))).Success);
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(-6, 0.501)]
    [InlineData(-100, 0)]
    public void DbConversion(double db, double expected) =>
        Assert.Equal(expected, SetVolumeCommand.DbToMultiplier(db), 3);

    [Fact]
    public async Task Execute_SerializesConcurrentCommands()
    {
        var tasks = Enumerable.Range(0, 50).Select(i => _executor.ExecuteAsync(new CreateSceneCommand($"S{i}")));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(50, _project.Scenes.Count);
        Assert.Equal(50, _engine.Scenes.Count);
    }

    [Fact]
    public async Task StateChanged_FiresOnExecuteUndoRedo_NotOnFailure()
    {
        var count = 0;
        _executor.StateChanged += (_, _) => count++;

        await CreateSceneAsync("A");
        await _executor.ExecuteAsync(new CreateSceneCommand("A")); // falla
        await _executor.ExecuteAsync(new RenameSceneCommand(_project.Scenes[0].Id, "B"));
        await _executor.UndoAsync();
        await _executor.RedoAsync();

        Assert.Equal(4, count);
    }

    [Fact]
    public async Task OfflineEditing_WorksWithoutEngine()
    {
        _engine.IsInitialized = false;

        var id = await CreateSceneAsync("Sin motor");
        await _executor.ExecuteAsync(new AddSourceCommand(id, "Texto", SourceKind.Text));

        Assert.Empty(_engine.Calls);
        Assert.Single(_project.GetScene(id).Items);
    }
}

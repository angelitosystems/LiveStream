using StreamPilot.Application.Commands;
using StreamPilot.Application.Scenes;
using StreamPilot.Domain.Entities;

namespace StreamPilot.UnitTests;

public sealed class CommandExecutorTests
{
    private readonly FakeStreamingEngine _engine = new();
    private readonly StudioProject _project = new(Guid.NewGuid(), "Test");
    private readonly CommandExecutor _executor;

    public CommandExecutorTests() => _executor = new CommandExecutor(new StudioContext(_project, _engine));

    [Fact]
    public async Task CreateScene_AddsToDomainAndEngineWithSameId()
    {
        var command = new CreateSceneCommand("Juego");

        var result = await _executor.ExecuteAsync(command);

        Assert.True(result.Success);
        var scene = Assert.Single(_project.Scenes);
        Assert.Equal(scene.Id, command.CreatedSceneId);
        Assert.Equal((scene.Id, "Juego"), Assert.Single(_engine.CreatedScenes));
        Assert.Equal(scene.Id, _project.ActiveSceneId);
    }

    [Fact]
    public async Task CreateScene_RejectsDuplicateNameWithoutTouchingEngine()
    {
        await _executor.ExecuteAsync(new CreateSceneCommand("Juego"));

        var result = await _executor.ExecuteAsync(new CreateSceneCommand("juego"));

        Assert.False(result.Success);
        Assert.Single(_project.Scenes);
        Assert.Single(_engine.CreatedScenes);
    }

    [Fact]
    public async Task Rename_CanBeUndoneAndRedone()
    {
        var create = new CreateSceneCommand("A");
        await _executor.ExecuteAsync(create);
        var id = create.CreatedSceneId!.Value;

        await _executor.ExecuteAsync(new RenameSceneCommand(id, "B"));
        Assert.Equal("B", _project.GetScene(id).Name);

        Assert.True((await _executor.UndoAsync()).Success);
        Assert.Equal("A", _project.GetScene(id).Name);

        Assert.True((await _executor.RedoAsync()).Success);
        Assert.Equal("B", _project.GetScene(id).Name);
    }

    [Fact]
    public async Task Execute_SerializesConcurrentCommands()
    {
        var tasks = Enumerable.Range(0, 50).Select(i => _executor.ExecuteAsync(new CreateSceneCommand($"S{i}")));

        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.Success));
        Assert.Equal(50, _project.Scenes.Count);
        Assert.Equal(50, _engine.CreatedScenes.Select(s => s.Id).Distinct().Count());
    }
}

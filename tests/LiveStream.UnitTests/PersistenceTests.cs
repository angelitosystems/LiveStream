using LiveStream.Application.Studio;
using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Infrastructure.Security;

namespace LiveStream.UnitTests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp-tests-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static StudioProject SampleProject()
    {
        var project = StudioSession.CreateDefaultProject("Prueba", new CanvasSettings(1080, 1920, 60));
        project.Transition = new TransitionSettings("swipe_transition", 750);
        project.Stream = new StreamSettings("rtmp://live.example.com/app", "clave-1");
        var scene = project.Scenes[0];
        var cam = new Source(Guid.NewGuid(), "Cámara", SourceKind.VideoCaptureDevice, """{"video_device_id":"cam:1","res":"1920x1080"}""")
        {
            Muted = true,
            Monitoring = AudioMonitoring.MonitorAndOutput,
        };
        cam.SetVolume(0.25);
        project.AddSource(cam);
        var item = scene.AddItem(cam.Id, new Transform(12.5, 40, 0.75, 0.75, 15, 10, 0, 20, 5));
        item.Locked = true;
        item.Visible = false;
        var second = project.AddScene("Pausa");
        project.SetPreviewScene(second.Id);
        return project;
    }

    [Fact]
    public void Serializer_RoundTripsEverything()
    {
        var original = SampleProject();

        var restored = ProjectSerializer.Deserialize(ProjectSerializer.Serialize(original));

        Assert.Equal(original.Id, restored.Id);
        Assert.Equal(original.Canvas, restored.Canvas);
        Assert.True(restored.Canvas.IsVertical);
        Assert.Equal(original.Transition, restored.Transition);
        Assert.Equal(original.Stream, restored.Stream);
        Assert.Equal(original.ActiveSceneId, restored.ActiveSceneId);
        Assert.Equal(original.PreviewSceneId, restored.PreviewSceneId);
        Assert.Equal(original.Scenes.Select(s => s.Name), restored.Scenes.Select(s => s.Name));

        var cam = restored.Sources.Single(s => s.Kind == SourceKind.VideoCaptureDevice);
        Assert.Equal(0.25, cam.Volume);
        Assert.True(cam.Muted);
        Assert.Equal(AudioMonitoring.MonitorAndOutput, cam.Monitoring);
        Assert.Contains("cam:1", cam.SettingsJson);
        Assert.Equal(3, restored.Sources.Single(s => s.Kind == SourceKind.AudioInput).GlobalAudioChannel);

        var item = restored.Scenes[0].Items.Single();
        Assert.Equal(new Transform(12.5, 40, 0.75, 0.75, 15, 10, 0, 20, 5), item.Transform);
        Assert.True(item.Locked);
        Assert.False(item.Visible);
    }

    [Fact]
    public void Serializer_NeverContainsStreamKey()
    {
        var json = ProjectSerializer.Serialize(SampleProject());
        Assert.Contains("clave-1", json); // solo el nombre de la credencial
        Assert.DoesNotContain("\"key\"", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Serializer_MigratesSchemaV1AndDropsDanglingItems()
    {
        var sceneId = Guid.NewGuid();
        var json = $$"""
            {"schemaVersion":1,"id":"{{Guid.NewGuid()}}","name":"Viejo","activeSceneId":"{{sceneId}}",
             "sources":[],"scenes":[{"id":"{{sceneId}}","name":"Escena","items":[
               {"id":"{{Guid.NewGuid()}}","sourceId":"{{Guid.NewGuid()}}","transform":{"x":0,"y":0,"scaleX":1,"scaleY":1,"rotationDegrees":0},"visible":true,"locked":false}]}]}
            """;

        var project = ProjectSerializer.Deserialize(json);

        Assert.Equal(new CanvasSettings(), project.Canvas);
        Assert.Empty(project.Scenes[0].Items);
        Assert.Equal(sceneId, project.ActiveSceneId);
    }

    [Fact]
    public void Serializer_RejectsNewerSchema() =>
        Assert.Throws<InvalidDataException>(() => ProjectSerializer.Deserialize($$"""{"schemaVersion":99,"id":"{{Guid.NewGuid()}}","name":"x"}"""));

    [Fact]
    public async Task Repository_SavesAtomicallyKeepsBackupAndRecoversFromCorruption()
    {
        var repo = new JsonProjectRepository(_dir);
        var project = SampleProject();
        var path = repo.DefaultPathFor(project);

        await repo.SaveAsync(project, path, CancellationToken.None);
        project.Rename("Segunda versión");
        await repo.SaveAsync(project, path, CancellationToken.None);

        Assert.True(File.Exists(path + ".bak"));
        Assert.False(File.Exists(path + ".tmp"));
        Assert.Equal("Segunda versión", (await repo.LoadAsync(path, CancellationToken.None))!.Name);

        await File.WriteAllTextAsync(path, "{ corrupto");
        var recovered = await repo.LoadAsync(path, CancellationToken.None);
        Assert.Equal("Prueba", recovered!.Name); // contenido del .bak
    }

    [Fact]
    public void Repository_RemembersLastOpenedOnlyIfItExists()
    {
        var repo = new JsonProjectRepository(_dir);
        var path = Path.Combine(_dir, "x.spproj");
        repo.LastOpenedPath = path;
        Assert.Null(repo.LastOpenedPath);
        File.WriteAllText(path, "{}");
        Assert.Equal(path, repo.LastOpenedPath);
    }

    [Fact]
    public async Task SecretStore_EncryptsAndRoundTrips()
    {
        var path = Path.Combine(_dir, "secrets.dat");
        var store = new DpapiSecretStore(path);

        await store.SetAsync("stream-key", "live_123_SECRETO", CancellationToken.None);

        Assert.Equal("live_123_SECRETO", await new DpapiSecretStore(path).GetAsync("stream-key", CancellationToken.None));
        Assert.DoesNotContain("SECRETO", System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(path)));
        await store.DeleteAsync("stream-key", CancellationToken.None);
        Assert.Null(await store.GetAsync("stream-key", CancellationToken.None));
    }

    [Fact]
    public async Task Session_ValidatesStreamAndPassesKeyOnlyToEngine()
    {
        var engine = new FakeStreamingEngine();
        var secrets = new DpapiSecretStore(Path.Combine(_dir, "s.dat"));
        var project = SampleProject();
        await using var session = new StudioSession(engine, new JsonProjectRepository(_dir), secrets, project,
            Path.Combine(_dir, "p.spproj"), TimeSpan.FromMilliseconds(10));

        Assert.Contains("destinos", await session.ValidateStreamAsync());
        var destination = StreamDestination.Create("YouTube", StreamPlatform.YouTube, "rtmp://a.rtmp.youtube.com/live2");
        project.InsertDestination(destination);
        Assert.Contains("clave", await session.ValidateStreamAsync());
        await session.SetDestinationKeyAsync(destination.Id, "abc");
        Assert.Null(await session.ValidateStreamAsync());

        await session.StartStreamAsync();
        var target = engine.DestinationTargets[destination.Id];
        Assert.Equal("abc", target.Key);
        Assert.DoesNotContain("abc", target.ToString());
        Assert.DoesNotContain("abc", ProjectSerializer.Serialize(project));
    }

    [Fact]
    public async Task Session_AutosavesAfterChanges()
    {
        var path = Path.Combine(_dir, "auto.spproj");
        var saved = new TaskCompletionSource();
        var session = new StudioSession(new FakeStreamingEngine(), new JsonProjectRepository(_dir), new DpapiSecretStore(Path.Combine(_dir, "s.dat")),
            StudioSession.CreateDefaultProject(), path, TimeSpan.FromMilliseconds(20));
        session.Saved += (_, _) => saved.TrySetResult();

        await session.Executor.ExecuteAsync(new LiveStream.Application.Scenes.CreateSceneCommand("Nueva"));

        await saved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("Nueva", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData(1920, 1080, 1920, 1080, 0, 0, 1)]
    [InlineData(1280, 720, 1920, 1080, 0, 0, 1.5)]
    [InlineData(1920, 1080, 1080, 1920, 0, 656.25, 0.5625)]
    public void FitToCanvas(int sw, int sh, int cw, int ch, double x, double y, double scale)
    {
        var t = StudioSession.FitToCanvas(sw, sh, new CanvasSettings(cw, ch));
        Assert.Equal(x, t.X, 3);
        Assert.Equal(y, t.Y, 3);
        Assert.Equal(scale, t.ScaleX, 4);
    }
}

public sealed class LegacyDataMigrationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ls-mig-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void MovesTheOldDataFolder_AndFixesTheLastOpenedProject()
    {
        var legacy = Path.Combine(_root, "StreamPilot");
        Directory.CreateDirectory(Path.Combine(legacy, "projects"));
        File.WriteAllText(Path.Combine(legacy, "projects", "a.spproj"), "{}");
        File.WriteAllText(Path.Combine(legacy, "last-project.txt"), Path.Combine(legacy, "projects", "a.spproj"));

        Assert.NotNull(LegacyDataMigration.Run(_root));
        var current = Path.Combine(_root, "LiveStream");
        Assert.False(Directory.Exists(legacy));
        Assert.True(File.Exists(Path.Combine(current, "projects", "a.spproj")));
        Assert.Equal(Path.Combine(current, "projects", "a.spproj"), File.ReadAllText(Path.Combine(current, "last-project.txt")));

        // Segunda vez (o si ya existe la carpeta nueva): no hace nada.
        Assert.Null(LegacyDataMigration.Run(_root));
    }
}

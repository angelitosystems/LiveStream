using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LiveStream.Application.DualStreaming;
using LiveStream.Application.Studio;
using LiveStream.Domain.Compositions;
using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;
using LiveStream.Infrastructure.Persistence;
using LiveStream.Infrastructure.Security;
using Xunit;

namespace LiveStream.UnitTests;

public sealed class DualCompositionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("sp-dual-tests-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private StudioSession CreateTestSession()
    {
        var project = StudioSession.CreateDefaultProject("DualTest", new CanvasSettings(1920, 1080, 60));
        var fakeEngine = new FakeStreamingEngine();
        var repo = new JsonProjectRepository(_dir);
        var secrets = new DpapiSecretStore(Path.Combine(_dir, "s.dat"));
        var projectFile = Path.Combine(_dir, "test.lsproj");
        return new StudioSession(fakeEngine, repo, secrets, project, projectFile);
    }

    [Fact]
    public void AdaptTransformToVertical_AdjustsCameraFraming()
    {
        var vCanvas = new CanvasSettings(1080, 1920, 60);
        var originalTransform = new Transform(100, 100, 0.5, 0.5, 0);

        var adaptedCamera = DualStreamCoordinator.AdaptTransformToVertical(
            originalTransform,
            SourceKind.VideoCaptureDevice,
            vCanvas
        );

        Assert.Equal(0, adaptedCamera.X);
        Assert.True(adaptedCamera.Y >= 0);
        Assert.Equal(1.0, adaptedCamera.ScaleX);
        Assert.Equal(1.0, adaptedCamera.ScaleY);
    }

    [Fact]
    public void DualCompositionSettings_CanvasFor_ReturnsExpectedDimensions()
    {
        var settings = new DualCompositionSettings();

        var horizontal = settings.CanvasFor(CompositionFormat.Horizontal);
        Assert.Equal(1920, horizontal.Width);
        Assert.Equal(1080, horizontal.Height);
        Assert.False(horizontal.IsVertical);

        var vertical = settings.CanvasFor(CompositionFormat.Vertical);
        Assert.Equal(1080, vertical.Width);
        Assert.Equal(1920, vertical.Height);
        Assert.True(vertical.IsVertical);
    }

    [Fact]
    public async Task DualStreamCoordinator_FiltersDestinationsByTargetFormat()
    {
        await using var session = CreateTestSession();
        var coordinator = new DualStreamCoordinator(session);

        var d1 = StreamDestination.Create("YouTube 16:9", StreamPlatform.YouTube, "rtmp://a.rtmp.youtube.com/live2") with
        {
            TargetFormat = CompositionFormat.Horizontal
        };
        var d2 = StreamDestination.Create("TikTok 9:16", StreamPlatform.TikTok, "rtmp://live.tiktok.com/app") with
        {
            TargetFormat = CompositionFormat.Vertical
        };
        var d3 = StreamDestination.Create("Restream Dual", StreamPlatform.Custom, "rtmp://live.restream.io/live") with
        {
            TargetFormat = CompositionFormat.Both
        };

        session.Project.InsertDestination(d1);
        session.Project.InsertDestination(d2);
        session.Project.InsertDestination(d3);

        var horizontalTargets = coordinator.GetDestinationsFor(CompositionFormat.Horizontal).ToList();
        Assert.Contains(d1, horizontalTargets);
        Assert.Contains(d3, horizontalTargets);
        Assert.DoesNotContain(d2, horizontalTargets);

        var verticalTargets = coordinator.GetDestinationsFor(CompositionFormat.Vertical).ToList();
        Assert.Contains(d2, verticalTargets);
        Assert.Contains(d3, verticalTargets);
        Assert.DoesNotContain(d1, verticalTargets);
    }

    [Fact]
    public async Task DualStreamCoordinator_SwitchFormat_SwapsCanvasAndScene()
    {
        await using var session = CreateTestSession();
        var coordinator = new DualStreamCoordinator(session);

        var originalScene = session.Project.Scenes.First(s => s.Id == session.Project.ActiveSceneId);
        var cam = new Source(Guid.NewGuid(), "Cam 1", SourceKind.VideoCaptureDevice);
        session.Project.AddSource(cam);
        originalScene.AddItem(cam.Id, new Transform(0, 0, 1, 1, 0));

        var verticalScene = coordinator.CreateVerticalCompositionFrom(originalScene, "Vertical 9:16");
        Assert.NotNull(verticalScene);
        Assert.Single(verticalScene.Items);

        // Switch to vertical
        await coordinator.SwitchFormatAsync(CompositionFormat.Vertical);
        Assert.Equal(CompositionFormat.Vertical, coordinator.ActiveFormat);
        Assert.Equal(verticalScene.Id, session.Project.ActiveSceneId);
        Assert.True(session.Project.Canvas.IsVertical);

        // Switch back to horizontal
        await coordinator.SwitchFormatAsync(CompositionFormat.Horizontal);
        Assert.Equal(CompositionFormat.Horizontal, coordinator.ActiveFormat);
        Assert.False(session.Project.Canvas.IsVertical);
    }
}

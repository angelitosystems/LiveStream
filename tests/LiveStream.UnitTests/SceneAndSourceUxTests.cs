using LiveStream.Application.Abstractions;
using LiveStream.Application.Commands;
using LiveStream.Application.Editing;
using LiveStream.Application.Graphics;
using LiveStream.Application.Scenes;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.Entities;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.UnitTests;

public sealed class DropPlacementTests
{
    private static readonly CanvasSettings Canvas = new(); // 1920x1080
    [Fact]
    public void Natural_OversizedImageShrinksToCanvas_AtTopLeft()
    {
        var t = DropPlacement.Place(960, 540, 3840, 2160, Canvas);
        Assert.Equal(0.5, t.ScaleX);
        Assert.Equal(0.5, t.ScaleY);
        Assert.Equal(0, t.X);
        Assert.Equal(0, t.Y);
    }

    [Fact]
    public void Natural_SmallImageCentersOnDropPoint()
    {
        var t = DropPlacement.Place(500, 400, 640, 480, Canvas);
        Assert.Equal(1, t.ScaleX);
        Assert.Equal(180, t.X);
        Assert.Equal(160, t.Y);
    }

    [Fact]
    public void Natural_DropNearEdgeClampsInsideCanvas()
    {
        var t = DropPlacement.Place(100, 50, 640, 480, Canvas);
        Assert.Equal(0, t.X);
        Assert.Equal(0, t.Y);
    }

    [Fact]
    public void Screen_FitsInsideEvenIfSmallerInOrigin()
    {
        var t = DropPlacement.Place(9_999, 9_999, 960, 540, Canvas, DropFit.Screen);
        Assert.Equal(2, t.ScaleX);
        Assert.Equal(0, t.X);
        Assert.Equal(0, t.Y);
    }

    [Fact]
    public void Fill_CoversTheWholeCanvas_ScalingNonUniformCase()
    {
        // 960x1080 en un lienzo 1920x1080: encaja por altura (2x) y cubre el ancho a medias → el mayor manda.
        var t = DropPlacement.Place(100, 100, 960, 1080, Canvas, DropFit.Fill);
        Assert.Equal(2, t.ScaleX);
        Assert.Equal(0, t.Y);
        // El centro del contenido cae en el punto solicitado, clamped al ancho disponible tras escalar.
        Assert.Equal(0, t.X); // 960*2=1920 ancho igual al lienzo: clamp(100-960)=0
    }

    [Fact]
    public void UnknownSize_LeavesIdentity()
    {
        Assert.Equal(Transform.Identity, DropPlacement.Place(400, 300, 0, 0, Canvas));
        Assert.Equal(Transform.Identity, DropPlacement.Place(400, 300, -1, 100, Canvas));
    }

    [Fact]
    public void VerticalCanvas_KeepsProportion()
    {
        var canvas = new CanvasSettings(1080, 1920);
        var t = DropPlacement.Place(540, 960, 3840, 2160, canvas, DropFit.Screen);
        Assert.Equal(0.28125, t.ScaleX, 6);
        Assert.Equal(0, t.X, 6);
        Assert.Equal(656.2, t.Y, 6); // Math.Round(656.25, 1) redondeo bancario → 656.2
    }
}

public sealed class SceneTemplatesTests
{
    private readonly FakeStreamingEngine _engine = new();
    private readonly StudioProject _project = new(Guid.NewGuid(), "Test");
    private readonly CommandExecutor _executor;

    public SceneTemplatesTests() => _executor = new CommandExecutor(new StudioContext(_project, _engine));

    [Fact]
    public async Task CreateAsync_Camera_FitsSourceToCanvasAccordingToEngineSize()
    {
        var (sceneId, error) = await SceneTemplates.CreateAsync(_executor.Context, _executor, "Cámara", SceneTemplateKind.Camera);

        Assert.Null(error);
        Assert.NotNull(sceneId);
        var scene = _project.GetScene(sceneId!.Value);
        Assert.Single(scene.Items);
        var source = _project.GetSource(scene.Items[0].SourceId);
        Assert.Equal(SourceKind.VideoCaptureDevice, source.Kind);
        // El motor falso reporta 1280x720: en un lienzo 1920x1080 escala 1.5 exacto.
        var t = scene.Items[0].Transform;
        Assert.Equal(1.5, t.ScaleX, 6);
        Assert.Equal(1.5, t.ScaleY, 6);
        Assert.Equal(0, t.X, 6);
        Assert.Equal(0, t.Y, 6);
    }

    [Fact]
    public async Task CreateAsync_Empty_LeavesSceneWithoutItems()
    {
        var (sceneId, error) = await SceneTemplates.CreateAsync(_executor.Context, _executor, "Vacía", SceneTemplateKind.Empty);
        Assert.Null(error);
        Assert.Empty(_project.GetScene(sceneId!.Value).Items);
    }

    [Fact]
    public async Task CreateAsync_Interview_AddsHiddenMicrophone()
    {
        var (sceneId, error) = await SceneTemplates.CreateAsync(_executor.Context, _executor, "Entrevista", SceneTemplateKind.Interview);
        Assert.Null(error);
        var scene = _project.GetScene(sceneId!.Value);
        var mic = scene.Items.Single(i => _project.GetSource(i.SourceId).Kind == SourceKind.AudioInput);
        Assert.False(mic.Visible);
        var camera = scene.Items.Single(i => _project.GetSource(i.SourceId).Kind == SourceKind.VideoCaptureDevice);
        Assert.True(camera.Visible);
    }

    [Fact]
    public async Task CreateAsync_Overlays_AddsGraphFromBrandKit()
    {
        // Arranque del motor como en producción: sin la capa de gráficos, AddGraphicCommand no encuentra la escena overlay.
        await _engine.InitializeAsync(new EngineSettings(), CancellationToken.None);
        await GraphicLayer.CreateOverlayAsync(_engine, _project, CancellationToken.None);
        await _executor.ExecuteAsync(new CreateSceneCommand("Base")); // el gráfico de la capa va a la escena de programa
        var before = _project.Graphics.Count;
        var (sceneId, error) = await SceneTemplates.CreateAsync(_executor.Context, _executor, "Rótulo", SceneTemplateKind.Overlays);
        Assert.True(error is null, error ?? "sin error");
        Assert.Equal(before + 1, _project.Graphics.Count);
        Assert.Single(_project.GetScene(sceneId!.Value).Items); // solo la cámara: el gráfico va al programa, no a la escena
    }

    [Fact]
    public async Task CreateAsync_RejectsBlankName()
    {
        var (sceneId, error) = await SceneTemplates.CreateAsync(_executor.Context, _executor, "   ", SceneTemplateKind.Empty);
        Assert.Null(sceneId);
        Assert.NotNull(error);
        Assert.Empty(_project.Scenes);
    }

    [Fact]
    public async Task TemplateOptions_CoverAllKinds_Once()
    {
        Assert.Equal(Enum.GetValues<SceneTemplateKind>().Length, SceneTemplates.Options.Count);
        Assert.Equal(Enum.GetValues<SceneTemplateKind>().OrderBy(k => k), SceneTemplates.Options.Select(o => o.Kind).OrderBy(k => k));
        Assert.All(SceneTemplates.Options, o => Assert.False(string.IsNullOrWhiteSpace(o.Name) || string.IsNullOrWhiteSpace(o.Hint)));
    }
}

public sealed class SourceCatalogTests
{
    [Fact]
    public void Entries_HaveUniqueKinds_NamesAndDescriptions()
    {
        Assert.Equal(SourceCatalog.Entries.Select(e => e.Kind), SourceCatalog.Entries.Select(e => e.Kind).Distinct());
        Assert.All(SourceCatalog.Entries, e =>
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Label) || string.IsNullOrWhiteSpace(e.Description) || string.IsNullOrWhiteSpace(e.DefaultName));
            Assert.Contains(e.Category, new List<string> { "Captura", "Audio", "Contenido visual" });
        });
    }

    [Fact]
    public void Grouping_Assessment_CategoryMatchesEntry()
    {
        Assert.Equal("Captura", SourceCatalog.Category(SourceKind.GameCapture));
        Assert.Equal("Audio", SourceCatalog.Category(SourceKind.AudioInput));
        Assert.Equal("Contenido visual", SourceCatalog.Category(SourceKind.Text));
        Assert.Equal("Otras", SourceCatalog.Category(SourceKind.Browser));
        Assert.Equal("Imagen", SourceCatalog.Label(SourceKind.Image));
    }

    [Fact]
    public void IconKey_GivesAnIconToEveryCatalogKind()
    {
        Assert.All(SourceCatalog.Entries, e => Assert.StartsWith("Icon.", SourceCatalog.IconKey(e.Kind)));
        Assert.Equal("Icon.Layers", SourceCatalog.IconKey(SourceKind.Browser));
    }
}

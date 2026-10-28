using System.IO.Compression;
using System.Text.Json;
using LiveStream.Application.Commands;
using LiveStream.Application.Graphics;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Infrastructure.Persistence;

namespace LiveStream.UnitTests;

/// <summary>Estudio gráfico: diseño libre en el motor, edición incremental en vivo, operaciones del editor y recursos.</summary>
public sealed class GraphicDesignTests : IDisposable
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];
    private readonly FakeStreamingEngine _engine = new();
    private readonly StudioProject _project = new(Guid.NewGuid(), "Diseño") { Canvas = new CanvasSettings(1920, 1080, 30) };
    private readonly CommandExecutor _executor;
    private readonly string _dir = Directory.CreateTempSubdirectory("ls-design-").FullName;

    public GraphicDesignTests()
    {
        _executor = new CommandExecutor(new StudioContext(_project, _engine));
        _ = GraphicLayer.CreateOverlayAsync(_engine, _project, CancellationToken.None);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private async Task Ok(IStudioCommand command)
    {
        var result = await _executor.ExecuteAsync(command);
        Assert.True(result.Success, result.Error);
    }

    private string Image(string name = "logo.png")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, Png);
        return path;
    }

    private Graphic Design(params DesignElement[] elements) =>
        GraphicTemplates.Create(GraphicKind.Custom, "Diseño", _project.Canvas) with { Design = elements, X = 100, Y = 200, Fields = new Dictionary<string, string> { ["nombre"] = "Ana" } };

    private string Settings(Guid graphicId, string key) => _engine.Sources[GraphicLayer.ElementSourceId(graphicId, key)].SettingsJson;

    [Fact]
    public async Task CustomDesign_BecomesNativePieces_WithTextEffects_Opacity_Shadow_AndFields()
    {
        var text = new DesignElement("t", "Nombre", DesignElementKind.Text, 10, 20, 500, 80, Rotation: 15, Color: "#FFFFFFFF",
            Text: "Hola {nombre}", FontSize: 40, Bold: true, Italic: true, GradientColor: "#FFFF0000", OutlineColor: "#FF000000",
            OutlineSize: 3, ShadowColor: "#80000000");
        var rect = new DesignElement("r", "Panel", DesignElementKind.Rect, 0, 0, 600, 120, Color: "#FF102030", Opacity: 0.5);
        var logo = new DesignElement("l", "Logo", DesignElementKind.Image, 620, 0, 120, 120, Opacity: 0.25, Path: Image());
        var hidden = new DesignElement("h", "Oculto", DesignElementKind.Rect, 0, 0, 10, 10, Visible: false);
        var g = Design(rect, text, logo, hidden);
        await Ok(new AddGraphicCommand(g));

        var keys = GraphicTemplates.Layout(g, _project.Canvas).Elements.Select(e => e.Key).ToList();
        Assert.Equal(["r", "t-shadow", "t", "l"], keys);

        var t = JsonDocument.Parse(Settings(g.Id, "t")).RootElement;
        Assert.Equal("Hola Ana", t.GetProperty("text").GetString());
        Assert.Equal(3, t.GetProperty("font").GetProperty("flags").GetInt32());
        Assert.True(t.GetProperty("gradient").GetBoolean());
        Assert.Equal(3, t.GetProperty("outline_size").GetInt32());
        Assert.True(t.GetProperty("extents_wrap").GetBoolean());
        Assert.Equal(15, _engine.Transforms[GraphicLayer.ElementItemId(g.Id, "t")].RotationDegrees);
        Assert.Equal(110, _engine.Transforms[GraphicLayer.ElementItemId(g.Id, "t")].X);
        Assert.False(JsonDocument.Parse(Settings(g.Id, "t-shadow")).RootElement.TryGetProperty("gradient", out _));

        // Opacidad: en el alfa del color de la forma y con un filtro en la imagen.
        var color = JsonDocument.Parse(Settings(g.Id, "r")).RootElement.GetProperty("color").GetInt64();
        Assert.InRange(color >> 24, 127, 128);
        var filter = _engine.Filters[GraphicLayer.OpacityFilterId(g.Id, "l")];
        Assert.Equal("color_filter_v2", filter.TypeId);
        Assert.Contains("0.25", filter.SettingsJson);
        Assert.False(_engine.Sources.ContainsKey(GraphicLayer.ElementSourceId(g.Id, "h")));
    }

    [Fact]
    public async Task LiveEdit_CreatesAndRemovesOnlyTheChangedPieces_AndUndoRestores()
    {
        var a = new DesignElement("a", "A", DesignElementKind.Rect, 0, 0, 100, 100);
        var b = new DesignElement("b", "B", DesignElementKind.Text, 0, 0, 100, 100, Text: "B");
        var g = Design(a, b);
        await Ok(new AddGraphicCommand(g));
        _engine.Calls.Clear();

        var c = new DesignElement("c", "C", DesignElementKind.Video, 0, 0, 320, 180, Path: Path.Combine(_dir, "clip.mp4"));
        var edited = g with { Design = [c, b with { Text = "B2" }] };
        await Ok(new UpdateGraphicCommand(edited));

        Assert.Equal(1, _engine.Calls.Count(x => x.StartsWith("source+", StringComparison.Ordinal)));
        Assert.Equal(1, _engine.Calls.Count(x => x == "source-"));
        Assert.Equal(SourceKind.Media, _engine.Sources[GraphicLayer.ElementSourceId(g.Id, "c")].Kind);
        Assert.Contains("mute", _engine.Calls);
        Assert.Contains("\"B2\"", Settings(g.Id, "b"));
        Assert.False(_engine.Sources.ContainsKey(GraphicLayer.ElementSourceId(g.Id, "a")));
        Assert.Contains("order", _engine.Calls);

        await _executor.UndoAsync();
        Assert.True(_engine.Sources.ContainsKey(GraphicLayer.ElementSourceId(g.Id, "a")));
        Assert.False(_engine.Sources.ContainsKey(GraphicLayer.ElementSourceId(g.Id, "c")));
        Assert.Contains("\"B\"", Settings(g.Id, "b"));
    }

    [Fact]
    public async Task CountdownText_InADesign_IsDrivenByTheStudio()
    {
        var timer = new DesignElement("cd", "Cuenta", DesignElementKind.Text, 0, 0, 300, 100, Binding: DesignBinding.Countdown);
        var g = Design(timer) with { Fields = new Dictionary<string, string> { ["duration"] = "1", ["finished"] = "¡YA!" } };
        Assert.True(GraphicTemplates.HasCountdown(g));
        await Ok(new AddGraphicCommand(g));
        await using var service = new GraphicsService(_executor);

        string Shown() => JsonDocument.Parse(Settings(g.Id, "cd")).RootElement.GetProperty("text").GetString()!;
        service.StartCountdown(g.Id);
        for (var i = 0; i < 30 && Shown() != "¡YA!"; i++)
            await Task.Delay(100);
        Assert.Equal("¡YA!", Shown());
        Assert.NotNull(GraphicTemplates.ValidateFields(g with { Fields = new Dictionary<string, string> { ["duration"] = "nunca" } }));
    }

    [Fact]
    public void Templates_ConvertToEditableDesigns_KeepingPositionsAndFields()
    {
        var lower = GraphicTemplates.Create(GraphicKind.LowerThird, "Rótulo", _project.Canvas);
        var design = DesignOps.FromTemplate(lower, _project.Canvas, "Rótulo (diseño)");
        Assert.Equal(GraphicKind.Custom, design.Kind);
        Assert.Null(design.Validate());
        Assert.Equal("{name}", design.Design!.Single(e => e.Key == "name").Text);
        Assert.DoesNotContain(design.Design!, e => e.Key == "logo");

        // Las piezas quedan exactamente donde estaban (salvo el logo vacío, que se quita).
        var before = GraphicTemplates.Layout(lower, _project.Canvas).Elements.Where(e => e.Key != "logo").ToDictionary(e => e.Key);
        foreach (var piece in GraphicTemplates.Layout(design, _project.Canvas).Elements)
        {
            Assert.Equal(before[piece.Key].X, piece.X, 0);
            Assert.Equal(before[piece.Key].Y, piece.Y, 0);
            if (piece.Kind == GraphicElementKind.Text)
                Assert.Equal(lower.Field(piece.Key), piece.Text);
        }

        var clock = DesignOps.FromTemplate(GraphicTemplates.Create(GraphicKind.Countdown, "C", _project.Canvas), _project.Canvas, "C2");
        Assert.True(GraphicTemplates.HasCountdown(clock));
        Assert.Equal(new[] { "duration", "endsAt", "finished", "label" }, GraphicTemplates.FieldsOf(clock).Select(f => f.Key).Order());
    }

    // ---- Operaciones del editor ---------------------------------------------------------------

    private static DesignElement Box(string key, double x, double y, double w = 100, double h = 50) =>
        new(key, key, DesignElementKind.Rect, x, y, w, h);

    [Fact]
    public void HitTest_RespectsRotationAndStacking()
    {
        var below = Box("below", 0, 0, 200, 200);
        var rotated = Box("rot", 100, 100, 100, 20) with { Rotation = 90 };
        IReadOnlyList<DesignElement> design = [below, rotated];
        Assert.Equal("rot", DesignOps.HitTest(design, 90, 150)?.Key);   // girado 90°: ocupa x 80..100, y 100..200
        Assert.Equal("below", DesignOps.HitTest(design, 150, 105)?.Key); // sin girar estaría ahí; girado, no
        Assert.Null(DesignOps.HitTest(design, 500, 500));
        var b = DesignOps.Bounds(rotated);
        Assert.Equal((80.0, 100.0, 20.0, 100.0), (Math.Round(b.X), Math.Round(b.Y), Math.Round(b.Width), Math.Round(b.Height)));
    }

    [Fact]
    public void Align_Distribute_AndLockedElementsStayPut()
    {
        IReadOnlyList<DesignElement> design = [Box("a", 0, 0), Box("b", 50, 300), Box("c", 400, 10) with { Locked = true }];
        var left = DesignOps.Align(design, ["a", "b", "c"], DesignAlign.Left, default);
        Assert.Equal(0, left.Single(e => e.Key == "b").X);
        Assert.Equal(400, left.Single(e => e.Key == "c").X);

        var centered = DesignOps.Align(design, ["a"], DesignAlign.CenterH, new DesignRect(0, 0, 1920, 1080));
        Assert.Equal(910, centered.Single(e => e.Key == "a").X);

        IReadOnlyList<DesignElement> row = [Box("x", 0, 0), Box("y", 130, 0), Box("z", 600, 0)];
        var spread = DesignOps.Distribute(row, ["x", "y", "z"], horizontal: true);
        Assert.Equal(300, spread.Single(e => e.Key == "y").X); // huecos iguales de 200 px
        Assert.Equal(600, spread.Single(e => e.Key == "z").X);
    }

    [Fact]
    public void Snap_PrefersGuides_ThenGrid()
    {
        IReadOnlyList<DesignElement> design = [Box("m", 0, 0), Box("o", 300, 400)];
        var canvas = new DesignRect(0, 0, 1920, 1080);
        var (dx, _, guides) = DesignOps.Snap(design, ["m"], 296, 7, canvas, threshold: 6, grid: 0);
        Assert.Equal(300, dx);
        Assert.Contains(guides, g => g.Vertical && g.Position == 300);

        var (gx, gy, none) = DesignOps.Snap(design, ["m"], 33, 47, canvas, threshold: 2, grid: 20);
        Assert.Equal((40, 40), (gx, gy));
        Assert.Empty(none);
    }

    [Fact]
    public void Layers_Reorder_ToEdge_AndDuplicate()
    {
        IReadOnlyList<DesignElement> design = [Box("a", 0, 0), Box("b", 0, 0), Box("c", 0, 0)];
        Assert.Equal(["b", "a", "c"], DesignOps.Reorder(design, ["a"], +1).Select(e => e.Key));
        Assert.Equal(["b", "c", "a"], DesignOps.ToEdge(design, ["a"], front: true).Select(e => e.Key));
        Assert.Equal(["c", "a", "b"], DesignOps.ToEdge(design, ["c"], front: false).Select(e => e.Key));
        var (copy, keys) = DesignOps.Duplicate(design, ["b"]);
        var dup = copy.Single(e => e.Key == keys.Single());
        Assert.Equal((20.0, 20.0, "b copia"), (dup.X, dup.Y, dup.Name));
        Assert.Equal(dup, copy[^1]);
    }

    // ---- Persistencia, biblioteca e intercambio ------------------------------------------------

    [Fact]
    public void Project_RoundTripsDesigns_AndOldSchemasStillOpen()
    {
        var g = Design(Box("a", 1, 2), new DesignElement("t", "T", DesignElementKind.Text, 0, 0, 100, 40, Text: "{nombre}", Binding: DesignBinding.None));
        _project.InsertGraphic(g);
        var json = ProjectSerializer.Serialize(_project);
        var loaded = ProjectSerializer.Deserialize(json).GetGraphic(g.Id);
        Assert.Equal(g.Design, loaded.Design);
        Assert.Contains("\"schemaVersion\": 6", json);

        var v4 = json.Replace("\"schemaVersion\": 6", "\"schemaVersion\": 4");
        Assert.Equal(g.Design, ProjectSerializer.Deserialize(v4).GetGraphic(g.Id).Design);
    }

    [Fact]
    public void ResourceLibrary_ValidatesContent_AndFindsMissingFiles()
    {
        var library = new ResourceLibrary(Path.Combine(_dir, "lib"));
        var item = library.Import(Image("marca.png"), "Logos");
        Assert.Equal(("Logos", ResourceKind.Image), (item.Category, item.Kind));
        Assert.Equal("marca (2).png", library.Import(Image("marca.png"), "Logos").Name);

        var fake = Path.Combine(_dir, "falso.png");
        File.WriteAllText(fake, "no soy una imagen");
        Assert.Throws<InvalidDataException>(() => library.Import(fake));
        File.WriteAllText(Path.Combine(_dir, "a.svg"), "<svg/>");
        Assert.Contains("PNG", Assert.Throws<InvalidDataException>(() => library.Import(Path.Combine(_dir, "a.svg"))).Message);
        Assert.Throws<InvalidDataException>(() => library.Import(Image(), "../fuera"));

        var g = Design(new DesignElement("i", "Foto", DesignElementKind.Image, 0, 0, 10, 10, Path: Path.Combine(_dir, "no-existe.png")));
        Assert.Equal("Foto", Assert.Single(ResourceLibrary.Missing([g])).ElementName);
    }

    [Fact]
    public void Lsgfx_CarriesItsResources_AndRejectsPathsOutsideTheArchive()
    {
        var library = new ResourceLibrary(Path.Combine(_dir, "lib"));
        var logo = Image("escudo.png");
        var g = Design(new DesignElement("i", "Escudo", DesignElementKind.Image, 0, 0, 100, 100, Path: logo));
        var file = Path.Combine(_dir, "g.lsgfx");
        GraphicPackage.Export(g, file);
        File.Delete(logo);

        var imported = GraphicPackage.Import(file, library);
        Assert.NotEqual(g.Id, imported.Id);
        var path = imported.Design!.Single().Path!;
        Assert.StartsWith(Path.Combine(_dir, "lib", "Importados"), path);
        Assert.Equal(Png, File.ReadAllBytes(path));

        var evil = Path.Combine(_dir, "evil.lsgfx");
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            zip.CreateEntry("graphic.json");
            zip.CreateEntry("../../fuera.png");
        }
        Assert.Throws<InvalidDataException>(() => GraphicPackage.Import(evil, library));
    }

    [Fact]
    public void Validation_RejectsBrokenDesigns()
    {
        Assert.NotNull(Design(Box("a", 0, 0), Box("a", 1, 1)).Validate());
        Assert.NotNull(Design(new DesignElement("i", "I", DesignElementKind.Image, 0, 0, 10, 10, Path: "relativa.png")).Validate());
        Assert.NotNull(Design(Box("a", 0, 0) with { Opacity = 2 }).Validate());
        Assert.NotNull(Design(Box("a", 0, 0) with { Binding = DesignBinding.Clock }).Validate());
        Assert.NotNull((GraphicTemplates.Create(GraphicKind.LowerThird, "x", _project.Canvas) with { Design = [Box("a", 0, 0)] }).Validate());
    }

    // ---- Editor en directo ---------------------------------------------------------------------

    [Fact]
    public async Task PreviewGraphicCommand_AppliesInLiveWithoutEnteringUndoHistory()
    {
        var a = Box("a", 0, 0);
        var b = new DesignElement("b", "B", DesignElementKind.Text, 0, 0, 100, 50, Text: "B", FontSize: 30, Bold: true, Color: "#FFFFFFFF");
        var g = Design(a, b);
        await Ok(new AddGraphicCommand(g));
        _executor.ClearHistory(); // solo probamos los pasos de la edición: la creación no cuenta.
        var edited = g with { Design = [a with { X = 40 }, b with { Text = "B2" }] };
        var result = await _executor.ExecuteAsync(new PreviewGraphicCommand(edited));
        Assert.True(result.Success, result.Error);
        Assert.Equal(edited.Design, _project.GetGraphic(g.Id).Design);
        Assert.Contains("\"B2\"", Settings(g.Id, "b"));
        Assert.False(_executor.CanUndo);

        // Al soltar: un solo paso de deshacer vuelve al estado previo al gesto entero.
        var result2 = await _executor.ExecuteAsync(new UpdateGraphicCommand(g with { Design = [a, b with { Text = "Definitivo" }] }, previous: g));
        Assert.True(result2.Success, result2.Error);
        Assert.Contains("\"Definitivo\"", Settings(g.Id, "b"));
        await _executor.UndoAsync();
        Assert.Equal(g.Design, _project.GetGraphic(g.Id).Design);
    }

    [Fact]
    public async Task TemplateToDesignConversion_ChangesKindInOneUndoStep()
    {
        var template = GraphicTemplates.Create(GraphicKind.LowerThird, "Rótulo", _project.Canvas);
        await Ok(new AddGraphicCommand(template));
        var design = DesignOps.FromTemplate(template, _project.Canvas, template.Name) with { Id = template.Id };

        var rejected = await _executor.ExecuteAsync(new UpdateGraphicCommand(design));
        Assert.False(rejected.Success); // sin permiso explícito no se puede cambiar el tipo

        var result = await _executor.ExecuteAsync(new UpdateGraphicCommand(design, allowKindChange: true));
        Assert.True(result.Success, result.Error);
        Assert.Equal(GraphicKind.Custom, _project.GetGraphic(template.Id).Kind);

        await _executor.UndoAsync();
        Assert.Equal(GraphicKind.LowerThird, _project.GetGraphic(template.Id).Kind);
        Assert.Equal(template.Design, _project.GetGraphic(template.Id).Design);
    }
}

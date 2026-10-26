using LiveStream.Application.Commands;
using LiveStream.Application.Graphics;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Infrastructure.Persistence;

namespace LiveStream.UnitTests;

public sealed class BrandKitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ls-brand-").FullName;
    private readonly BrandKit _brand = new("Angelito", new GraphicStyle("#FF0055AA", "#E0101010", "#FFFFFFFF", "#FF000000", "Arial"), "");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ApplyBrand_RestylesEveryGraphic_SetsLogoOnlyWhereThereIsAField_AndUndoesAtOnce()
    {
        var project = new StudioProject(Guid.NewGuid(), "Test") { Canvas = new CanvasSettings(1920, 1080, 30) };
        var engine = new FakeStreamingEngine();
        var executor = new CommandExecutor(new StudioContext(project, engine));
        await GraphicLayer.CreateOverlayAsync(engine, project, CancellationToken.None);
        var lower = GraphicTemplates.Create(GraphicKind.LowerThird, "Rótulo", project.Canvas);
        var clock = GraphicTemplates.Create(GraphicKind.Clock, "Reloj", project.Canvas);
        Assert.True((await executor.ExecuteAsync(new AddGraphicCommand(lower))).Success);
        Assert.True((await executor.ExecuteAsync(new AddGraphicCommand(clock))).Success);

        var brand = _brand with { Logo = @"C:\marca\logo.png" };
        Assert.True((await executor.ExecuteAsync(new ApplyBrandCommand(brand))).Success);
        Assert.All(project.Graphics, g => Assert.Equal(brand.Style, g.Style));
        Assert.Equal(brand.Logo, project.GetGraphic(lower.Id).Field("logo"));
        Assert.False(project.GetGraphic(clock.Id).Fields.ContainsKey("logo"));

        await executor.UndoAsync();
        Assert.Equal(lower, project.GetGraphic(lower.Id));
        Assert.Equal(clock, project.GetGraphic(clock.Id));
    }

    [Fact]
    public void NewGraphics_TakeTheBrand() =>
        Assert.Equal(_brand.Style, GraphicTemplates.Create(GraphicKind.Headline, "x", new CanvasSettings(1920, 1080, 30), brand: _brand).Style);

    [Fact]
    public void ExportImport_CarriesTheLogoInsideTheFile()
    {
        var logo = Path.Combine(_dir, "logo.png");
        File.WriteAllBytes(logo, [0x89, 0x50, 0x4E, 0x47, 1, 2, 3]);
        var source = new BrandKitStore(Path.Combine(_dir, "a"));
        var file = Path.Combine(_dir, "marca.lsbrand");
        source.Export(_brand with { Logo = logo }, file);
        File.Delete(logo);

        var target = new BrandKitStore(Path.Combine(_dir, "b"));
        var imported = target.Import(file);
        Assert.Equal(_brand.Style, imported.Style);
        Assert.StartsWith(Path.Combine(_dir, "b", "brand"), imported.Logo);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3 }, File.ReadAllBytes(imported.Logo));

        target.Save(imported);
        Assert.Equal(imported, target.Load());
    }

    [Theory]
    [InlineData("""{"format":"otra-cosa","brand":{"name":"x","style":{}}}""")]
    [InlineData("""{"format":"livestream-brand-v1","brand":{"name":"x","style":{}},"logoName":"..\\..\\evil.exe","logoData":"AAAA"}""")]
    [InlineData("""{"format":"livestream-brand-v1","brand":{"name":"x","style":{"primaryColor":"rojo"}}}""")]
    public void Import_RejectsForeignFiles_NonImageLogos_AndInvalidColors(string json)
    {
        var file = Path.Combine(_dir, "bad.lsbrand");
        File.WriteAllText(file, json);
        Assert.Throws<InvalidDataException>(() => new BrandKitStore(_dir).Import(file));
        Assert.False(File.Exists(Path.Combine(_dir, "evil.exe")));
    }

    [Fact]
    public void Load_FallsBackToTheDefault_WhenTheFileIsDamaged()
    {
        File.WriteAllText(Path.Combine(_dir, "brand.json"), "{ dañado");
        Assert.Equal(BrandKit.Default, new BrandKitStore(_dir).Load());
    }
}

using StreamPilot.Application.Editing;
using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.UnitTests;

public sealed class CanvasGeometryTests
{
    private static EditorItem Item(double x, double y, double scale = 1, double rot = 0, int w = 200, int h = 100) =>
        new(Guid.NewGuid(), new Transform(x, y, scale, scale, rot), w, h);

    [Fact]
    public void ViewFit_MatchesNativeLetterboxAndRoundTrips()
    {
        var view = ViewTransform.Fit(1920, 1080, 960, 1000);
        Assert.Equal(0.5, view.Scale);
        Assert.Equal(0, view.OffsetX);
        Assert.Equal(230, view.OffsetY);

        var canvas = view.ToCanvas(480, 500);
        Assert.Equal(new Point2(960, 540), canvas);
        Assert.Equal(new Point2(480, 500), view.ToView(canvas));
    }

    [Fact]
    public void ZoomAt_KeepsPointUnderCursorFixed()
    {
        var view = ViewTransform.Fit(1920, 1080, 960, 540);
        var before = view.ToCanvas(300, 200);
        var zoomed = view.ZoomAt(300, 200, view.Scale * 2);
        var after = zoomed.ToCanvas(300, 200);
        Assert.Equal(before.X, after.X, 6);
        Assert.Equal(before.Y, after.Y, 6);
    }

    [Fact]
    public void HitTest_ReturnsTopmostVisible_RespectingRotation()
    {
        var bottom = Item(0, 0, w: 1920, h: 1080);
        var top = Item(100, 100);
        var hidden = Item(100, 100) with { Visible = false };

        Assert.Equal(top.Id, CanvasGeometry.HitTest([bottom, top, hidden], new Point2(150, 150))!.Id);
        Assert.Equal(bottom.Id, CanvasGeometry.HitTest([bottom, top], new Point2(1000, 900))!.Id);

        // Girado 90° alrededor de (500,500): ocupa x en [400,500], y en [500,700].
        var rotated = Item(500, 500, rot: 90);
        Assert.True(CanvasGeometry.Contains(rotated, new Point2(450, 600)));
        Assert.False(CanvasGeometry.Contains(rotated, new Point2(550, 550)));
    }

    [Fact]
    public void HitHandle_FindsCornersEdgesAndBody()
    {
        var item = Item(100, 100);
        Assert.Equal(Handle.TopLeft, CanvasGeometry.HitHandle(item, new Point2(101, 99), 5));
        Assert.Equal(Handle.BottomRight, CanvasGeometry.HitHandle(item, new Point2(300, 200), 5));
        Assert.Equal(Handle.Right, CanvasGeometry.HitHandle(item, new Point2(300, 150), 5));
        Assert.Equal(Handle.Body, CanvasGeometry.HitHandle(item, new Point2(200, 150), 5));
        Assert.Equal(Handle.None, CanvasGeometry.HitHandle(item, new Point2(500, 500), 5));
    }

    [Fact]
    public void Move_SnapsToCanvasCenterAndShowsGuide()
    {
        var item = Item(0, 0); // 200x100
        var gesture = new DragGesture(item, Handle.Body, new Point2(10, 10), 1920, 1080, []);

        // Centro del elemento queda a 3 px del centro del lienzo (960): se ajusta.
        var update = gesture.Update(new Point2(10 + 857, 10 + 300), snapDistance: 8, crop: false, freeAspect: false);

        Assert.Equal(860, update.Transform.X); // 860 + 100 = 960
        Assert.Equal([960], update.GuidesX);
        Assert.Empty(update.GuidesY);
    }

    [Fact]
    public void Move_WithoutSnapIsExact()
    {
        var item = Item(0, 0);
        var update = new DragGesture(item, Handle.Body, new Point2(0, 0), 1920, 1080, []).Update(new Point2(857, 3), 0, false, false);
        Assert.Equal(new Transform(857, 3, 1, 1, 0), update.Transform);
    }

    [Fact]
    public void Move_SnapsToOtherItemEdges()
    {
        var other = Item(500, 500);
        var item = Item(0, 0);
        var update = new DragGesture(item, Handle.Body, new Point2(0, 0), 1920, 1080, [other]).Update(new Point2(296, 50), 8, false, false);
        Assert.Equal(300, update.Transform.X); // borde derecho (300+200) con borde izquierdo del otro (500)
    }

    [Fact]
    public void CornerResize_KeepsAspectAndAnchorsOppositeCorner()
    {
        var item = Item(100, 100); // 200x100 -> esquina inferior derecha en (300,200)
        var gesture = new DragGesture(item, Handle.BottomRight, new Point2(300, 200), 1920, 1080, []);

        var update = gesture.Update(new Point2(500, 210), 0, false, freeAspect: false);

        Assert.Equal(2, update.Transform.ScaleX, 6); // ancho 400
        Assert.Equal(2, update.Transform.ScaleY, 6); // conserva aspecto
        Assert.Equal((100.0, 100.0), (update.Transform.X, update.Transform.Y));
    }

    [Fact]
    public void TopLeftResize_MovesPositionKeepsBottomRight()
    {
        var item = Item(100, 100);
        var update = new DragGesture(item, Handle.TopLeft, new Point2(100, 100), 1920, 1080, []).Update(new Point2(200, 150), 0, false, false);

        Assert.Equal(0.5, update.Transform.ScaleX, 6);
        var resized = item with { Transform = update.Transform };
        var corners = CanvasGeometry.Corners(resized);
        Assert.Equal(300, corners[2].X, 6);
        Assert.Equal(200, corners[2].Y, 6);
    }

    [Fact]
    public void EdgeResize_StretchesOneAxis()
    {
        var item = Item(100, 100);
        var update = new DragGesture(item, Handle.Right, new Point2(300, 150), 1920, 1080, []).Update(new Point2(400, 500), 0, false, false);
        Assert.Equal(1.5, update.Transform.ScaleX, 6);
        Assert.Equal(1, update.Transform.ScaleY, 6);
    }

    [Fact]
    public void Crop_LeftEdgeKeepsVisibleContentInPlace()
    {
        var item = Item(100, 100, scale: 2); // fuente 200x100 a escala 2 -> 400x200
        var update = new DragGesture(item, Handle.Left, new Point2(100, 200), 1920, 1080, []).Update(new Point2(140, 200), 0, crop: true, freeAspect: false);

        Assert.Equal(20, update.Transform.CropLeft); // 40 px de lienzo / escala 2
        Assert.Equal(140, update.Transform.X);
        Assert.Equal(2, update.Transform.ScaleX);
    }

    [Fact]
    public void Crop_NeverRemovesWholeSource()
    {
        var item = Item(0, 0);
        var update = new DragGesture(item, Handle.Right, new Point2(200, 50), 1920, 1080, []).Update(new Point2(-5000, 50), 0, true, false);
        Assert.Equal(199, update.Transform.CropRight);
    }
}

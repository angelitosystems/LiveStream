using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.Application.Editing;

/// <summary>Elemento tal como lo ve el editor: transformación + tamaño real de la fuente.</summary>
public sealed record EditorItem(Guid Id, Transform Transform, int SourceWidth, int SourceHeight, bool Visible = true, bool Locked = false)
{
    public double CroppedWidth => Math.Max(0, SourceWidth - Transform.CropLeft - Transform.CropRight);
    public double CroppedHeight => Math.Max(0, SourceHeight - Transform.CropTop - Transform.CropBottom);
    public double Width => CroppedWidth * Transform.ScaleX;
    public double Height => CroppedHeight * Transform.ScaleY;
    public bool HasSize => CroppedWidth > 0 && CroppedHeight > 0;
}

public readonly record struct Point2(double X, double Y)
{
    public static Point2 operator +(Point2 a, Point2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Point2 operator -(Point2 a, Point2 b) => new(a.X - b.X, a.Y - b.Y);
}

public enum Handle { None, Body, TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left }

/// <summary>
/// Correspondencia entre píxeles de la ventana de vista previa y coordenadas del lienzo. Es exactamente la
/// misma fórmula que usa el puente nativo al dibujar (ajuste centrado o zoom + desplazamiento).
/// </summary>
public readonly record struct ViewTransform(double Scale, double OffsetX, double OffsetY)
{
    public static ViewTransform Fit(int canvasWidth, int canvasHeight, double viewWidth, double viewHeight)
    {
        var scale = Math.Min(viewWidth / canvasWidth, viewHeight / canvasHeight);
        if (!double.IsFinite(scale) || scale <= 0)
            scale = 1;
        return new ViewTransform(scale, (viewWidth - canvasWidth * scale) / 2, (viewHeight - canvasHeight * scale) / 2);
    }

    public Point2 ToCanvas(double viewX, double viewY) => new((viewX - OffsetX) / Scale, (viewY - OffsetY) / Scale);

    public Point2 ToView(Point2 canvas) => new(canvas.X * Scale + OffsetX, canvas.Y * Scale + OffsetY);

    /// <summary>Zoom manteniendo fijo el punto del lienzo bajo el cursor.</summary>
    public ViewTransform ZoomAt(double viewX, double viewY, double newScale)
    {
        newScale = Math.Clamp(newScale, 0.05, 32);
        var anchor = ToCanvas(viewX, viewY);
        return new ViewTransform(newScale, viewX - anchor.X * newScale, viewY - anchor.Y * newScale);
    }
}

public static class CanvasGeometry
{
    /// <summary>Esquinas en el lienzo (sup-izq, sup-der, inf-der, inf-izq). El giro pivota en la posición (alineación sup-izq).</summary>
    public static Point2[] Corners(EditorItem item)
    {
        var p = new Point2(item.Transform.X, item.Transform.Y);
        return [p, p + Rotate(new(item.Width, 0), item), p + Rotate(new(item.Width, item.Height), item), p + Rotate(new(0, item.Height), item)];
    }

    /// <summary>Caja envolvente alineada a los ejes (para guías con elementos girados).</summary>
    public static (double Left, double Top, double Right, double Bottom) Bounds(EditorItem item)
    {
        var c = Corners(item);
        return (c.Min(p => p.X), c.Min(p => p.Y), c.Max(p => p.X), c.Max(p => p.Y));
    }

    /// <summary>Punto del lienzo en coordenadas locales del elemento (sin girar, en píxeles del lienzo).</summary>
    public static Point2 ToLocal(EditorItem item, Point2 canvas) =>
        RotateBy(canvas - new Point2(item.Transform.X, item.Transform.Y), -item.Transform.RotationDegrees);

    public static Point2 Rotate(Point2 local, EditorItem item) => RotateBy(local, item.Transform.RotationDegrees);

    public static Point2 RotateBy(Point2 v, double degrees)
    {
        var r = degrees * Math.PI / 180;
        var (sin, cos) = Math.SinCos(r);
        return new Point2(v.X * cos - v.Y * sin, v.X * sin + v.Y * cos);
    }

    public static bool Contains(EditorItem item, Point2 canvas)
    {
        var l = ToLocal(item, canvas);
        return item.HasSize && Between(l.X, 0, item.Width) && Between(l.Y, 0, item.Height);
    }

    /// <summary>Elemento visible más alto bajo el punto. <paramref name="bottomToTop"/> sigue el orden de la escena.</summary>
    public static EditorItem? HitTest(IReadOnlyList<EditorItem> bottomToTop, Point2 canvas)
    {
        for (var i = bottomToTop.Count - 1; i >= 0; i--)
            if (bottomToTop[i].Visible && Contains(bottomToTop[i], canvas))
                return bottomToTop[i];
        return null;
    }

    /// <summary>Tirador bajo el punto; <paramref name="radius"/> en unidades del lienzo.</summary>
    public static Handle HitHandle(EditorItem item, Point2 canvas, double radius)
    {
        if (!item.HasSize)
            return Handle.None;
        var l = ToLocal(item, canvas);
        double w = item.Width, h = item.Height;
        // Con escala negativa (volteo) el rectángulo local se extiende hacia el lado contrario.
        bool Near(double a, double b) => Math.Abs(a - b) <= radius;
        var left = Near(l.X, 0);
        var right = Near(l.X, w);
        var top = Near(l.Y, 0);
        var bottom = Near(l.Y, h);
        var insideX = Between(l.X, 0, w);
        var insideY = Between(l.Y, 0, h);
        if (top && left) return Handle.TopLeft;
        if (top && right) return Handle.TopRight;
        if (bottom && right) return Handle.BottomRight;
        if (bottom && left) return Handle.BottomLeft;
        if (top && Near(l.X, w / 2)) return Handle.Top;
        if (bottom && Near(l.X, w / 2)) return Handle.Bottom;
        if (left && Near(l.Y, h / 2)) return Handle.Left;
        if (right && Near(l.Y, h / 2)) return Handle.Right;
        return insideX && insideY ? Handle.Body : Handle.None;
    }

    private static bool Between(double v, double a, double b) => v >= Math.Min(a, b) && v <= Math.Max(a, b);
}

/// <summary>Resultado de un paso de arrastre: nueva transformación y guías de alineación a dibujar.</summary>
public sealed record DragUpdate(Transform Transform, IReadOnlyList<double> GuidesX, IReadOnlyList<double> GuidesY);

/// <summary>
/// Un gesto de arrastre (mover, escalar o recortar) sobre un elemento. Se crea al pulsar y se alimenta con cada
/// movimiento; el editor aplica la transformación al motor en vivo y emite un único comando al soltar.
/// </summary>
public sealed class DragGesture
{
    private readonly EditorItem _start;
    private readonly Handle _handle;
    private readonly Point2 _origin;
    private readonly int _canvasWidth;
    private readonly int _canvasHeight;
    private readonly IReadOnlyList<EditorItem> _others;

    public DragGesture(EditorItem item, Handle handle, Point2 origin, int canvasWidth, int canvasHeight, IReadOnlyList<EditorItem> others)
    {
        _start = item;
        _handle = handle;
        _origin = origin;
        _canvasWidth = canvasWidth;
        _canvasHeight = canvasHeight;
        _others = others.Where(o => o.Id != item.Id && o.Visible && o.HasSize).ToList();
    }

    public Guid ItemId => _start.Id;
    public Transform StartTransform => _start.Transform;
    public Handle Handle => _handle;

    /// <param name="snapDistance">Distancia de atracción en unidades del lienzo (0 = sin imán).</param>
    /// <param name="crop">Recortar en lugar de escalar (Alt).</param>
    /// <param name="freeAspect">Permite deformar al escalar desde una esquina (Shift).</param>
    public DragUpdate Update(Point2 current, double snapDistance, bool crop, bool freeAspect)
    {
        var delta = current - _origin;
        return _handle == Handle.Body ? Move(delta, snapDistance) : crop ? Crop(delta) : Resize(delta, snapDistance, freeAspect);
    }

    private DragUpdate Move(Point2 delta, double snap)
    {
        var t = _start.Transform.WithPosition(_start.Transform.X + delta.X, _start.Transform.Y + delta.Y);
        if (snap <= 0)
            return new DragUpdate(t, [], []);

        var moved = _start with { Transform = t };
        var (l, top, r, b) = CanvasGeometry.Bounds(moved);
        var (dx, gx) = BestSnap([l, (l + r) / 2, r], TargetsX(), snap);
        var (dy, gy) = BestSnap([top, (top + b) / 2, b], TargetsY(), snap);
        return new DragUpdate(t.WithPosition(t.X + dx, t.Y + dy), gx is { } x ? [x] : [], gy is { } y ? [y] : []);
    }

    private DragUpdate Resize(Point2 canvasDelta, double snap, bool freeAspect)
    {
        var s = _start;
        var d = CanvasGeometry.RotateBy(canvasDelta, -s.Transform.RotationDegrees);
        double left = 0, top = 0, right = s.Width, bottom = s.Height;
        var movesLeft = _handle is Handle.Left or Handle.TopLeft or Handle.BottomLeft;
        var movesRight = _handle is Handle.Right or Handle.TopRight or Handle.BottomRight;
        var movesTop = _handle is Handle.Top or Handle.TopLeft or Handle.TopRight;
        var movesBottom = _handle is Handle.Bottom or Handle.BottomLeft or Handle.BottomRight;
        if (movesLeft) left += d.X;
        if (movesRight) right += d.X;
        if (movesTop) top += d.Y;
        if (movesBottom) bottom += d.Y;

        // Imán sobre los bordes que se mueven (solo sin giro: con giro los bordes no son verticales).
        var guidesX = new List<double>();
        var guidesY = new List<double>();
        if (snap > 0 && Math.Abs(s.Transform.RotationDegrees % 360) < 1e-6)
        {
            if (movesLeft || movesRight)
            {
                var edge = s.Transform.X + (movesLeft ? left : right);
                var (dx, gx) = BestSnap([edge], TargetsX(), snap);
                if (movesLeft) left += dx; else right += dx;
                if (gx is { } x) guidesX.Add(x);
            }
            if (movesTop || movesBottom)
            {
                var edge = s.Transform.Y + (movesTop ? top : bottom);
                var (dy, gy) = BestSnap([edge], TargetsY(), snap);
                if (movesTop) top += dy; else bottom += dy;
                if (gy is { } y) guidesY.Add(y);
            }
        }

        var isCorner = movesLeft != movesRight && movesTop != movesBottom;
        double newW = Math.Max(1, right - left), newH = Math.Max(1, bottom - top);
        if (isCorner && !freeAspect && s.Width > 0 && s.Height > 0)
        {
            // Conserva la relación de aspecto siguiendo el eje que más cambió.
            var factor = Math.Max(newW / s.Width, newH / s.Height);
            newW = s.Width * factor;
            newH = s.Height * factor;
        }
        // El borde opuesto al tirador queda fijo.
        var originX = movesLeft ? s.Width - newW : 0;
        var originY = movesTop ? s.Height - newH : 0;
        var pos = new Point2(s.Transform.X, s.Transform.Y) + CanvasGeometry.Rotate(new Point2(originX, originY), s);
        var t = s.Transform.WithPosition(pos.X, pos.Y).WithScale(
            s.CroppedWidth > 0 ? newW / s.CroppedWidth : s.Transform.ScaleX,
            s.CroppedHeight > 0 ? newH / s.CroppedHeight : s.Transform.ScaleY);
        return new DragUpdate(t, guidesX, guidesY);
    }

    private DragUpdate Crop(Point2 canvasDelta)
    {
        var s = _start;
        var t = s.Transform;
        var d = CanvasGeometry.RotateBy(canvasDelta, -t.RotationDegrees);
        double cropL = t.CropLeft, cropT = t.CropTop, cropR = t.CropRight, cropB = t.CropBottom;
        double shiftX = 0, shiftY = 0;
        var maxW = s.SourceWidth - 1;
        var maxH = s.SourceHeight - 1;

        if (Handle is Handle.Left or Handle.TopLeft or Handle.BottomLeft)
        {
            var c = Math.Clamp(cropL + d.X / t.ScaleX, 0, maxW - cropR);
            shiftX = (c - cropL) * t.ScaleX;
            cropL = c;
        }
        if (Handle is Handle.Right or Handle.TopRight or Handle.BottomRight)
            cropR = Math.Clamp(cropR - d.X / t.ScaleX, 0, maxW - cropL);
        if (Handle is Handle.Top or Handle.TopLeft or Handle.TopRight)
        {
            var c = Math.Clamp(cropT + d.Y / t.ScaleY, 0, maxH - cropB);
            shiftY = (c - cropT) * t.ScaleY;
            cropT = c;
        }
        if (Handle is Handle.Bottom or Handle.BottomLeft or Handle.BottomRight)
            cropB = Math.Clamp(cropB - d.Y / t.ScaleY, 0, maxH - cropT);

        // Lo que queda visible no se mueve: la posición avanza lo mismo que se recorta por la izquierda/arriba.
        var pos = new Point2(t.X, t.Y) + CanvasGeometry.Rotate(new Point2(shiftX, shiftY), s);
        return new DragUpdate(t.WithPosition(pos.X, pos.Y).WithCrop((int)Math.Round(cropL), (int)Math.Round(cropT),
            (int)Math.Round(cropR), (int)Math.Round(cropB)), [], []);
    }

    private IEnumerable<double> TargetsX() =>
        new double[] { 0, _canvasWidth / 2.0, _canvasWidth }.Concat(_others.SelectMany(o =>
        {
            var (l, _, r, _) = CanvasGeometry.Bounds(o);
            return new[] { l, (l + r) / 2, r };
        }));

    private IEnumerable<double> TargetsY() =>
        new double[] { 0, _canvasHeight / 2.0, _canvasHeight }.Concat(_others.SelectMany(o =>
        {
            var (_, t, _, b) = CanvasGeometry.Bounds(o);
            return new[] { t, (t + b) / 2, b };
        }));

    /// <summary>Menor corrección que alinea alguna de las referencias con algún objetivo dentro de la distancia.</summary>
    internal static (double Delta, double? Guide) BestSnap(IReadOnlyList<double> references, IEnumerable<double> targets, double distance)
    {
        double bestDelta = 0;
        double? guide = null;
        var best = distance;
        foreach (var target in targets)
            foreach (var reference in references)
            {
                var diff = target - reference;
                if (Math.Abs(diff) <= best)
                {
                    best = Math.Abs(diff);
                    bestDelta = diff;
                    guide = target;
                }
            }
        return (bestDelta, guide);
    }
}

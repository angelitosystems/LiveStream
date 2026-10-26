using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LiveStream.Application.Graphics;
using LiveStream.Domain.Graphics;

namespace LiveStream.Desktop.Components;

/// <summary>
/// Lienzo del editor visual: dibuja el diseño libre sobre el lienzo del proyecto y permite seleccionar (clic,
/// Mayús+clic, recuadro), mover con guías inteligentes y cuadrícula, redimensionar (Mayús: proporcional) y girar
/// (Mayús: pasos de 15°). Ctrl+rueda acerca/aleja, la rueda desplaza y el botón central arrastra la vista.
/// Es una aproximación fiel del render de libobs (mismas cajas, giros, colores y tipografías del sistema); el
/// resultado exacto se ve en la vista previa del estudio.
/// </summary>
public sealed class DesignCanvas : Control
{
    private enum Mode { None, Move, Resize, Rotate, Marquee, Pan }

    private const double HandleSize = 9;
    private const double RotateHandleDistance = 28;

    private readonly Dictionary<string, Bitmap?> _bitmaps = [];
    private Mode _mode;
    private Point _start;
    private Point _current;
    private IReadOnlyList<DesignElement> _startDesign = [];
    private DesignElement? _startElement;
    private Vector _startPan;
    private IReadOnlyList<SnapGuide> _guides = [];
    private bool _changed;

    public DesignCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public IReadOnlyList<DesignElement> Design { get; private set; } = [];
    public HashSet<string> Selection { get; } = [];
    public Size CanvasSize { get; set; } = new(1920, 1080);
    /// <summary>Posición del gráfico en el lienzo y su escala: diseño → lienzo = Origin + p × GraphicScale.</summary>
    public Point Origin { get; set; }
    public double GraphicScale { get; set; } = 1;
    public double Zoom { get; private set; } = 1;
    public Vector Pan { get; private set; }
    public bool ShowGrid { get; set; } = true;
    public bool SnapEnabled { get; set; } = true;
    public double GridSize { get; set; } = 20;
    /// <summary>Texto que se ve en un elemento (campos {x}, reloj, cuenta atrás).</summary>
    public Func<DesignElement, string>? TextOf { get; set; }

    /// <summary>El diseño cambió por una interacción. commit = false mientras se arrastra; true al soltar.</summary>
    public event Action<IReadOnlyList<DesignElement>, bool>? DesignChanged;
    public event Action? SelectionChanged;
    public event Action<DesignElement>? ElementDoubleClicked;
    /// <summary>Empieza una manipulación (mover, redimensionar o girar): el anfitrión recuerda el estado para deshacer.</summary>
    public event Action? GestureStarted;
    /// <summary>Cambió el zoom o el encuadre (al anfitrión le interesa para la etiqueta).</summary>
    public event Action? ZoomChanged;

    public void SetDesign(IReadOnlyList<DesignElement> design)
    {
        Design = design;
        Selection.RemoveWhere(k => design.All(e => e.Key != k));
        InvalidateVisual();
    }

    public void Select(IEnumerable<string> keys)
    {
        Selection.Clear();
        Selection.UnionWith(keys);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    public void SetZoom(double zoom, Point? anchor = null)
    {
        var old = Scale;
        var at = anchor ?? new Point(Bounds.Width / 2, Bounds.Height / 2);
        var canvasPoint = ScreenToCanvas(at);
        Zoom = Math.Clamp(zoom, 0.1, 8);
        // El punto bajo el cursor no se mueve al hacer zoom.
        var moved = CanvasToScreen(canvasPoint);
        Pan += at - moved;
        if (Math.Abs(old - Scale) > 0.0001)
        {
            InvalidateVisual();
            ZoomChanged?.Invoke();
        }
    }

    public void ResetView()
    {
        Zoom = 1;
        Pan = default;
        InvalidateVisual();
    }

    /// <summary>Forgets cached images (tras volver a enlazar un recurso).</summary>
    public void ReloadImages()
    {
        foreach (var bitmap in _bitmaps.Values)
            bitmap?.Dispose();
        _bitmaps.Clear();
        InvalidateVisual();
    }

    // ---- Coordenadas ------------------------------------------------------------------------------

    private double FitScale => Math.Max(0.01, Math.Min((Bounds.Width - 48) / CanvasSize.Width, (Bounds.Height - 48) / CanvasSize.Height));
    private double Scale => FitScale * Zoom;
    private Point Offset => new Point((Bounds.Width - CanvasSize.Width * Scale) / 2, (Bounds.Height - CanvasSize.Height * Scale) / 2) + Pan;

    private Point CanvasToScreen(Point c) => new(Offset.X + c.X * Scale, Offset.Y + c.Y * Scale);
    private Point ScreenToCanvas(Point s) => new((s.X - Offset.X) / Scale, (s.Y - Offset.Y) / Scale);
    private Point DesignToScreen(double x, double y) => CanvasToScreen(new Point(Origin.X + x * GraphicScale, Origin.Y + y * GraphicScale));
    private Point ScreenToDesign(Point s)
    {
        var c = ScreenToCanvas(s);
        return new Point((c.X - Origin.X) / GraphicScale, (c.Y - Origin.Y) / GraphicScale);
    }

    /// <summary>El lienzo del proyecto en coordenadas del diseño (para alinear y ajustar a sus bordes y centro).</summary>
    public DesignRect CanvasInDesign => new(-Origin.X / GraphicScale, -Origin.Y / GraphicScale, CanvasSize.Width / GraphicScale, CanvasSize.Height / GraphicScale);

    /// <summary>Centro de lo que se ve, en coordenadas del diseño (donde aparecen los elementos nuevos).</summary>
    public (double X, double Y) ViewCenterInDesign
    {
        get
        {
            var p = ScreenToDesign(new Point(Bounds.Width / 2, Bounds.Height / 2));
            return (p.X, p.Y);
        }
    }

    // ---- Dibujo -------------------------------------------------------------------------------

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(new SolidColorBrush(Color.Parse("#FF0E1015")), new Rect(Bounds.Size));
        var canvas = new Rect(CanvasToScreen(default), CanvasToScreen(new Point(CanvasSize.Width, CanvasSize.Height)));
        context.FillRectangle(new SolidColorBrush(Color.Parse("#FF1C1F27")), canvas);
        using (context.PushClip(canvas))
        {
            DrawChecker(context, canvas);
            if (ShowGrid && GridSize * Scale * GraphicScale >= 6)
                DrawGrid(context, canvas);
            foreach (var e in Design.Where(e => e.Visible))
                DrawElement(context, e);
        }
        context.DrawRectangle(null, new Pen(new SolidColorBrush(Color.Parse("#FF4A5060")), 1), canvas);

        var guidePen = new Pen(Brushes.Magenta, 1);
        foreach (var g in _guides)
        {
            if (g.Vertical)
            {
                var x = DesignToScreen(g.Position, 0).X;
                context.DrawLine(guidePen, new Point(x, 0), new Point(x, Bounds.Height));
            }
            else
            {
                var y = DesignToScreen(0, g.Position).Y;
                context.DrawLine(guidePen, new Point(0, y), new Point(Bounds.Width, y));
            }
        }
        DrawSelection(context);
        if (_mode == Mode.Marquee)
        {
            var r = new Rect(_start, _current).Normalize();
            context.DrawRectangle(new SolidColorBrush(Color.FromArgb(40, 80, 160, 255)), new Pen(new SolidColorBrush(Color.FromArgb(200, 80, 160, 255)), 1), r);
        }
    }

    private void DrawChecker(DrawingContext context, Rect canvas)
    {
        // Cuadros grandes y tenues: indican transparencia sin distraer.
        var size = 48 * Scale;
        if (size < 8)
            return;
        var brush = new SolidColorBrush(Color.Parse("#FF22262F"));
        for (var y = 0; y * size < canvas.Height; y++)
            for (var x = (y % 2); x * size < canvas.Width; x += 2)
                context.FillRectangle(brush, new Rect(canvas.X + x * size, canvas.Y + y * size, size, size));
    }

    private void DrawGrid(DrawingContext context, Rect canvas)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), 1);
        var step = GridSize * GraphicScale;
        var startX = Origin.X % step;
        var startY = Origin.Y % step;
        for (var x = startX; x <= CanvasSize.Width; x += step)
            context.DrawLine(pen, CanvasToScreen(new Point(x, 0)), CanvasToScreen(new Point(x, CanvasSize.Height)));
        for (var y = startY; y <= CanvasSize.Height; y += step)
            context.DrawLine(pen, CanvasToScreen(new Point(0, y)), CanvasToScreen(new Point(CanvasSize.Width, y)));
        var center = new Pen(new SolidColorBrush(Color.FromArgb(60, 120, 180, 255)), 1, DashStyle.Dash);
        context.DrawLine(center, CanvasToScreen(new Point(CanvasSize.Width / 2, 0)), CanvasToScreen(new Point(CanvasSize.Width / 2, CanvasSize.Height)));
        context.DrawLine(center, CanvasToScreen(new Point(0, CanvasSize.Height / 2)), CanvasToScreen(new Point(CanvasSize.Width, CanvasSize.Height / 2)));
    }

    private static Color ColorOf(string? hex, double opacity = 1)
    {
        var c = Color.TryParse(hex, out var parsed) ? parsed : Colors.White;
        return Color.FromArgb((byte)Math.Round(c.A * Math.Clamp(opacity, 0, 1)), c.R, c.G, c.B);
    }

    private void DrawElement(DrawingContext context, DesignElement e)
    {
        var s = Scale * GraphicScale;
        if (e.ShadowColor is { } shadow && e.Kind is DesignElementKind.Rect or DesignElementKind.Text)
            DrawPiece(context, e with { X = e.X + e.ShadowX, Y = e.Y + e.ShadowY, Color = shadow, GradientColor = null, OutlineSize = 0 }, s);
        DrawPiece(context, e, s);
    }

    private void DrawPiece(DrawingContext context, DesignElement e, double s)
    {
        var at = DesignToScreen(e.X, e.Y);
        var box = new Rect(0, 0, e.Width * s, e.Height * s);
        using var _ = context.PushTransform(Matrix.CreateRotation(e.Rotation * Math.PI / 180) * Matrix.CreateTranslation(at.X, at.Y));
        switch (e.Kind)
        {
            case DesignElementKind.Rect:
                context.FillRectangle(new SolidColorBrush(ColorOf(e.Color, e.Opacity)), box);
                break;

            case DesignElementKind.Text:
            {
                var typeface = new Typeface(new FontFamily(e.FontFace ?? "Segoe UI"), e.Italic ? FontStyle.Italic : FontStyle.Normal,
                    e.Bold ? FontWeight.Bold : FontWeight.Normal);
                IBrush brush = e.GradientColor is { } g
                    ? new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                        EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(ColorOf(e.Color, e.Opacity), 0), new GradientStop(ColorOf(g, e.Opacity), 1) },
                    }
                    : new SolidColorBrush(ColorOf(e.Color, e.Opacity));
                var text = new FormattedText(TextOf?.Invoke(e) ?? e.Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface,
                    Math.Max(1, e.FontSize * s), brush)
                {
                    MaxTextWidth = Math.Max(1, box.Width),
                    TextAlignment = e.Align switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left },
                };
                var origin = new Point(0, (box.Height - text.Height) / 2);
                using (context.PushClip(box))
                {
                    if (e is { OutlineColor: { } outline, OutlineSize: > 0 } && text.BuildGeometry(origin) is { } geometry)
                        context.DrawGeometry(brush, new Pen(new SolidColorBrush(ColorOf(outline, e.Opacity)), e.OutlineSize * s * 2), geometry);
                    context.DrawText(text, origin);
                }
                break;
            }

            case DesignElementKind.Image:
                if (Bitmap(e.Path) is { } bitmap)
                {
                    var size = bitmap.PixelSize;
                    var source = new Rect(e.CropLeft, e.CropTop, Math.Max(1, size.Width - e.CropLeft - e.CropRight), Math.Max(1, size.Height - e.CropTop - e.CropBottom));
                    using (context.PushOpacity(e.Opacity))
                        context.DrawImage(bitmap, source, box);
                }
                else
                {
                    DrawPlaceholder(context, box, $"Falta: {System.IO.Path.GetFileName(e.Path)}", Colors.IndianRed);
                }
                break;

            case DesignElementKind.Video:
                DrawPlaceholder(context, box, File.Exists(e.Path) ? $"▶ {System.IO.Path.GetFileName(e.Path)}" : $"Falta: {System.IO.Path.GetFileName(e.Path)}",
                    File.Exists(e.Path) ? Color.Parse("#FF6A7BFF") : Colors.IndianRed);
                break;
        }
    }

    private static void DrawPlaceholder(DrawingContext context, Rect box, string label, Color color)
    {
        context.DrawRectangle(new SolidColorBrush(Color.FromArgb(50, color.R, color.G, color.B)), new Pen(new SolidColorBrush(color), 1, DashStyle.Dash), box);
        var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, 12, Brushes.White)
        {
            MaxTextWidth = Math.Max(1, box.Width - 8),
        };
        context.DrawText(text, new Point(4, 4));
    }

    private Bitmap? Bitmap(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        if (!_bitmaps.TryGetValue(path, out var bitmap))
        {
            try
            {
                bitmap = File.Exists(path) ? new Bitmap(path) : null;
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                bitmap = null;
            }
            _bitmaps[path] = bitmap;
        }
        return bitmap;
    }

    private void DrawSelection(DrawingContext context)
    {
        var selected = Design.Where(e => Selection.Contains(e.Key) && e.Visible).ToList();
        var accent = new SolidColorBrush(Color.Parse("#FF3B9CFF"));
        var pen = new Pen(accent, 1.5);
        foreach (var e in selected)
        {
            var corners = DesignOps.Corners(e).Select(c => DesignToScreen(c.X, c.Y)).ToArray();
            var geometry = new PolylineGeometry(corners.Append(corners[0]).ToArray(), false);
            context.DrawGeometry(null, e.Locked ? new Pen(Brushes.Orange, 1.5, DashStyle.Dash) : pen, geometry);
        }
        if (selected.Count == 1 && !selected[0].Locked)
        {
            var (resize, rotate, top) = Handles(selected[0]);
            context.DrawLine(pen, top, rotate);
            context.DrawRectangle(Brushes.White, pen, new Rect(resize.X - HandleSize / 2, resize.Y - HandleSize / 2, HandleSize, HandleSize));
            context.DrawEllipse(Brushes.White, pen, rotate, HandleSize / 2 + 1, HandleSize / 2 + 1);
        }
    }

    /// <summary>Tiradores en pantalla: redimensionar (esquina inferior derecha) y girar (sobre el centro del borde superior).</summary>
    private (Point Resize, Point Rotate, Point TopCenter) Handles(DesignElement e)
    {
        var a = e.Rotation * Math.PI / 180;
        var corners = DesignOps.Corners(e);
        var resize = DesignToScreen(corners[2].X, corners[2].Y);
        var top = DesignToScreen((corners[0].X + corners[1].X) / 2, (corners[0].Y + corners[1].Y) / 2);
        // «Arriba» del elemento girado.
        var up = new Vector(Math.Sin(a), -Math.Cos(a));
        return (resize, top + up * RotateHandleDistance, top);
    }

    // ---- Interacción --------------------------------------------------------------------------

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var point = e.GetCurrentPoint(this);
        _start = _current = e.GetPosition(this);
        _startDesign = Design;
        _changed = false;
        e.Pointer.Capture(this);

        if (point.Properties.IsMiddleButtonPressed || point.Properties.IsRightButtonPressed)
        {
            _mode = Mode.Pan;
            _startPan = Pan;
            return;
        }

        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        if (Selection.Count == 1 && Design.FirstOrDefault(x => Selection.Contains(x.Key)) is { Locked: false, Visible: true } single)
        {
            var (resize, rotate, _) = Handles(single);
            if (Distance(_start, resize) <= HandleSize)
            {
                (_mode, _startElement) = (Mode.Resize, single);
                GestureStarted?.Invoke();
                return;
            }
            if (Distance(_start, rotate) <= HandleSize)
            {
                (_mode, _startElement) = (Mode.Rotate, single);
                GestureStarted?.Invoke();
                return;
            }
        }

        var d = ScreenToDesign(_start);
        var hit = DesignOps.HitTest(Design, d.X, d.Y);
        if (hit is null)
        {
            if (!shift)
                Selection.Clear();
            _mode = Mode.Marquee;
            SelectionChanged?.Invoke();
            InvalidateVisual();
            return;
        }
        if (e.ClickCount == 2)
        {
            ElementDoubleClicked?.Invoke(hit);
            return;
        }
        if (shift)
        {
            if (!Selection.Remove(hit.Key))
                Selection.Add(hit.Key);
        }
        else if (!Selection.Contains(hit.Key))
        {
            Selection.Clear();
            Selection.Add(hit.Key);
        }
        (_mode, _startElement) = (Mode.Move, hit);
        GestureStarted?.Invoke();
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_mode == Mode.None)
        {
            Cursor = HoverCursor(e.GetPosition(this));
            return;
        }
        _current = e.GetPosition(this);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        switch (_mode)
        {
            case Mode.Pan:
                Pan = _startPan + (_current - _start);
                InvalidateVisual();
                return;
            case Mode.Marquee:
                InvalidateVisual();
                return;
            case Mode.Move:
            {
                var a = ScreenToDesign(_start);
                var b = ScreenToDesign(_current);
                if (!_changed && Distance(_start, _current) < 3)
                    return;
                var moving = Selection.Where(k => _startDesign.Any(x => x.Key == k && !x.Locked)).ToList();
                var (dx, dy, guides) = SnapEnabled
                    ? DesignOps.Snap(_startDesign, moving, b.X - a.X, b.Y - a.Y, CanvasInDesign, 6 / (Scale * GraphicScale), GridSize)
                    : (Math.Round(b.X - a.X), Math.Round(b.Y - a.Y), []);
                _guides = guides;
                Emit(DesignOps.Move(_startDesign, moving, dx, dy));
                return;
            }
            case Mode.Resize when _startElement is { } el:
            {
                var d = ScreenToDesign(_current);
                var (lx, ly) = DesignOps.ToLocal(el, d.X, d.Y);
                double w = Math.Max(4, lx), h = Math.Max(4, ly);
                if (shift)
                {
                    var ratio = el.Width / el.Height;
                    if (w / h > ratio) w = h * ratio; else h = w / ratio;
                }
                if (SnapEnabled && GridSize > 0 && el.Rotation % 360 == 0)
                {
                    w = Math.Max(4, Math.Round((el.X + w) / GridSize) * GridSize - el.X);
                    h = Math.Max(4, Math.Round((el.Y + h) / GridSize) * GridSize - el.Y);
                }
                Emit(DesignOps.Update(_startDesign, [el.Key], x => x with { Width = Math.Round(w), Height = Math.Round(h) }));
                return;
            }
            case Mode.Rotate when _startElement is { } el:
            {
                var origin = DesignToScreen(el.X, el.Y);
                double Angle(Point p) => Math.Atan2(p.Y - origin.Y, p.X - origin.X) * 180 / Math.PI;
                var angle = el.Rotation + Angle(_current) - Angle(_start);
                angle = shift ? Math.Round(angle / 15) * 15 : Math.Round(angle, 1);
                angle = ((angle % 360) + 360) % 360;
                Emit(DesignOps.Update(_startDesign, [el.Key], x => x with { Rotation = angle }));
                return;
            }
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        if (_mode == Mode.Marquee)
        {
            var a = ScreenToDesign(_start);
            var b = ScreenToDesign(_current);
            var box = new DesignRect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
            if (box.Width > 2 || box.Height > 2)
            {
                foreach (var el in Design.Where(x => x.Visible && Intersects(DesignOps.Bounds(x), box)))
                    Selection.Add(el.Key);
                SelectionChanged?.Invoke();
            }
        }
        if (_changed)
            DesignChanged?.Invoke(Design, true);
        _mode = Mode.None;
        _guides = [];
        _startElement = null;
        InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            SetZoom(Zoom * (e.Delta.Y > 0 ? 1.15 : 1 / 1.15), e.GetPosition(this));
            ZoomChanged?.Invoke();
        }
        else
        {
            Pan += e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? new Vector(e.Delta.Y * 60, 0) : new Vector(e.Delta.X * 60, e.Delta.Y * 60);
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        InvalidateVisual();
    }

    private void Emit(IReadOnlyList<DesignElement> design)
    {
        _changed = true;
        Design = design;
        DesignChanged?.Invoke(design, false);
        InvalidateVisual();
    }

    private Cursor? HoverCursor(Point p)
    {
        if (Selection.Count == 1 && Design.FirstOrDefault(x => Selection.Contains(x.Key)) is { Locked: false, Visible: true } single)
        {
            var (resize, rotate, _) = Handles(single);
            if (Distance(p, resize) <= HandleSize)
                return new Cursor(StandardCursorType.BottomRightCorner);
            if (Distance(p, rotate) <= HandleSize)
                return new Cursor(StandardCursorType.Hand);
        }
        var d = ScreenToDesign(p);
        return DesignOps.HitTest(Design, d.X, d.Y) is { Locked: false } ? new Cursor(StandardCursorType.SizeAll) : null;
    }

    private static bool Intersects(DesignRect a, DesignRect b) =>
        a.X <= b.Right && b.X <= a.Right && a.Y <= b.Bottom && b.Y <= a.Bottom;

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
}

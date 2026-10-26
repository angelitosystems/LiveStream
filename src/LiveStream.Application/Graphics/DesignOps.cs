using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;

namespace LiveStream.Application.Graphics;

public enum DesignAlign { Left, CenterH, Right, Top, Middle, Bottom }

/// <summary>Rectángulo en coordenadas del diseño (relativas al origen del gráfico).</summary>
public readonly record struct DesignRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;
    public double CenterY => Y + Height / 2;

    public static DesignRect Union(IEnumerable<DesignRect> rects)
    {
        var list = rects.ToList();
        if (list.Count == 0)
            return default;
        var x = list.Min(r => r.X);
        var y = list.Min(r => r.Y);
        return new DesignRect(x, y, list.Max(r => r.Right) - x, list.Max(r => r.Bottom) - y);
    }
}

/// <summary>Guía de alineación: vertical (X constante) u horizontal (Y constante).</summary>
public readonly record struct SnapGuide(bool Vertical, double Position);

/// <summary>
/// Operaciones del editor visual sobre un diseño libre. Son puras (devuelven un diseño nuevo): la interfaz las usa
/// para el lienzo y el historial, y las pruebas las comprueban sin interfaz. La lista va de abajo (0) a arriba.
/// </summary>
public static class DesignOps
{
    public static string NewKey() => "e" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>Elemento nuevo de un tipo, centrado en <paramref name="at"/>, con estilo de la marca si se da.</summary>
    public static DesignElement Create(DesignElementKind kind, IReadOnlyList<DesignElement> design, (double X, double Y) at,
        GraphicStyle? style = null, string? path = null, DesignBinding binding = DesignBinding.None)
    {
        style ??= new GraphicStyle();
        var (w, h) = kind switch
        {
            DesignElementKind.Text => (600.0, 90.0),
            DesignElementKind.Rect => (400.0, 120.0),
            _ => (320.0, 180.0),
        };
        var label = (kind, binding) switch
        {
            (_, DesignBinding.Clock) => "Reloj",
            (_, DesignBinding.Countdown) => "Cuenta atrás",
            (DesignElementKind.Text, _) => "Texto",
            (DesignElementKind.Rect, _) => "Forma",
            (DesignElementKind.Image, _) => "Imagen",
            _ => "Video",
        };
        return new DesignElement(NewKey(), UniqueName(design, label), kind, Math.Round(at.X - w / 2), Math.Round(at.Y - h / 2), w, h,
            Color: kind == DesignElementKind.Rect ? style.PrimaryColor : style.TextColor,
            Text: binding switch
            {
                DesignBinding.Clock => "HH:mm:ss",
                DesignBinding.Countdown => "",
                _ => kind == DesignElementKind.Text ? "Texto" : "",
            },
            FontFace: style.FontFace, FontSize: 56, Bold: binding != DesignBinding.None,
            Align: binding == DesignBinding.None ? "left" : "center", Path: path, Binding: binding);
    }

    public static string UniqueName(IReadOnlyList<DesignElement> design, string baseName)
    {
        if (design.All(e => e.Name != baseName))
            return baseName;
        for (var i = 2; ; i++)
            if (design.All(e => e.Name != $"{baseName} {i}"))
                return $"{baseName} {i}";
    }

    /// <summary>
    /// Copia editable de una plantilla: sus piezas pasan a ser elementos libres y los textos que mostraban un campo
    /// quedan como «{campo}», así siguen cambiando con el formulario, los datos en vivo, el asistente y los plugins.
    /// </summary>
    public static Graphic FromTemplate(Graphic template, CanvasSettings canvas, string name)
    {
        if (template.Kind == GraphicKind.Custom)
            return template with { Id = Guid.NewGuid(), Name = name };
        var fieldKeys = GraphicTemplates.Fields(template.Kind).Select(f => f.Key).ToHashSet();
        var design = new List<DesignElement>();
        foreach (var p in GraphicTemplates.Layout(template, canvas).Elements)
        {
            var (x, y) = (Math.Round(p.X - template.X), Math.Round(p.Y - template.Y));
            var (w, h) = (Math.Max(1, Math.Round(p.Width)), Math.Max(1, Math.Round(p.Height)));
            design.Add(p.Kind switch
            {
                GraphicElementKind.Rect => new DesignElement(p.Key, p.Key, DesignElementKind.Rect, x, y, w, h, Color: p.Color),
                GraphicElementKind.Image => new DesignElement(p.Key, p.Key, DesignElementKind.Image, x, y,
                    w, h, Path: string.IsNullOrWhiteSpace(p.Text) ? null : p.Text, Visible: !string.IsNullOrWhiteSpace(p.Text)),
                _ => new DesignElement(p.Key, p.Key, DesignElementKind.Text, x, y, w, h, Color: p.Color,
                    Text: p.Key == "time" ? (template.Kind == GraphicKind.Clock ? GraphicTemplates.SafeFormat(template.Field("format")) : "")
                        : fieldKeys.Contains(p.Key) ? "{" + p.Key + "}" : p.Text,
                    FontFace: template.Style.FontFace, FontSize: Math.Max(4, Math.Round(p.FontSize)), Bold: p.Bold, Align: p.Align,
                    Binding: p.Key != "time" ? DesignBinding.None : template.Kind == GraphicKind.Clock ? DesignBinding.Clock : DesignBinding.Countdown),
            });
        }
        // Un logo vacío no puede ser una imagen sin archivo: se quita (se añade otro desde la biblioteca).
        design.RemoveAll(e => e.Kind == DesignElementKind.Image && e.Path is null);
        var fields = new Dictionary<string, string>(template.Fields);
        fields.Remove("logo");
        return template with
        {
            Id = Guid.NewGuid(), Name = name, Kind = GraphicKind.Custom, Fields = fields, Scale = 1, Design = design,
            X = Math.Round(template.X), Y = Math.Round(template.Y),
        };
    }

    /// <summary>Caja que ocupa un elemento girado (alrededor de su esquina superior izquierda).</summary>
    public static DesignRect Bounds(DesignElement e)
    {
        if (e.Rotation % 360 == 0)
            return new DesignRect(e.X, e.Y, e.Width, e.Height);
        var corners = Corners(e);
        var minX = corners.Min(c => c.X);
        var minY = corners.Min(c => c.Y);
        return new DesignRect(minX, minY, corners.Max(c => c.X) - minX, corners.Max(c => c.Y) - minY);
    }

    public static (double X, double Y)[] Corners(DesignElement e)
    {
        var a = e.Rotation * Math.PI / 180;
        var (cos, sin) = (Math.Cos(a), Math.Sin(a));
        (double, double) P(double lx, double ly) => (e.X + lx * cos - ly * sin, e.Y + lx * sin + ly * cos);
        return [P(0, 0), P(e.Width, 0), P(e.Width, e.Height), P(0, e.Height)];
    }

    /// <summary>Elemento visible más alto bajo el punto (teniendo en cuenta el giro); null si no hay.</summary>
    public static DesignElement? HitTest(IReadOnlyList<DesignElement> design, double x, double y)
    {
        for (var i = design.Count - 1; i >= 0; i--)
        {
            var e = design[i];
            if (!e.Visible)
                continue;
            var (lx, ly) = ToLocal(e, x, y);
            if (lx >= 0 && ly >= 0 && lx <= e.Width && ly <= e.Height)
                return e;
        }
        return null;
    }

    /// <summary>Punto del diseño en coordenadas del elemento (sin giro, origen en su esquina).</summary>
    public static (double X, double Y) ToLocal(DesignElement e, double x, double y)
    {
        var a = -e.Rotation * Math.PI / 180;
        var (dx, dy) = (x - e.X, y - e.Y);
        return (dx * Math.Cos(a) - dy * Math.Sin(a), dx * Math.Sin(a) + dy * Math.Cos(a));
    }

    public static IReadOnlyList<DesignElement> Update(IReadOnlyList<DesignElement> design, IEnumerable<string> keys,
        Func<DesignElement, DesignElement> change)
    {
        var set = keys.ToHashSet();
        return design.Select(e => set.Contains(e.Key) ? change(e) : e).ToList();
    }

    /// <summary>Mueve los elementos no bloqueados.</summary>
    public static IReadOnlyList<DesignElement> Move(IReadOnlyList<DesignElement> design, IEnumerable<string> keys, double dx, double dy) =>
        Update(design, keys, e => e.Locked ? e : e with { X = Math.Round(e.X + dx), Y = Math.Round(e.Y + dy) });

    /// <summary>
    /// Alinea la selección: con un solo elemento, respecto a <paramref name="reference"/> (p. ej. el lienzo);
    /// con varios, respecto a la caja que ocupan juntos.
    /// </summary>
    public static IReadOnlyList<DesignElement> Align(IReadOnlyList<DesignElement> design, IReadOnlyCollection<string> keys, DesignAlign mode,
        DesignRect reference)
    {
        var selected = design.Where(e => keys.Contains(e.Key)).ToList();
        if (selected.Count == 0)
            return design;
        var box = selected.Count == 1 ? reference : DesignRect.Union(selected.Select(Bounds));
        return Update(design, keys, e =>
        {
            if (e.Locked)
                return e;
            var b = Bounds(e);
            var (dx, dy) = mode switch
            {
                DesignAlign.Left => (box.X - b.X, 0.0),
                DesignAlign.CenterH => (box.CenterX - b.CenterX, 0.0),
                DesignAlign.Right => (box.Right - b.Right, 0.0),
                DesignAlign.Top => (0.0, box.Y - b.Y),
                DesignAlign.Middle => (0.0, box.CenterY - b.CenterY),
                _ => (0.0, box.Bottom - b.Bottom),
            };
            return e with { X = Math.Round(e.X + dx), Y = Math.Round(e.Y + dy) };
        });
    }

    /// <summary>Reparte la selección (3 o más) dejando el mismo espacio entre elementos; los extremos no se mueven.</summary>
    public static IReadOnlyList<DesignElement> Distribute(IReadOnlyList<DesignElement> design, IReadOnlyCollection<string> keys, bool horizontal)
    {
        var selected = design.Where(e => keys.Contains(e.Key) && !e.Locked)
            .OrderBy(e => horizontal ? Bounds(e).X : Bounds(e).Y).ToList();
        if (selected.Count < 3)
            return design;
        double Start(DesignElement e) => horizontal ? Bounds(e).X : Bounds(e).Y;
        double Size(DesignElement e) => horizontal ? Bounds(e).Width : Bounds(e).Height;
        var first = selected[0];
        var last = selected[^1];
        var gap = (Start(last) + Size(last) - Start(first) - selected.Sum(Size)) / (selected.Count - 1);
        var positions = new Dictionary<string, double>();
        var cursor = Start(first);
        foreach (var e in selected)
        {
            positions[e.Key] = cursor;
            cursor += Size(e) + gap;
        }
        return Update(design, positions.Keys, e =>
        {
            var delta = Math.Round(positions[e.Key] - Start(e));
            return horizontal ? e with { X = e.X + delta } : e with { Y = e.Y + delta };
        });
    }

    /// <summary>
    /// Ajusta un desplazamiento para que la selección encaje con bordes y centros de otros elementos y de
    /// <paramref name="canvas"/>; si no hay guía cerca y hay cuadrícula, encaja en la cuadrícula.
    /// </summary>
    public static (double Dx, double Dy, IReadOnlyList<SnapGuide> Guides) Snap(IReadOnlyList<DesignElement> design,
        IReadOnlyCollection<string> moving, double dx, double dy, DesignRect canvas, double threshold, double grid)
    {
        var selected = design.Where(e => moving.Contains(e.Key)).ToList();
        if (selected.Count == 0)
            return (dx, dy, []);
        var box = DesignRect.Union(selected.Select(Bounds));
        var others = design.Where(e => e.Visible && !moving.Contains(e.Key)).Select(Bounds).Append(canvas).ToList();
        var xs = others.SelectMany(r => new[] { r.X, r.CenterX, r.Right }).ToList();
        var ys = others.SelectMany(r => new[] { r.Y, r.CenterY, r.Bottom }).ToList();
        var guides = new List<SnapGuide>();

        double Best(double delta, double[] edges, List<double> targets, bool vertical, double origin)
        {
            var best = (Distance: threshold + 1, Delta: delta, Target: 0.0);
            foreach (var edge in edges)
                foreach (var target in targets)
                {
                    var d = Math.Abs(edge + delta - target);
                    if (d <= threshold && d < best.Distance)
                        best = (d, target - edge, target);
                }
            if (best.Distance <= threshold)
            {
                guides.Add(new SnapGuide(vertical, best.Target));
                return best.Delta;
            }
            // Sin guía cerca: la esquina de la selección cae en la cuadrícula.
            return grid > 0 ? Math.Round((origin + delta) / grid) * grid - origin : delta;
        }

        var sx = Best(dx, [box.X, box.CenterX, box.Right], xs, vertical: true, box.X);
        var sy = Best(dy, [box.Y, box.CenterY, box.Bottom], ys, vertical: false, box.Y);
        return (Math.Round(sx), Math.Round(sy), guides);
    }

    /// <summary>Sube (+) o baja (−) los elementos un puesto en la pila de capas.</summary>
    public static IReadOnlyList<DesignElement> Reorder(IReadOnlyList<DesignElement> design, IReadOnlyCollection<string> keys, int direction)
    {
        var list = design.ToList();
        var indices = Enumerable.Range(0, list.Count).Where(i => keys.Contains(list[i].Key)).ToList();
        if (direction > 0)
            indices.Reverse();
        foreach (var i in indices)
        {
            var j = i + Math.Sign(direction);
            if (j < 0 || j >= list.Count || keys.Contains(list[j].Key))
                continue;
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }

    /// <summary>Lleva los elementos arriba del todo (o abajo del todo) conservando su orden relativo.</summary>
    public static IReadOnlyList<DesignElement> ToEdge(IReadOnlyList<DesignElement> design, IReadOnlyCollection<string> keys, bool front)
    {
        var picked = design.Where(e => keys.Contains(e.Key));
        var rest = design.Where(e => !keys.Contains(e.Key));
        return (front ? rest.Concat(picked) : picked.Concat(rest)).ToList();
    }

    /// <summary>Copia los elementos encima de los originales, desplazados 20 px. Devuelve el diseño y las claves nuevas.</summary>
    public static (IReadOnlyList<DesignElement> Design, IReadOnlyList<string> Keys) Duplicate(IReadOnlyList<DesignElement> design,
        IReadOnlyCollection<string> keys)
    {
        var list = design.ToList();
        var created = new List<string>();
        foreach (var e in design.Where(e => keys.Contains(e.Key)))
        {
            var copy = e with { Key = NewKey(), Name = UniqueName(list, e.Name + " copia"), X = e.X + 20, Y = e.Y + 20, Locked = false };
            list.Add(copy);
            created.Add(copy.Key);
        }
        return (list.Count > DesignElement.MaxElements ? design : list, list.Count > DesignElement.MaxElements ? [] : created);
    }
}

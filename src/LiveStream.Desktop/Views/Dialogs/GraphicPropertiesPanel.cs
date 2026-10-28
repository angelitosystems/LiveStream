using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using LiveStream.Application.Graphics;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.Graphics;
using LiveStream.Infrastructure.Persistence;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>
/// Panel de propiedades del editor de diseño. Edita un solo elemento (posición, tamaño, giro, opacidad,
/// contenido, texto vivo, tipografía, efectos, archivo y recorte) y la presentación del gráfico entero (nombre,
/// posición y escala en el lienzo, animaciones, salida automática y datos en vivo). Cada cambio se envía con el
/// delegado que recorre el elemento (<paramref name="editElement"/>) o que reemplaza el gráfico (<paramref
/// name="editGraphic"/>); la ventana lo traduce en un paso del historial de deshacer.
/// </summary>
public sealed class GraphicPropertiesPanel : StackPanel
{
    private readonly MainViewModel _vm;
    private readonly DesignElement _element;
    private readonly Graphic _graphic;
    private readonly Func<DesignElement, DesignElement> _editElement;
    private readonly Func<Graphic, Graphic> _editGraphic;

    public GraphicPropertiesPanel(MainViewModel vm, DesignElement element, Graphic graphic,
        Func<DesignElement, DesignElement> editElement, Func<Graphic, Graphic> editGraphic)
    {
        _vm = vm;
        _element = element;
        _graphic = graphic;
        _editElement = editElement;
        _editGraphic = editGraphic;
        Spacing = 10;
        Build();
    }

    private void Edit(Func<DesignElement, DesignElement> change) => _editElement(change(_element));

    private void Build()
    {
        Children.Add(new TextBlock
        {
            Text = _element.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontWeight = FontWeight.SemiBold,
        });
        BuildCommon();
        switch (_element.Kind)
        {
            case DesignElementKind.Text:
                BuildText();
                break;
            case DesignElementKind.Image or DesignElementKind.Video:
                BuildMedia();
                break;
        }
        BuildPresentation();
    }

    // ---- Comunes --------------------------------------------------------------------------------

    private void BuildCommon()
    {
        var name = new TextBox { Text = _element.Name, MinWidth = 150 };
        Submit(name, () =>
        {
            var value = name.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(value) && value != _element.Name)
                Edit(e => e with { Name = value });
        });
        Children.Add(Value("Nombre", name));

        var x = Number(_element.X, -20_000, 20_000, 1);
        x.ValueChanged += (_, _) => { if (x.Value is { } v) Edit(e => e with { X = (double)v }); };
        var y = Number(_element.Y, -20_000, 20_000, 1);
        y.ValueChanged += (_, _) => { if (y.Value is { } v) Edit(e => e with { Y = (double)v }); };
        Children.Add(Value("Posición", Pair(x, y)));

        var w = Number(_element.Width, 1, 20_000, 1);
        w.ValueChanged += (_, _) => { if (w.Value is { } v) Edit(e => e with { Width = (double)v }); };
        var h = Number(_element.Height, 1, 20_000, 1);
        h.ValueChanged += (_, _) => { if (h.Value is { } v) Edit(e => e with { Height = (double)v }); };
        Children.Add(Value("Tamaño", Pair(w, h)));

        var rotation = Number(_element.Rotation, -360, 360, 1);
        rotation.ValueChanged += (_, _) => { if (rotation.Value is { } v) Edit(e => e with { Rotation = (double)v }); };
        Children.Add(Value("Giro (°)", rotation));

        var opacity = Number(_element.Opacity, 0, 1, 0.05, "0.00");
        opacity.ValueChanged += (_, _) => { if (opacity.Value is { } v) Edit(e => e with { Opacity = Math.Clamp((double)v, 0, 1) }); };
        Children.Add(Value("Opacidad", opacity));

        var visible = new CheckBox { IsChecked = _element.Visible, Content = "Visible" };
        visible.Click += (_, _) => Edit(e => e with { Visible = visible.IsChecked == true });
        var locked = new CheckBox { IsChecked = _element.Locked, Content = "Bloqueado" };
        locked.Click += (_, _) => Edit(e => e with { Locked = locked.IsChecked == true });
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { visible, locked } });
    }

    private void BuildText()
    {
        var content = new TextBox
        {
            Text = _element.Text,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 80,
            MinWidth = 150,
        };
        var caption = _element.Binding == DesignBinding.Clock
            ? "Formato de hora"
            : _element.Binding == DesignBinding.Countdown ? "Texto al terminar" : "Contenido ({campo})";
        Submit(content, () =>
        {
            var value = content.Text ?? "";
            if (value != _element.Text)
                Edit(e => e with { Text = value });
        });
        Children.Add(Value(caption, content));

        var binding = new ComboBox
        {
            ItemsSource = new List<string> { "Normal", "Reloj", "Cuenta atrás" },
            SelectedIndex = _element.Binding switch { DesignBinding.Clock => 1, DesignBinding.Countdown => 2, _ => 0 },
            MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        binding.SelectionChanged += (_, _) => Edit(e => e with
        {
            Binding = binding.SelectedIndex switch { 1 => DesignBinding.Clock, 2 => DesignBinding.Countdown, _ => DesignBinding.None },
        });
        Children.Add(Value("Texto vivo ({campo})", binding));

        var fonts = Fonts();
        var font = new ComboBox
        {
            ItemsSource = fonts.ToList(),
            SelectedIndex = Math.Max(0, fonts.IndexOf(_element.FontFace ?? "Segoe UI")),
            MinWidth = 150,
        };
        font.SelectionChanged += (_, _) =>
        {
            if (font.SelectedItem is { } value)
                Edit(e => e with { FontFace = value.ToString()! });
        };
        Children.Add(Value("Tipografía", font));

        var size = Number(_element.FontSize, 4, 1000, 2);
        size.ValueChanged += (_, _) => { if (size.Value is { } v) Edit(e => e with { FontSize = (double)v }); };
        Children.Add(Value("Tamaño de letra", size));

        var bold = new CheckBox { IsChecked = _element.Bold, Content = "Negrita" };
        bold.Click += (_, _) => Edit(e => e with { Bold = bold.IsChecked == true });
        var italic = new CheckBox { IsChecked = _element.Italic, Content = "Cursiva" };
        italic.Click += (_, _) => Edit(e => e with { Italic = italic.IsChecked == true });
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { bold, italic } });

        var align = new ComboBox
        {
            ItemsSource = new List<string> { "Izquierda", "Centro", "Derecha" },
            SelectedIndex = _element.Align switch { "center" => 1, "right" => 2, _ => 0 },
            MinWidth = 110,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        align.SelectionChanged += (_, _) =>
            Edit(e => e with { Align = align.SelectedIndex switch { 1 => "center", 2 => "right", _ => "left" } });
        Children.Add(Value("Alineación", align));

        AddColor("Color", _element.Color, value => Edit(e => e with { Color = value }));
        AddColor("Degradado", _element.GradientColor ?? "#00000000",
            value => Edit(e => e with { GradientColor = Transparent(value) ? null : value }));
        AddColor("Contorno", _element.OutlineColor ?? "#00000000",
            value => Edit(e => e with { OutlineColor = Transparent(value) ? null : value }));
        var outline = Number(_element.OutlineSize, 0, 50, 1);
        outline.ValueChanged += (_, _) => { if (outline.Value is { } v) Edit(e => e with { OutlineSize = (int)v }); };
        Children.Add(Value("Grosor de contorno", outline));
        Children.Add(Muted("Un color con alfa 0 desactiva el degradado o el contorno."));

        AddColor("Sombra", _element.ShadowColor ?? "#00000000",
            value => Edit(e => e with { ShadowColor = Transparent(value) ? null : value }));
        var shadowX = Number(_element.ShadowX, -10_000, 10_000, 1);
        shadowX.ValueChanged += (_, _) => { if (shadowX.Value is { } v) Edit(e => e with { ShadowX = (double)v }); };
        var shadowY = Number(_element.ShadowY, -10_000, 10_000, 1);
        shadowY.ValueChanged += (_, _) => { if (shadowY.Value is { } v) Edit(e => e with { ShadowY = (double)v }); };
        Children.Add(Value("Sombra →x ↓y", Pair(shadowX, shadowY)));
    }

    private void BuildMedia()
    {
        var path = new TextBox
        {
            Text = _element.Path ?? "",
            MinWidth = 150,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 60,
        };
        var browse = new Button { Content = "Cambiar archivo…" };
        browse.Click += async (_, _) =>
        {
            var kind = _element.Kind == DesignElementKind.Video ? ResourceKind.Video : ResourceKind.Image;
            var picked = await _vm.Dialogs.PickFileAsync(kind == ResourceKind.Image ? "Cambiar imagen" : "Cambiar video",
                kind == ResourceKind.Image ? ResourceLibrary.ImageFilter : ResourceLibrary.VideoFilter);
            if (picked is not null)
                Edit(e => e with { Path = picked });
        };
        Children.Add(Value("Archivo", new StackPanel { Children = { path, browse }, Spacing = 4 }));

        if (_element.Kind == DesignElementKind.Image)
        {
            var crop = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 4, MinWidth = 170 };
            AddCrop(crop, 0, _element.CropLeft, (e, v) => e with { CropLeft = v });
            AddCrop(crop, 1, _element.CropTop, (e, v) => e with { CropTop = v });
            AddCrop(crop, 2, _element.CropRight, (e, v) => e with { CropRight = v });
            AddCrop(crop, 3, _element.CropBottom, (e, v) => e with { CropBottom = v });
            Children.Add(Value("Recorte I·T·D·B", crop));
        }
        else
        {
            var muted = new CheckBox { IsChecked = _element.Muted, Content = "Silenciado" };
            muted.Click += (_, _) => Edit(e => e with { Muted = muted.IsChecked == true });
            Children.Add(muted);
            Children.Add(Muted("El video se reproduce en bucle; mantener el audio depende de esta casilla."));
        }
    }

    private void AddCrop(Grid grid, int column, int value, Func<DesignElement, int, DesignElement> change)
    {
        var box = Number(value, 0, 10_000, 1);
        box.ValueChanged += (_, _) => { if (box.Value is { } v) Edit(e => change(e, (int)v)); };
        Grid.SetColumn(box, column);
        grid.Children.Add(box);
    }

    private void BuildPresentation()
    {
        Children.Add(new TextBlock
        {
            Text = "Presentación del gráfico",
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 8, 0, 0),
        });

        var name = new TextBox { Text = _graphic.Name, MinWidth = 150 };
        Submit(name, () =>
        {
            var value = name.Text?.Trim();
            if (!string.IsNullOrWhiteSpace(value) && value != _graphic.Name)
                _editGraphic(_graphic with { Name = value });
        });
        Children.Add(Value("Nombre", name));

        var gx = Number(_graphic.X, -20_000, 20_000, 1);
        var gy = Number(_graphic.Y, -20_000, 20_000, 1);
        void Position()
        {
            var (nx, ny) = ((double?)gx.Value ?? _graphic.X, (double?)gy.Value ?? _graphic.Y);
            if (nx != _graphic.X || ny != _graphic.Y)
                _editGraphic(_graphic with { X = nx, Y = ny });
        }
        gx.LostFocus += (_, _) => Position();
        gy.LostFocus += (_, _) => Position();
        Children.Add(Value("Posición (lienzo)", Pair(gx, gy)));

        var scale = Number(_graphic.Scale * 100, 10, 500, 5);
        scale.LostFocus += (_, _) =>
        {
            if (scale.Value is { } v)
            {
                var value = (double)v / 100.0;
                if (Math.Abs(value - _graphic.Scale) > 0.0005)
                    _editGraphic(_graphic with { Scale = value });
            }
        };
        Children.Add(Value("Escala (%)", scale));

        var names = GraphicDesignerDialog.AnimationNames().Select(a => a.Value).ToList();
        var animIn = new ComboBox { ItemsSource = names.ToList(), SelectedIndex = Math.Max(0, names.IndexOf(NameOf(_graphic.AnimationIn))), MinWidth = 150 };
        var animOut = new ComboBox { ItemsSource = names.ToList(), SelectedIndex = Math.Max(0, names.IndexOf(NameOf(_graphic.AnimationOut))), MinWidth = 150 };
        animIn.SelectionChanged += (_, _) =>
        {
            if (animIn.SelectedItem is { } value && NameOf(_graphic.AnimationIn) != (string)value)
                _editGraphic(_graphic with { AnimationIn = FromName((string)value) });
        };
        animOut.SelectionChanged += (_, _) =>
        {
            if (animOut.SelectedItem is { } value && NameOf(_graphic.AnimationOut) != (string)value)
                _editGraphic(_graphic with { AnimationOut = FromName((string)value) });
        };
        Children.Add(Value("Entrada", animIn));
        Children.Add(Value("Salida", animOut));

        var animMs = Number(_graphic.AnimationMs, 0, 10_000, 50);
        animMs.LostFocus += (_, _) =>
        {
            if (animMs.Value is { } v && (double)v != _graphic.AnimationMs)
                _editGraphic(_graphic with { AnimationMs = (int)v });
        };
        Children.Add(Value("Duración (ms)", animMs));

        var autoHide = Number(_graphic.AutoHideSeconds, 0, 3600, 1, "0.#");
        autoHide.LostFocus += (_, _) =>
        {
            if (autoHide.Value is { } v && (double)v != _graphic.AutoHideSeconds)
                _editGraphic(_graphic with { AutoHideSeconds = (double)v });
        };
        Children.Add(Value("Salida automática (s, 0 = manual)", autoHide));

        var dataFile = new TextBox
        {
            Text = _graphic.DataFile ?? "",
            MinWidth = 150,
            TextWrapping = TextWrapping.Wrap,
        };
        Submit(dataFile, () =>
        {
            var value = dataFile.Text?.Trim();
            var applied = value is { Length: > 0 } ? value : null;
            if (!string.Equals(applied, _graphic.DataFile, StringComparison.OrdinalIgnoreCase))
                _editGraphic(_graphic with { DataFile = applied });
        });
        var browse = new Button
        {
            Content = "Examinar…",
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        browse.Click += async (_, _) =>
        {
            if (await _vm.Dialogs.PickFileAsync("Archivo de datos JSON", "Datos JSON (*.json);;Todos (*.*)") is { } path)
            {
                dataFile.Text = path;
                _editGraphic(_graphic with { DataFile = path });
            }
        };
        Children.Add(Value("Datos en vivo", new StackPanel { Children = { dataFile, browse }, Spacing = 4 }));
        Children.Add(Muted("Enlaza un archivo JSON con los mismos nombres de campo (p. ej. {\"titulo\": \"…\"}); el gráfico se actualiza en directo cuando otro programa lo guarda."));
    }

    // ---- Controles ------------------------------------------------------------------------------

    private static TextBlock Muted(string text) => new() { Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 11 };

    private static Control Value(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), ColumnSpacing = 10 };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static Control Pair(Control left, Control right)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        left.HorizontalAlignment = HorizontalAlignment.Stretch;
        right.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return grid;
    }

    private static NumericUpDown Number(double value, double min, double max, double step, string format = "0") =>
        new()
        {
            Value = (decimal)value,
            Minimum = (decimal)min,
            Maximum = (decimal)max,
            Increment = (decimal)step,
            FormatString = format,
            MinWidth = 50,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

    /// <summary>Aplica al pulsar Enter o al salir del campo (cada aplicación es un paso de deshacer).</summary>
    private static void Submit(TextBox box, Action apply)
    {
        box.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter)
            {
                args.Handled = true;
                apply();
            }
        };
        box.LostFocus += (_, _) => apply();
    }

    private static List<string> Fonts() =>
        [.. FontManager.Current.SystemFonts.Select(f => f.Name).OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)];

    /// <summary>Selector de color: cada cambio se aplica (los arrastres generan pasos propios, como en el lienzo).</summary>
    private void AddColor(string label, string hex, Action<string> apply)
    {
        var picker = new ColorPicker
        {
            Color = Color.TryParse(hex, out var parsed) ? parsed : Colors.White,
            IsAlphaEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        picker.ColorChanged += (_, _) =>
        {
            var value = Hex(picker.Color);
            if (!string.Equals(value, hex, StringComparison.OrdinalIgnoreCase))
                apply(value);
        };
        Children.Add(Value(label, picker));
    }

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    private static bool Transparent(string hex) => Color.TryParse(hex, out var c) && c.A == 0;

    private static string NameOf(GraphicAnimation animation) =>
        GraphicDesignerDialog.AnimationNames().FirstOrDefault(a => a.Key == animation).Value ?? "Corte";

    private static GraphicAnimation FromName(string name) =>
        GraphicDesignerDialog.AnimationNames().FirstOrDefault(a => string.Equals(a.Value, name, StringComparison.CurrentCulture)).Key;
}

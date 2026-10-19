using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using LiveStream.Application.Abstractions;
using LiveStream.Desktop.Services;

namespace LiveStream.Desktop.Components;

/// <summary>
/// Editor genérico de propiedades de fuente. Los controles se generan a partir de la descripción que da libobs
/// (<see cref="SourceProperty"/>) y editan un objeto JSON con los ajustes. Cada cambio dispara
/// <see cref="SettingsChanged"/>; los cambios estructurales (listas, casillas) piden además reconstruir el
/// formulario porque libobs puede mostrar u ocultar propiedades según su valor.
/// </summary>
public sealed class PropertyEditor : ContentControl
{
    private JsonObject _settings = [];
    private DialogService? _dialogs;

    public event Action<JsonObject, bool>? SettingsChanged;

    public JsonObject Settings => _settings;

    public void Build(IReadOnlyList<SourceProperty> properties, JsonObject settings, DialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
        var panel = new StackPanel { Spacing = 10 };
        foreach (var property in properties)
            if (CreateRow(property) is { } row)
                panel.Children.Add(row);
        if (panel.Children.Count == 0)
            panel.Children.Add(new TextBlock { Text = "Esta fuente no tiene propiedades editables.", Classes = { "muted" } });
        Content = panel;
    }

    private void Changed(bool structural) => SettingsChanged?.Invoke(_settings, structural);

    private Control? CreateRow(SourceProperty p)
    {
        var editor = CreateEditor(p);
        if (editor is null)
            return null;
        editor.IsEnabled = p.Enabled;
        if (p.Tooltip is { Length: > 0 } tip)
            ToolTip.SetTip(editor, tip);
        if (p.Type is SourcePropertyType.Bool or SourcePropertyType.Group || p.Info)
            return editor;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("180,*"), ColumnSpacing = 12 };
        var label = new TextBlock { Text = p.Description, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        grid.Children.Add(label);
        Grid.SetColumn(editor, 1);
        grid.Children.Add(editor);
        return grid;
    }

    private Control? CreateEditor(SourceProperty p) => p.Type switch
    {
        SourcePropertyType.Bool => BoolEditor(p),
        SourcePropertyType.Int or SourcePropertyType.Float => NumberEditor(p),
        SourcePropertyType.Text => TextEditor(p),
        SourcePropertyType.Path => PathEditor(p),
        SourcePropertyType.List => ListEditor(p),
        SourcePropertyType.Color or SourcePropertyType.ColorAlpha => ColorEditor(p),
        SourcePropertyType.Font => FontEditor(p),
        SourcePropertyType.EditableList => FileListEditor(p),
        SourcePropertyType.Group => GroupEditor(p),
        // Los botones de libobs ejecutan callbacks nativos (p. ej. el diálogo del driver de la cámara) que el puente
        // aún no expone, y la velocidad de fotogramas usa el valor del dispositivo. No se muestran controles inertes.
        _ => null,
    };

    private Control BoolEditor(SourceProperty p)
    {
        var box = new CheckBox { Content = p.Description, IsChecked = _settings[p.Name]?.GetValueKind() == JsonValueKind.True };
        box.IsCheckedChanged += (_, _) =>
        {
            _settings[p.Name] = box.IsChecked == true;
            Changed(true);
        };
        return box;
    }

    private Control NumberEditor(SourceProperty p)
    {
        var isInt = p.Type == SourcePropertyType.Int;
        var value = _settings[p.Name] is JsonValue v && v.TryGetValue<double>(out var d) ? d : p.Min ?? 0;
        var number = new NumericUpDown
        {
            Minimum = (decimal)(p.Min ?? -1e9),
            Maximum = (decimal)(p.Max ?? 1e9),
            Increment = (decimal)(p.Step is > 0 ? p.Step.Value : 1),
            Value = (decimal)value,
            FormatString = isInt ? "0" : "0.###",
            MinWidth = 120,
        };
        void Store(double x)
        {
            _settings[p.Name] = isInt ? JsonValue.Create((long)Math.Round(x)) : JsonValue.Create(x);
            Changed(false);
        }

        if (!p.Slider)
        {
            number.ValueChanged += (_, e) =>
            {
                if (e.NewValue is { } n)
                    Store((double)n);
            };
            return number;
        }

        var slider = new Slider { Minimum = p.Min ?? 0, Maximum = p.Max ?? 100, Value = value, SmallChange = p.Step ?? 1 };
        var syncing = false;
        slider.ValueChanged += (_, e) =>
        {
            if (syncing) return;
            syncing = true;
            number.Value = (decimal)e.NewValue;
            syncing = false;
            Store(e.NewValue);
        };
        number.ValueChanged += (_, e) =>
        {
            if (syncing || e.NewValue is not { } n) return;
            syncing = true;
            slider.Value = (double)n;
            syncing = false;
            Store((double)n);
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,110"), ColumnSpacing = 8 };
        grid.Children.Add(slider);
        Grid.SetColumn(number, 1);
        grid.Children.Add(number);
        return grid;
    }

    private Control TextEditor(SourceProperty p)
    {
        var text = _settings[p.Name]?.GetValue<string>() ?? string.Empty;
        if (p.Info)
            return new TextBlock { Text = string.IsNullOrEmpty(text) ? p.Description : $"{p.Description}: {text}", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } };
        var box = new TextBox
        {
            Text = text,
            AcceptsReturn = p.Multiline,
            TextWrapping = p.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            MinHeight = p.Multiline ? 80 : 0,
            PasswordChar = p.Password ? '•' : default,
        };
        box.TextChanged += (_, _) =>
        {
            _settings[p.Name] = box.Text ?? string.Empty;
            Changed(false);
        };
        return box;
    }

    private Control PathEditor(SourceProperty p)
    {
        var box = new TextBox { Text = _settings[p.Name]?.GetValue<string>() ?? string.Empty };
        box.TextChanged += (_, _) =>
        {
            _settings[p.Name] = box.Text ?? string.Empty;
            Changed(false);
        };
        var browse = new Button { Content = "Examinar…" };
        browse.Click += async (_, _) =>
        {
            if (_dialogs is null) return;
            var path = p.PathType == "directory"
                ? await _dialogs.PickFolderAsync(p.Description)
                : await _dialogs.PickFileAsync(p.Description, p.Filter);
            if (path is not null)
                box.Text = path.Replace('\\', '/');
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        grid.Children.Add(box);
        Grid.SetColumn(browse, 1);
        grid.Children.Add(browse);
        return grid;
    }

    private Control ListEditor(SourceProperty p)
    {
        var options = p.Options ?? [];
        var current = _settings[p.Name];
        var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch, ItemsSource = options.Select(o => o.Name).ToList() };
        var index = options.ToList().FindIndex(o => SameValue(o.Value, current));
        combo.SelectedIndex = index;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= options.Count) return;
            var option = options[combo.SelectedIndex];
            if (option.Disabled) return;
            _settings[p.Name] = option.Value switch
            {
                long l => JsonValue.Create(l),
                double d => JsonValue.Create(d),
                bool b => JsonValue.Create(b),
                string s => JsonValue.Create(s),
                _ => null,
            };
            Changed(true);
        };
        return combo;
    }

    private static bool SameValue(object? option, JsonNode? current) => current switch
    {
        null => false,
        JsonValue v when option is string s => v.TryGetValue<string>(out var cs) && cs == s,
        JsonValue v when option is long l => v.TryGetValue<double>(out var cd) && Math.Abs(cd - l) < 1e-9,
        JsonValue v when option is double d => v.TryGetValue<double>(out var cd) && Math.Abs(cd - d) < 1e-9,
        JsonValue v when option is bool b => v.TryGetValue<bool>(out var cb) && cb == b,
        _ => false,
    };

    /// <summary>libobs guarda los colores como entero 0xAABBGGRR.</summary>
    private Control ColorEditor(SourceProperty p)
    {
        var raw = _settings[p.Name] is JsonValue v && v.TryGetValue<long>(out var l) ? (uint)l : 0xFFFFFFFF;
        var withAlpha = p.Type == SourcePropertyType.ColorAlpha;
        var picker = new ColorPicker
        {
            Color = Color.FromArgb(withAlpha ? (byte)(raw >> 24) : (byte)255, (byte)raw, (byte)(raw >> 8), (byte)(raw >> 16)),
            IsAlphaEnabled = withAlpha,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        picker.ColorChanged += (_, e) =>
        {
            var c = e.NewColor;
            var a = withAlpha ? c.A : (byte)255;
            _settings[p.Name] = (long)((uint)a << 24 | (uint)c.B << 16 | (uint)c.G << 8 | c.R);
            Changed(false);
        };
        return picker;
    }

    /// <summary>Fuente tipográfica de libobs: {face, size, style, flags} (flags: 1 negrita, 2 cursiva, 4 subrayado, 8 tachado).</summary>
    private Control FontEditor(SourceProperty p)
    {
        var font = _settings[p.Name] as JsonObject ?? new JsonObject { ["face"] = "Arial", ["size"] = 72, ["flags"] = 0, ["style"] = "" };
        _settings[p.Name] = font;
        var flags = font["flags"]?.GetValue<int>() ?? 0;

        var faces = FontManager.Current.SystemFonts.Select(f => f.Name).OrderBy(n => n).ToList();
        var face = new ComboBox { ItemsSource = faces, SelectedItem = faces.FirstOrDefault(f => f == font["face"]?.GetValue<string>()), MinWidth = 180 };
        var size = new NumericUpDown { Minimum = 4, Maximum = 512, Value = font["size"]?.GetValue<int>() ?? 72, FormatString = "0", Width = 110 };
        var bold = new ToggleButton { Content = "N", FontWeight = FontWeight.Bold, IsChecked = (flags & 1) != 0 };
        var italic = new ToggleButton { Content = "K", FontStyle = FontStyle.Italic, IsChecked = (flags & 2) != 0 };
        ToolTip.SetTip(bold, "Negrita");
        ToolTip.SetTip(italic, "Cursiva");

        void Store()
        {
            font["face"] = face.SelectedItem as string ?? "Arial";
            font["size"] = (int)(size.Value ?? 72);
            font["flags"] = ((font["flags"]?.GetValue<int>() ?? 0) & ~3) | (bold.IsChecked == true ? 1 : 0) | (italic.IsChecked == true ? 2 : 0);
            font["style"] = bold.IsChecked == true ? (italic.IsChecked == true ? "Bold Italic" : "Bold") : italic.IsChecked == true ? "Italic" : "Regular";
            Changed(false);
        }

        face.SelectionChanged += (_, _) => Store();
        size.ValueChanged += (_, _) => Store();
        bold.IsCheckedChanged += (_, _) => Store();
        italic.IsCheckedChanged += (_, _) => Store();
        return new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { face, size, bold, italic } };
    }

    /// <summary>Lista de archivos (p. ej. la presentación de imágenes): [{value, selected, hidden}].</summary>
    private Control FileListEditor(SourceProperty p)
    {
        var array = _settings[p.Name] as JsonArray ?? [];
        _settings[p.Name] = array;
        var list = new ListBox { Height = 120 };
        void Reload() => list.ItemsSource = array.Select(n => n?["value"]?.GetValue<string>() ?? "").ToList();
        Reload();

        var add = new Button { Content = "Añadir archivo…" };
        add.Click += async (_, _) =>
        {
            if (_dialogs is null || await _dialogs.PickFileAsync(p.Description, p.Filter) is not { } path) return;
            array.Add(new JsonObject { ["value"] = path.Replace('\\', '/'), ["selected"] = false, ["hidden"] = false });
            Reload();
            Changed(false);
        };
        var addFolder = new Button { Content = "Añadir carpeta…" };
        addFolder.Click += async (_, _) =>
        {
            if (_dialogs is null || await _dialogs.PickFolderAsync(p.Description) is not { } path) return;
            array.Add(new JsonObject { ["value"] = path.Replace('\\', '/'), ["selected"] = false, ["hidden"] = false });
            Reload();
            Changed(false);
        };
        var remove = new Button { Content = "Quitar" };
        remove.Click += (_, _) =>
        {
            if (list.SelectedIndex < 0) return;
            array.RemoveAt(list.SelectedIndex);
            Reload();
            Changed(false);
        };
        return new StackPanel { Spacing = 6, Children = { list, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { add, addFolder, remove } } } };
    }

    private Control GroupEditor(SourceProperty p)
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(12, 4, 0, 0) };
        foreach (var child in p.Children ?? [])
            if (CreateRow(child) is { } row)
                panel.Children.Add(row);
        return new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = p.Description, Classes = { "section" } }, panel },
        };
    }
}

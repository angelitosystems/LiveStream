using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using LiveStream.Application.Filters;
using LiveStream.Application.Graphics;
using LiveStream.Application.Scenes;
using LiveStream.Desktop.Components;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using static LiveStream.Desktop.Views.Dialogs.StudioDialogs;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>Diálogos de producción: gráficos, plantillas, filtros y transiciones. Todo sale como comandos.</summary>
public static class ProductionDialogs
{
    private static readonly IReadOnlyDictionary<GraphicAnimation, string> AnimationNames = new Dictionary<GraphicAnimation, string>
    {
        [GraphicAnimation.Cut] = "Corte",
        [GraphicAnimation.Fade] = "Fundido",
        [GraphicAnimation.SlideFromLeft] = "Deslizar desde la izquierda",
        [GraphicAnimation.SlideFromRight] = "Deslizar desde la derecha",
        [GraphicAnimation.SlideFromBottom] = "Deslizar desde abajo",
        [GraphicAnimation.SlideFromTop] = "Deslizar desde arriba",
        [GraphicAnimation.Wipe] = "Cortinilla (revelado)",
        [GraphicAnimation.Pop] = "Aparición con rebote",
    };

    private static TextBlock Label(string text) => new() { Text = text, VerticalAlignment = VerticalAlignment.Center };

    private static TextBlock Error() => new() { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };

    private static TextBlock Section(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 8, 0, 0) };

    /// <summary>Rejilla etiqueta/control de dos columnas.</summary>
    private static Grid Form(params (string Label, Control Control)[] rows)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("230,*"), ColumnSpacing = 12, RowSpacing = 8 };
        for (var i = 0; i < rows.Length; i++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var label = Label(rows[i].Label);
            Grid.SetRow(label, i);
            Grid.SetRow(rows[i].Control, i);
            Grid.SetColumn(rows[i].Control, 1);
            grid.Children.Add(label);
            grid.Children.Add(rows[i].Control);
        }
        return grid;
    }

    private static NumericUpDown Number(double value, double min, double max, double step, string format = "0") =>
        new() { Value = (decimal)value, Minimum = (decimal)min, Maximum = (decimal)max, Increment = (decimal)step, FormatString = format, Width = 160, HorizontalAlignment = HorizontalAlignment.Left };

    private static ColorPicker ColorBox(string hex) => new()
    {
        Color = Color.TryParse(hex, out var c) ? c : Colors.White,
        IsAlphaEnabled = true,
        HorizontalAlignment = HorizontalAlignment.Left,
    };

    private static string Hex(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    // ---- Editor de gráficos -----------------------------------------------------------------------

    public static async Task ShowGraphicEditorAsync(MainViewModel vm, Guid graphicId)
    {
        if (vm.Dialogs.Owner is not { } owner || vm.Project.FindGraphic(graphicId) is not { } original)
            return;

        var window = NewWindow($"Gráfico — {original.Name}", 760, 780);
        var name = new TextBox { Text = original.Name };
        var fieldBoxes = new Dictionary<string, TextBox>();
        var fieldRows = new List<(string, Control)>();
        foreach (var field in GraphicTemplates.Fields(original.Kind))
        {
            var box = new TextBox { Text = original.Field(field.Key), AcceptsReturn = field.Multiline, TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
            if (field.Multiline)
                box.Height = 96;
            fieldBoxes[field.Key] = box;
            fieldRows.Add((field.Label, box));
        }

        var primary = ColorBox(original.Style.PrimaryColor);
        var secondary = ColorBox(original.Style.SecondaryColor);
        var text = ColorBox(original.Style.TextColor);
        var accentText = ColorBox(original.Style.AccentTextColor);
        var faces = FontManager.Current.SystemFonts.Select(f => f.Name).OrderBy(n => n).ToList();
        var font = new ComboBox { ItemsSource = faces, SelectedItem = faces.FirstOrDefault(f => f == original.Style.FontFace) ?? faces.FirstOrDefault(), MinWidth = 220 };

        var x = Number(original.X, -20_000, 20_000, 1);
        var y = Number(original.Y, -20_000, 20_000, 1);
        var scale = Number(original.Scale * 100, 10, 500, 5);
        var animations = AnimationNames.ToList();
        var animIn = new ComboBox { ItemsSource = animations.Select(a => a.Value).ToList(), SelectedIndex = animations.FindIndex(a => a.Key == original.AnimationIn), MinWidth = 220 };
        var animOut = new ComboBox { ItemsSource = animations.Select(a => a.Value).ToList(), SelectedIndex = animations.FindIndex(a => a.Key == original.AnimationOut), MinWidth = 220 };
        var animMs = Number(original.AnimationMs, 0, 10_000, 50);
        var autoHide = Number(original.AutoHideSeconds, 0, 3600, 1, "0.#");
        var dataFile = new TextBox { Text = original.DataFile ?? "", Watermark = "Sin enlace (los campos de arriba)" };
        var browse = new Button { Content = "Examinar…" };
        browse.Click += async (_, _) =>
        {
            if (await vm.Dialogs.PickFileAsync("Archivo de datos JSON", "Datos JSON (*.json);;Todos (*.*)") is { } path)
                dataFile.Text = path;
        };
        var dataRow = new DockPanel { Children = { browse, dataFile } };
        DockPanel.SetDock(browse, Dock.Right);
        browse.Margin = new Thickness(8, 0, 0, 0);
        var error = Error();
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };

        Graphic Build() => original with
        {
            Name = name.Text?.Trim() ?? "",
            Fields = fieldBoxes.ToDictionary(f => f.Key, f => f.Value.Text ?? ""),
            Style = new GraphicStyle(Hex(primary.Color), Hex(secondary.Color), Hex(text.Color), Hex(accentText.Color),
                font.SelectedItem as string ?? original.Style.FontFace),
            X = (double)(x.Value ?? 0),
            Y = (double)(y.Value ?? 0),
            Scale = (double)(scale.Value ?? 100) / 100.0,
            AnimationIn = animations[Math.Max(0, animIn.SelectedIndex)].Key,
            AnimationOut = animations[Math.Max(0, animOut.SelectedIndex)].Key,
            AnimationMs = (int)(animMs.Value ?? 0),
            AutoHideSeconds = (double)(autoHide.Value ?? 0),
            DataFile = string.IsNullOrWhiteSpace(dataFile.Text) ? null : dataFile.Text.Trim(),
        };

        var current = original;
        async Task<bool> ApplyAsync()
        {
            var updated = Build();
            if (updated == current)
                return true;
            var result = await vm.Run(new UpdateGraphicCommand(updated));
            error.Text = result.Success ? "" : result.Error;
            if (result.Success)
            {
                current = updated;
                status.Text = vm.Session.Graphics.IsOnAir(graphicId) ? "Aplicado en directo." : "Aplicado.";
            }
            return result.Success;
        }

        var body = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = GraphicTemplates.DisplayName(original.Kind), Classes = { "muted" } },
                    Form(("Nombre", name)),
                    Section("Contenido"),
                    Form([.. fieldRows]),
                    Section("Estilo"),
                    Form(("Color principal", primary), ("Color de fondo", secondary), ("Texto", text), ("Texto sobre color principal", accentText),
                        ("Tipografía", font)),
                    Section("Posición y animación"),
                    Form(("X (px)", x), ("Y (px)", y), ("Escala (%)", scale), ("Entrada", animIn), ("Salida", animOut),
                        ("Duración animación (ms)", animMs), ("Salida automática (s, 0 = manual)", autoHide)),
                    Section("Datos en vivo"),
                    new TextBlock
                    {
                        Text = "Enlaza un archivo JSON con los mismos nombres de campo (p. ej. {\"homeScore\": 2}). Cada vez que otro programa lo guarde, el gráfico se actualiza en directo.",
                        Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                    },
                    Form(("Archivo de datos", dataRow)),
                    new TextBlock
                    {
                        Text = "Campos: " + string.Join(", ", GraphicTemplates.Fields(original.Kind).Select(f => f.Key)),
                        Classes = { "muted" }, FontSize = 11,
                    },
                    error,
                    status,
                },
            },
        };

        var saveTemplate = new Button { Content = "Guardar como plantilla…" };
        saveTemplate.Click += async (_, _) =>
        {
            if (await vm.Dialogs.PromptAsync("Guardar plantilla", "Nombre de la plantilla (reutilizable en otros proyectos):", name.Text ?? "") is not { } templateName)
                return;
            try
            {
                await vm.Templates.SaveAsync(templateName, Build(), CancellationToken.None);
                status.Text = $"Plantilla «{templateName}» guardada.";
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                error.Text = ex.Message;
            }
        };
        var apply = new Button { Content = "Aplicar" };
        apply.Click += async (_, _) => await ApplyAsync();
        var ok = Primary("Aceptar");
        ok.Click += async (_, _) =>
        {
            if (await ApplyAsync())
                window.Close();
        };
        var cancel = new Button { Content = "Cerrar", IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        window.Content = Layout(body, saveTemplate, cancel, apply, ok);
        await window.ShowDialog(owner);
    }

    // ---- Plantillas guardadas ---------------------------------------------------------------------

    public static async Task ShowTemplatePickerAsync(MainViewModel vm)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;
        var window = NewWindow("Añadir gráfico desde plantilla", 520, 460);
        var templates = (await vm.Templates.ListAsync(CancellationToken.None)).ToList();
        var list = new ListBox { Classes = { "studioList" } };
        var error = Error();
        void Reload()
        {
            list.ItemsSource = templates.Select(t => $"{t.Name}  ·  {GraphicTemplates.DisplayName(t.Kind)}").ToList();
            list.SelectedIndex = templates.Count > 0 ? 0 : -1;
        }
        Reload();

        var empty = new TextBlock
        {
            Text = "No hay plantillas. Abre un gráfico y pulsa «Guardar como plantilla…».",
            Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, IsVisible = templates.Count == 0,
        };
        var body = new DockPanel { Margin = new Thickness(16) };
        var footer = new StackPanel { Children = { empty, error } };
        DockPanel.SetDock(footer, Dock.Bottom);
        body.Children.Add(footer);
        body.Children.Add(list);

        var delete = new Button { Content = "Eliminar plantilla" };
        delete.Click += async (_, _) =>
        {
            if (list.SelectedIndex < 0)
                return;
            var t = templates[list.SelectedIndex];
            if (!await vm.Dialogs.ConfirmAsync("Eliminar plantilla", $"¿Eliminar la plantilla «{t.Name}»?", "Eliminar", destructive: true))
                return;
            await vm.Templates.DeleteAsync(t.Name, CancellationToken.None);
            templates.Remove(t);
            Reload();
        };
        var add = Primary("Añadir");
        add.Click += async (_, _) =>
        {
            if (list.SelectedIndex < 0)
                return;
            try
            {
                if (await vm.AddGraphicFromTemplateAsync(templates[list.SelectedIndex].Name))
                    window.Close();
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                error.Text = ex.Message;
            }
        };
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        window.Content = Layout(body, delete, cancel, add);
        await window.ShowDialog(owner);
    }

    // ---- Filtros ------------------------------------------------------------------------------------

    public static async Task ShowFiltersAsync(MainViewModel vm, Guid sourceId)
    {
        if (vm.Dialogs.Owner is not { } owner || vm.Project.FindSource(sourceId) is not { } source)
            return;
        if (!vm.EngineReady)
        {
            await vm.Dialogs.InfoAsync("Filtros", "Los filtros se aplican en el motor, que no está iniciado.");
            return;
        }

        var engineTypes = (await vm.Engine.ListFilterTypesAsync(CancellationToken.None)).Select(t => t.Id).ToHashSet();
        var hasAudio = source.Kind is SourceKind.AudioInput or SourceKind.AudioOutput or SourceKind.Media or SourceKind.VideoCaptureDevice or SourceKind.Browser;
        var offered = FilterCatalog.All
            .Where(e => engineTypes.Contains(e.TypeId) && (e.Audio ? hasAudio : !source.IsAudioOnly))
            .ToList();

        var window = NewWindow($"Filtros — {source.Name}", 880, 600);
        var list = new ListBox { Classes = { "studioList" } };
        var editor = new PropertyEditor();
        var placeholder = new TextBlock { Text = "Añade un filtro con + o selecciona uno para editarlo.", Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        var error = Error();
        var editorHost = new ScrollViewer { Content = new StackPanel { Margin = new Thickness(16), Spacing = 10, Children = { placeholder, editor, error } } };

        Guid? editing = null;
        string? baseline = null; // ajustes del motor al empezar a editar el filtro seleccionado
        string? previous = null; // ajustes del dominio, para deshacer
        CancellationTokenSource? pending = null;

        async Task CommitAsync()
        {
            pending?.Cancel();
            if (editing is not { } id || baseline is null)
                return;
            var json = editor.Settings.ToJsonString();
            if (json != baseline && source.Filters.Any(f => f.Id == id))
                await vm.Run(new UpdateFilterSettingsCommand(sourceId, id, json, previous));
            baseline = json;
            previous = json;
        }

        editor.SettingsChanged += (current, structural) =>
        {
            if (editing is not { } id)
                return;
            pending?.Cancel();
            var cts = pending = new CancellationTokenSource();
            var json = current.ToJsonString();
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(120, cts.Token);
                    await vm.Engine.UpdateFilterAsync(id, json, CancellationToken.None);
                    if (structural)
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => SelectAsync(id));
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => error.Text = ex.Message);
                }
            });
        };

        async Task SelectAsync(Guid id)
        {
            editing = id;
            var settingsJson = await vm.Engine.GetSourceSettingsAsync(id, CancellationToken.None);
            baseline ??= settingsJson;
            previous ??= source.GetFilter(id).SettingsJson;
            var properties = await vm.Engine.GetSourcePropertiesAsync(id, CancellationToken.None);
            editor.Build(properties, JsonNode.Parse(settingsJson) as JsonObject ?? [], vm.Dialogs);
            placeholder.IsVisible = false;
        }

        void Reload(Guid? select = null)
        {
            var rows = source.Filters.Select(f =>
            {
                var check = new CheckBox { IsChecked = f.Enabled, Content = f.Name, Tag = f.Id };
                ToolTip.SetTip(check, FilterCatalog.Find(f.TypeId)?.Name ?? f.TypeId);
                check.IsCheckedChanged += async (_, _) =>
                {
                    if (check.IsChecked != f.Enabled)
                        await vm.Run(new SetFilterEnabledCommand(sourceId, f.Id, check.IsChecked == true));
                };
                return check;
            }).ToList();
            list.ItemsSource = rows;
            list.SelectedItem = rows.FirstOrDefault(r => (Guid)r.Tag! == select) ?? rows.FirstOrDefault(r => (Guid?)r.Tag == editing);
            if (rows.Count == 0)
            {
                editing = null;
                placeholder.IsVisible = true;
                editor.Build([], [], vm.Dialogs);
            }
        }

        list.SelectionChanged += async (_, _) =>
        {
            if (list.SelectedItem is not CheckBox { Tag: Guid id } || id == editing)
                return;
            await CommitAsync();
            baseline = previous = null;
            await SelectAsync(id);
        };

        var addMenu = new MenuFlyout();
        foreach (var group in offered.GroupBy(e => e.Audio))
        {
            if (addMenu.Items.Count > 0)
                addMenu.Items.Add(new Separator());
            foreach (var entry in group)
            {
                var item = new MenuItem { Header = entry.Name };
                item.Click += async (_, _) =>
                {
                    await CommitAsync();
                    var command = new AddFilterCommand(sourceId, entry.TypeId, NextFilterName(source, entry.Name));
                    var result = await vm.Run(command);
                    error.Text = result.Success ? "" : result.Error;
                    if (result.Success)
                        Reload(command.FilterId);
                };
                addMenu.Items.Add(item);
            }
        }
        Button IconButton(string icon, string tip)
        {
            var b = new Button { Classes = { "icon" }, Content = new PathIcon { Data = (Geometry)Avalonia.Application.Current!.FindResource(icon)! } };
            ToolTip.SetTip(b, tip);
            Avalonia.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }
        var add = IconButton("Icon.Plus", "Añadir filtro");
        add.Flyout = addMenu;
        add.IsEnabled = offered.Count > 0;
        var remove = IconButton("Icon.Trash", "Eliminar filtro");
        remove.Click += async (_, _) =>
        {
            if (editing is not { } id)
                return;
            pending?.Cancel();
            editing = null;
            baseline = previous = null;
            await vm.Run(new RemoveFilterCommand(sourceId, id));
            Reload();
        };
        var up = IconButton("Icon.Up", "Antes en la cadena");
        var down = IconButton("Icon.Down", "Después en la cadena");
        async Task Move(int delta)
        {
            if (editing is not { } id)
                return;
            await CommitAsync();
            await vm.Run(new MoveFilterCommand(sourceId, id, source.IndexOfFilter(id) + delta));
            Reload(id);
        }
        up.Click += async (_, _) => await Move(-1);
        down.Click += async (_, _) => await Move(+1);

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Children = { add, remove, up, down } };
        var left = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(toolbar, Dock.Top);
        left.Children.Add(toolbar);
        left.Children.Add(new TextBlock
        {
            Text = hasAudio && source.IsAudioOnly
                ? "El orden importa: la señal pasa de arriba abajo (p. ej. puerta de ruido → supresión → compresor → limitador)."
                : "Los filtros se aplican en orden, de arriba abajo. La casilla activa o desactiva cada uno.",
            Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8),
            [DockPanel.DockProperty] = Dock.Bottom,
        });
        left.Children.Add(list);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("280,*") };
        body.Children.Add(new Border { Child = left, Background = new SolidColorBrush(Color.Parse("#FF17191D")) });
        body.Children.Add(WithColumn(editorHost, 1));

        var close = Primary("Cerrar");
        close.Click += (_, _) => window.Close();
        window.Content = Layout(body, close);
        Reload();
        await window.ShowDialog(owner);
        await CommitAsync();
    }

    private static string NextFilterName(Source source, string baseName)
    {
        if (source.Filters.All(f => f.Name != baseName))
            return baseName;
        for (var i = 2; ; i++)
            if (source.Filters.All(f => f.Name != $"{baseName} {i}"))
                return $"{baseName} {i}";
    }

    // ---- Transiciones -------------------------------------------------------------------------------

    /// <summary>Ajustes de la transición general (sceneId null) o de la transición propia de una escena.</summary>
    public static async Task ShowTransitionSettingsAsync(MainViewModel vm, Guid? sceneId = null, string? initialType = null)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;
        var scene = sceneId is { } id ? vm.Project.FindScene(id) : null;
        if (sceneId is not null && scene is null)
            return;
        var current = scene is null ? vm.Project.Transition : scene.TransitionOverride ?? vm.Project.Transition;
        var window = NewWindow(scene is null ? "Transición" : $"Transición de la escena — {scene.Name}", 600, 560);

        var useGlobal = new CheckBox
        {
            Content = "Usar la transición general del proyecto",
            IsChecked = scene is not null && scene.TransitionOverride is null,
            IsVisible = scene is not null,
        };
        var types = TransitionCatalog.Names.ToList();
        var type = new ComboBox { ItemsSource = types.Select(t => t.Value).ToList(), MinWidth = 240 };
        type.SelectedIndex = Math.Max(0, types.FindIndex(t => t.Key == (initialType ?? current.TypeId)));
        var duration = Number(current.DurationMs, 0, 20_000, 50);
        var specific = new StackPanel { Spacing = 8 };
        var error = Error();
        Func<JsonObject> collect = () => [];

        JsonObject Existing(string typeId) =>
            typeId == current.TypeId && JsonNode.Parse(string.IsNullOrWhiteSpace(current.SettingsJson) ? "{}" : current.SettingsJson) is JsonObject o ? o : [];

        void BuildSpecific()
        {
            specific.Children.Clear();
            var typeId = types[Math.Max(0, type.SelectedIndex)].Key;
            var s = Existing(typeId);
            duration.IsEnabled = typeId is not (TransitionCatalog.Stinger or TransitionCatalog.Cut);
            switch (typeId)
            {
                case TransitionCatalog.Stinger:
                {
                    var path = new TextBox { Text = s["path"]?.GetValue<string>() ?? "", Watermark = "Video con canal alfa (.webm, .mov) o normal (.mp4)" };
                    var browse = new Button { Content = "Examinar…", Margin = new Thickness(8, 0, 0, 0) };
                    browse.Click += async (_, _) =>
                    {
                        if (await vm.Dialogs.PickFileAsync("Video del stinger", "Videos (*.webm *.mov *.mp4 *.mkv);;Todos (*.*)") is { } file)
                            path.Text = file;
                    };
                    var row = new DockPanel { Children = { browse, path } };
                    DockPanel.SetDock(browse, Dock.Right);
                    var point = Number(s["transition_point"]?.GetValue<double>() ?? 500, 0, 60_000, 50);
                    specific.Children.Add(Form(("Video", row), ("Punto de corte (ms)", point)));
                    specific.Children.Add(new TextBlock
                    {
                        Text = "La escena cambia en el punto de corte, mientras el video tapa la imagen. La duración la marca el video.",
                        Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                    });
                    collect = () => new JsonObject
                    {
                        ["path"] = (path.Text ?? "").Trim().Replace('\\', '/'),
                        ["transition_point"] = (long)(point.Value ?? 0),
                        ["tp_type"] = TransitionCatalog.StingerPointTime,
                    };
                    break;
                }
                case TransitionCatalog.LumaWipe:
                {
                    var images = TransitionCatalog.LumaImages.ToList();
                    var image = new ComboBox { ItemsSource = images, SelectedItem = s["luma_image"]?.GetValue<string>() ?? "linear-h.png", MinWidth = 240 };
                    var softness = new Slider { Minimum = 0, Maximum = 1, Value = s["luma_softness"]?.GetValue<double>() ?? 0.03, Width = 240, HorizontalAlignment = HorizontalAlignment.Left };
                    var invert = new CheckBox { Content = "Invertir", IsChecked = s["luma_invert"]?.GetValue<bool>() ?? false };
                    specific.Children.Add(Form(("Patrón", image), ("Suavidad del borde", softness), ("", invert)));
                    collect = () => new JsonObject
                    {
                        ["luma_image"] = image.SelectedItem as string ?? "linear-h.png",
                        ["luma_softness"] = Math.Round(softness.Value, 3),
                        ["luma_invert"] = invert.IsChecked == true,
                    };
                    break;
                }
                case TransitionCatalog.Slide or TransitionCatalog.Swipe:
                {
                    var names = new[] { "Hacia la izquierda", "Hacia la derecha", "Hacia arriba", "Hacia abajo" };
                    var dirs = TransitionCatalog.Directions.ToList();
                    var direction = new ComboBox { ItemsSource = names, SelectedIndex = Math.Max(0, dirs.IndexOf(s["direction"]?.GetValue<string>() ?? "left")), MinWidth = 240 };
                    var swipeIn = new CheckBox { Content = "La nueva escena entra por encima", IsChecked = s["swipe_in"]?.GetValue<bool>() ?? false, IsVisible = typeId == TransitionCatalog.Swipe };
                    specific.Children.Add(Form(("Dirección", direction), ("", swipeIn)));
                    collect = () =>
                    {
                        var o = new JsonObject { ["direction"] = dirs[Math.Max(0, direction.SelectedIndex)] };
                        if (typeId == TransitionCatalog.Swipe)
                            o["swipe_in"] = swipeIn.IsChecked == true;
                        return o;
                    };
                    break;
                }
                case TransitionCatalog.FadeToColor:
                {
                    var raw = s["color"] is JsonValue v && v.TryGetValue<long>(out var l) ? (uint)l : 0xFF000000;
                    var color = new ColorPicker
                    {
                        Color = Color.FromArgb(255, (byte)raw, (byte)(raw >> 8), (byte)(raw >> 16)),
                        IsAlphaEnabled = false, HorizontalAlignment = HorizontalAlignment.Left,
                    };
                    var switchPoint = Number(s["switch_point"]?.GetValue<double>() ?? 50, 0, 100, 5);
                    specific.Children.Add(Form(("Color", color), ("Punto de cambio (%)", switchPoint)));
                    collect = () => new JsonObject
                    {
                        ["color"] = (long)(0xFF000000u | (uint)color.Color.B << 16 | (uint)color.Color.G << 8 | color.Color.R),
                        ["switch_point"] = (long)(switchPoint.Value ?? 50),
                    };
                    break;
                }
                default:
                    collect = () => [];
                    break;
            }
        }

        type.SelectionChanged += (_, _) => BuildSpecific();
        BuildSpecific();
        var settingsPanel = new StackPanel { Spacing = 10, Children = { Form(("Tipo", type), ("Duración (ms)", duration)), specific } };
        settingsPanel.IsEnabled = useGlobal.IsChecked != true;
        useGlobal.IsCheckedChanged += (_, _) => settingsPanel.IsEnabled = useGlobal.IsChecked != true;

        var body = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = scene is null
                            ? "Se usa al cambiar la escena de programa, salvo en escenas con transición propia."
                            : "Se usa al entrar en esta escena (al ponerla en programa o con «Transición» en modo estudio).",
                        Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                    },
                    useGlobal, settingsPanel, error,
                },
            },
        };

        var ok = Primary("Aceptar");
        ok.Click += async (_, _) =>
        {
            TransitionSettings? chosen = null;
            if (useGlobal.IsChecked != true)
            {
                chosen = new TransitionSettings(types[Math.Max(0, type.SelectedIndex)].Key, (int)(duration.Value ?? 0), collect().ToJsonString());
                if (TransitionCatalog.Validate(chosen) is { } invalid)
                {
                    error.Text = invalid;
                    return;
                }
            }
            var result = scene is null
                ? await vm.Run(new SetTransitionCommand(chosen!))
                : await vm.Run(new SetSceneTransitionCommand(scene.Id, chosen));
            if (result.Success)
                window.Close();
            else
                error.Text = result.Error;
        };
        var cancel = new Button { Content = "Cancelar", IsCancel = true };
        cancel.Click += (_, _) => window.Close();
        window.Content = Layout(body, cancel, ok);
        await window.ShowDialog(owner);
    }

    private static Scene? FindScene(this StudioProject project, Guid id) => project.Scenes.FirstOrDefault(s => s.Id == id);
}

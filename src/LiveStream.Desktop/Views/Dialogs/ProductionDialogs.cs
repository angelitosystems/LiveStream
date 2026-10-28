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
using LiveStream.Domain.Alerts;
using LiveStream.Domain.Entities;
using LiveStream.Domain.Graphics;
using LiveStream.Domain.Tts;
using LiveStream.Infrastructure.Persistence;
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
        foreach (var field in GraphicTemplates.FieldsOf(original))
        {
            var box = new TextBox { Text = original.Field(field.Key), AcceptsReturn = field.Multiline, TextWrapping = field.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
            if (field.Multiline)
                box.Height = 96;
            fieldBoxes[field.Key] = box;
            if (field.FileFilter is { } filter)
            {
                var pick = new Button { Content = "Examinar…" };
                pick.Click += async (_, _) =>
                {
                    if (await vm.Dialogs.PickFileAsync(field.Label, filter) is { } path)
                        box.Text = path;
                };
                var row = new DockPanel { Children = { pick, box } };
                DockPanel.SetDock(pick, Dock.Right);
                fieldRows.Add((field.Label, row));
            }
            else
            {
                fieldRows.Add((field.Label, box));
            }
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
                        Text = "Campos: " + string.Join(", ", GraphicTemplates.FieldsOf(original).Select(f => f.Key)),
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

    // ---- Kit de marca -----------------------------------------------------------------------------

    public static async Task ShowBrandKitAsync(MainViewModel vm)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;
        var window = NewWindow("Kit de marca", 640, 600);
        var kit = vm.Brand.Load();
        var name = new TextBox();
        var primary = ColorBox(kit.Style.PrimaryColor);
        var secondary = ColorBox(kit.Style.SecondaryColor);
        var text = ColorBox(kit.Style.TextColor);
        var accentText = ColorBox(kit.Style.AccentTextColor);
        var faces = FontManager.Current.SystemFonts.Select(f => f.Name).OrderBy(n => n).ToList();
        var font = new ComboBox { ItemsSource = faces, MinWidth = 220 };
        var logo = new TextBox { Watermark = "Sin logo" };
        var browse = new Button { Content = "Examinar…", Margin = new Thickness(8, 0, 0, 0) };
        browse.Click += async (_, _) =>
        {
            if (await vm.Dialogs.PickFileAsync("Logo", GraphicTemplates.ImageFilter) is { } path)
                logo.Text = path;
        };
        var logoRow = new DockPanel { Children = { browse, logo } };
        DockPanel.SetDock(browse, Dock.Right);
        var error = Error();
        var status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };

        void Show(BrandKit k)
        {
            name.Text = k.Name;
            primary.Color = Color.Parse(k.Style.PrimaryColor);
            secondary.Color = Color.Parse(k.Style.SecondaryColor);
            text.Color = Color.Parse(k.Style.TextColor);
            accentText.Color = Color.Parse(k.Style.AccentTextColor);
            font.SelectedItem = faces.FirstOrDefault(f => f == k.Style.FontFace) ?? faces.FirstOrDefault();
            logo.Text = k.Logo;
        }
        Show(kit);

        BrandKit Build() => new(name.Text?.Trim() ?? "",
            new GraphicStyle(Hex(primary.Color), Hex(secondary.Color), Hex(text.Color), Hex(accentText.Color), font.SelectedItem as string ?? kit.Style.FontFace),
            logo.Text?.Trim() ?? "");

        bool Save()
        {
            try
            {
                var built = Build();
                if (built.Logo.Length > 0 && !File.Exists(built.Logo))
                    throw new ArgumentException($"No se encuentra el logo: {built.Logo}");
                vm.Brand.Save(built);
                error.Text = "";
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
            {
                error.Text = ex.Message;
                return false;
            }
        }

        var importButton = new Button { Content = "Importar…" };
        importButton.Click += async (_, _) =>
        {
            if (await vm.Dialogs.PickFileAsync("Importar kit de marca", BrandKitStore.FileFilter) is not { } path)
                return;
            try
            {
                Show(vm.Brand.Import(path));
                error.Text = "";
                status.Text = "Kit importado. Pulsa «Guardar» para usarlo.";
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                error.Text = ex.Message;
            }
        };
        var exportButton = new Button { Content = "Exportar…" };
        exportButton.Click += async (_, _) =>
        {
            if (await vm.Dialogs.SaveFileAsync("Exportar kit de marca", (name.Text ?? "marca") + ".lsbrand", "lsbrand") is not { } path)
                return;
            try
            {
                vm.Brand.Export(Build(), path);
                error.Text = "";
                status.Text = $"Exportado: {path}";
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                error.Text = ex.Message;
            }
        };
        var applyAll = new Button { Content = "Aplicar a todos los gráficos" };
        applyAll.Click += async (_, _) =>
        {
            if (!Save())
                return;
            var result = await vm.Run(new ApplyBrandCommand(Build()));
            error.Text = result.Success ? "" : result.Error;
            if (result.Success)
                status.Text = "Marca aplicada a todos los gráficos (Ctrl+Z para deshacer).";
        };
        var ok = Primary("Guardar");
        ok.Click += (_, _) =>
        {
            if (Save())
                window.Close();
        };
        var cancel = new Button { Content = "Cerrar", IsCancel = true };
        cancel.Click += (_, _) => window.Close();

        var body = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Colores, tipografía y logo que reciben los gráficos nuevos. «Aplicar a todos» los lleva también a los gráficos de este proyecto.",
                        Classes = { "muted" }, TextWrapping = TextWrapping.Wrap,
                    },
                    Form(("Nombre", name), ("Color principal", primary), ("Color de fondo", secondary), ("Texto", text),
                        ("Texto sobre color principal", accentText), ("Tipografía", font), ("Logo", logoRow)),
                    error,
                    status,
                },
            },
        };
        window.Content = Layout(body, importButton, exportButton, applyAll, cancel, ok);
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

        var window = NewWindow($"Filtros — {source.Name}", 1020, 720);
        var list = new ListBox { Classes = { "studioList" } };
        var editor = new PropertyEditor();
        var placeholder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF161920")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF262B34")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(24, 30),
            Child = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    new TextBlock { Text = "✨", FontSize = 28, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock
                    {
                        Text = "Añade un filtro con el botón «+» o selecciona uno de la lista para configurar sus parámetros.",
                        Classes = { "muted" },
                        FontSize = 12.5,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center
                    }
                }
            }
        };

        var error = Error();

        // Cabecera informativa del filtro seleccionado
        var filterTitle = new TextBlock { FontWeight = FontWeight.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        var filterCategoryBadge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF1E2838")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = new SolidColorBrush(Color.Parse("#FF93C5FD")) }
        };
        var filterDesc = new TextBlock { Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        var filterInfoCard = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF181C23")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 10),
            IsVisible = false,
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { filterTitle, filterCategoryBadge } },
                    filterDesc
                }
            }
        };

        // Componentes de vista previa en tiempo real
        LiveSourcePreview? preview = null;
        AudioMeterControl? meter = null;

        if (!source.IsAudioOnly)
        {
            preview = new LiveSourcePreview { Height = 220, Margin = new Thickness(0, 0, 0, 10) };
            preview.Attach(vm, sourceId, allowCompare: true);
        }

        if (source.SupportsAudio)
        {
            meter = new AudioMeterControl { Margin = new Thickness(0, 0, 0, 10) };
            meter.Attach(vm, sourceId);
        }

        var previewContainer = new StackPanel { Spacing = 0 };
        if (preview is not null)
            previewContainer.Children.Add(preview);
        if (meter is not null)
            previewContainer.Children.Add(meter);

        var editorHost = new ScrollViewer
        {
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    previewContainer,
                    filterInfoCard,
                    placeholder,
                    editor,
                    error
                }
            }
        };

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
            var filter = source.GetFilter(id);
            var entry = FilterCatalog.Find(filter.TypeId);
            filterTitle.Text = filter.Name;
            ((TextBlock)filterCategoryBadge.Child!).Text = entry is not null ? FilterCatalog.CategoryName(entry.Category) : "Filtro";
            filterDesc.Text = entry?.Description ?? "";
            filterInfoCard.IsVisible = true;

            var settingsJson = await vm.Engine.GetSourceSettingsAsync(id, CancellationToken.None);
            baseline ??= settingsJson;
            previous ??= filter.SettingsJson;
            var properties = await vm.Engine.GetSourcePropertiesAsync(id, CancellationToken.None);
            editor.Build(properties, JsonNode.Parse(settingsJson) as JsonObject ?? [], vm.Dialogs);
            placeholder.IsVisible = false;
        }

        void Reload(Guid? select = null)
        {
            var rows = source.Filters.Select(f =>
            {
                var check = new CheckBox { IsChecked = f.Enabled, Content = f.Name, Tag = f.Id };
                var catEntry = FilterCatalog.Find(f.TypeId);
                ToolTip.SetTip(check, $"{catEntry?.Name ?? f.TypeId} — {catEntry?.Description ?? ""}");
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
                filterInfoCard.IsVisible = false;
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

        // Menú categorizado de añadir filtros
        var addMenu = new MenuFlyout();
        var categoryGroups = new[]
        {
            FilterCategory.Audio,
            FilterCategory.ColorCorrection,
            FilterCategory.KeyingAndBackground,
            FilterCategory.MasksAndCrop,
            FilterCategory.CreativeEffects,
            FilterCategory.Utility
        };

        foreach (var cat in categoryGroups)
        {
            var inCat = offered.Where(e => e.Category == cat).ToList();
            if (inCat.Count == 0)
                continue;

            var catSub = new MenuItem { Header = FilterCatalog.CategoryName(cat) };
            foreach (var entry in inCat)
            {
                var item = new MenuItem { Header = entry.Name };
                ToolTip.SetTip(item, entry.Description);
                item.Click += async (_, _) =>
                {
                    await CommitAsync();
                    var command = new AddFilterCommand(sourceId, entry.TypeId, NextFilterName(source, entry.Name));
                    var result = await vm.Run(command);
                    error.Text = result.Success ? "" : result.Error;
                    if (result.Success)
                        Reload(command.FilterId);
                };
                catSub.Items.Add(item);
            }
            addMenu.Items.Add(catSub);
        }

        Button IconButton(string icon, string tip)
        {
            var b = new Button { Classes = { "icon" }, Content = new Glyph { Data = (Geometry)Avalonia.Application.Current!.FindResource(icon)! } };
            ToolTip.SetTip(b, tip);
            Avalonia.Automation.AutomationProperties.SetName(b, tip);
            return b;
        }

        var add = IconButton("Icon.Plus", "Añadir filtro");
        add.Flyout = addMenu;
        add.IsEnabled = offered.Count > 0;

        var remove = IconButton("Icon.Trash", "Eliminar filtro seleccionado");
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

        var up = IconButton("Icon.Up", "Subir en la cadena de filtros");
        var down = IconButton("Icon.Down", "Bajar en la cadena de filtros");
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

        var duplicate = IconButton("Icon.Copy", "Duplicar filtro seleccionado con sus ajustes");
        duplicate.Click += async (_, _) =>
        {
            if (editing is not { } id)
                return;
            await CommitAsync();
            var f = source.Filters.FirstOrDefault(filt => filt.Id == id);
            if (f is null)
                return;
            var command = new DuplicateFilterCommand(sourceId, id, NextFilterName(source, f.Name));
            var result = await vm.Run(command);
            error.Text = result.Success ? "" : result.Error;
            if (result.Success)
                Reload(command.NewFilterId);
        };

        // Menú de preajustes y portapapeles
        var presetsMenu = new MenuFlyout();
        var recSubMenu = new MenuItem { Header = "⭐ Cadenas predefinidas recomendadas" };
        var sourcePresets = FilterPresetStore.GetPresetsForSource(source.IsAudioOnly);
        foreach (var preset in sourcePresets)
        {
            var pItem = new MenuItem { Header = preset.Name };
            ToolTip.SetTip(pItem, preset.Description);
            var localPreset = preset;
            pItem.Click += async (_, _) =>
            {
                if (await vm.Dialogs.ConfirmAsync("Aplicar preajuste", $"¿Deseas añadir la cadena «{localPreset.Name}» ({localPreset.Filters.Count} filtros)?"))
                {
                    await CommitAsync();
                    foreach (var pf in localPreset.Filters)
                    {
                        if (engineTypes.Contains(pf.TypeId))
                        {
                            var cmd = new AddFilterCommand(sourceId, pf.TypeId, NextFilterName(source, pf.Name), pf.SettingsJson);
                            await vm.Run(cmd);
                        }
                    }
                    Reload();
                }
            };
            recSubMenu.Items.Add(pItem);
        }
        if (recSubMenu.Items.Count > 0)
            presetsMenu.Items.Add(recSubMenu);

        presetsMenu.Items.Add(new Separator());

        var copyFiltersItem = new MenuItem { Header = "Copiar filtros al portapapeles" };
        copyFiltersItem.Click += async (_, _) =>
        {
            await CommitAsync();
            if (source.Filters.Count == 0)
            {
                error.Text = "No hay filtros para copiar.";
                return;
            }
            var items = source.Filters.Select(f => new FilterPresetItem(f.Name, f.TypeId, f.SettingsJson, f.Enabled)).ToList();
            var json = FilterPresetStore.ExportFiltersToJson(items);
            if (window.Clipboard is { } cb)
            {
                await cb.SetTextAsync(json);
                error.Text = $"Copiados {items.Count} filtros al portapapeles.";
            }
        };
        presetsMenu.Items.Add(copyFiltersItem);

        var pasteFiltersItem = new MenuItem { Header = "Pegar filtros desde el portapapeles" };
        pasteFiltersItem.Click += async (_, _) =>
        {
            if (window.Clipboard is { } cb && await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(cb) is { } text)
            {
                var imported = FilterPresetStore.ImportFiltersFromJson(text);
                if (imported is { Count: > 0 })
                {
                    await CommitAsync();
                    var count = 0;
                    foreach (var item in imported)
                    {
                        if (engineTypes.Contains(item.TypeId))
                        {
                            var cmd = new AddFilterCommand(sourceId, item.TypeId, NextFilterName(source, item.Name), item.SettingsJson);
                            var res = await vm.Run(cmd);
                            if (res.Success) count++;
                        }
                    }
                    Reload();
                    error.Text = $"Se pegaron {count} filtros correctamente.";
                }
                else
                {
                    error.Text = "El portapapeles no contiene una cadena de filtros válida.";
                }
            }
        };
        presetsMenu.Items.Add(pasteFiltersItem);

        var savePresetItem = new MenuItem { Header = "Guardar filtros como preajuste..." };
        savePresetItem.Click += async (_, _) =>
        {
            await CommitAsync();
            if (source.Filters.Count == 0)
            {
                error.Text = "No hay filtros para guardar como preajuste.";
                return;
            }
            if (await vm.Dialogs.PromptAsync("Guardar preajuste", "Nombre para esta cadena de filtros:", $"{source.Name} Cadena") is { } pName && !string.IsNullOrWhiteSpace(pName))
            {
                var items = source.Filters.Select(f => new FilterPresetItem(f.Name, f.TypeId, f.SettingsJson, f.Enabled)).ToList();
                FilterPresetStore.SaveCustomPreset(pName.Trim(), "Preajuste guardado por el usuario", source.IsAudioOnly, items);
                error.Text = $"Preajuste «{pName.Trim()}» guardado.";
            }
        };
        presetsMenu.Items.Add(savePresetItem);

        var presetsBtn = new Button
        {
            Content = "Preajustes",
            Padding = new Thickness(8, 4),
            Flyout = presetsMenu,
            VerticalAlignment = VerticalAlignment.Center
        };

        var resetBtn = new Button
        {
            Content = "Restablecer ajustes",
            Classes = { "ghost" },
            VerticalAlignment = VerticalAlignment.Center
        };
        resetBtn.Click += async (_, _) =>
        {
            if (editing is not { } id)
                return;
            var f = source.Filters.FirstOrDefault(filt => filt.Id == id);
            if (f is null || FilterCatalog.Find(f.TypeId) is not { } entry)
                return;
            await vm.Run(new UpdateFilterSettingsCommand(sourceId, id, entry.DefaultSettingsJson));
            baseline = null;
            await SelectAsync(id);
            error.Text = "Ajustes restablecidos a valores predeterminados.";
        };

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { add, remove, up, down, duplicate, presetsBtn }
        };

        var left = new DockPanel { Margin = new Thickness(8) };
        DockPanel.SetDock(toolbar, Dock.Top);
        left.Children.Add(toolbar);
        left.Children.Add(new TextBlock
        {
            Text = hasAudio && source.IsAudioOnly
                ? "El orden importa: la señal se procesa de arriba abajo (p. ej. Puerta de ruido → Supresión RNNoise → Compresor → Limitador)."
                : "Los filtros se aplican en orden descendente. La casilla activa o desactiva cada filtro en tiempo real.",
            Classes = { "muted" }, FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 8),
            [DockPanel.DockProperty] = Dock.Bottom,
        });
        left.Children.Add(list);

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("310,*") };
        body.Children.Add(new Border { Child = left, Background = new SolidColorBrush(Color.Parse("#FF17191D")) });
        body.Children.Add(WithColumn(editorHost, 1));

        var close = Primary("Cerrar");
        close.Click += (_, _) => window.Close();
        window.Content = Layout(body, resetBtn, close);
        Reload();

        await window.ShowDialog(owner);
        await CommitAsync();
        meter?.Dispose();
        if (preview is not null)
            await preview.DisposeAsync();
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

    // ---- Ajustes de Lectura por Voz (TTS) --------------------------------------------------------

    public static async Task ShowTtsSettingsAsync(MainViewModel vm)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;

        var tts = vm.Tts;
        await tts.LoadVoicesAsync();

        var window = NewWindow("Lectura de chat por voz (TTS)", 780, 680);

        // Control maestro
        var enabledCheck = new CheckBox { Content = "Activar síntesis de voz (TTS)", IsChecked = tts.Enabled };
        enabledCheck.IsCheckedChanged += (_, _) => tts.Enabled = enabledCheck.IsChecked == true;

        var autoReadCheck = new CheckBox { Content = "Leer automáticamente mensajes entrantes del chat", IsChecked = tts.AutoRead };
        autoReadCheck.IsCheckedChanged += (_, _) => tts.AutoRead = autoReadCheck.IsChecked == true;

        var muteBtn = new Button { Content = tts.IsMuted ? "Quitar silencio" : "Silenciar audio TTS" };
        muteBtn.Click += (_, _) =>
        {
            tts.ToggleMuteCommand.Execute(null);
            muteBtn.Content = tts.IsMuted ? "Quitar silencio" : "Silenciar audio TTS";
        };

        var skipBtn = new Button { Content = "Saltar actual" };
        skipBtn.Click += (_, _) => tts.SkipCommand.Execute(null);

        var pauseBtn = new Button { Content = tts.IsPaused ? "Reanudar" : "Pausar" };
        pauseBtn.Click += async (_, _) =>
        {
            await tts.TogglePauseCommand.ExecuteAsync(null);
            pauseBtn.Content = tts.IsPaused ? "Reanudar" : "Pausar";
        };

        var clearBtn = new Button { Content = "Vaciar cola" };
        clearBtn.Click += (_, _) => tts.ClearQueueCommand.Execute(null);

        // Voces disponibles
        var voiceCombo = new ComboBox
        {
            ItemsSource = tts.AvailableVoices,
            SelectedItem = tts.SelectedVoice,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        voiceCombo.SelectionChanged += (_, _) =>
        {
            if (voiceCombo.SelectedItem is TtsVoice v)
                tts.SelectedVoice = v;
        };

        // Volumen y velocidad
        var volSlider = new Slider { Minimum = 0, Maximum = 100, Value = tts.Volume, SmallChange = 5, LargeChange = 10, Width = 160 };
        var volLabel = new TextBlock { Text = $"{tts.Volume}%", Width = 45, VerticalAlignment = VerticalAlignment.Center };
        volSlider.ValueChanged += (_, _) =>
        {
            var val = (int)volSlider.Value;
            tts.Volume = val;
            volLabel.Text = $"{val}%";
        };

        var rateSlider = new Slider { Minimum = -10, Maximum = 10, Value = tts.Rate, SmallChange = 1, LargeChange = 2, Width = 160 };
        var rateLabel = new TextBlock { Text = $"{tts.Rate}", Width = 45, VerticalAlignment = VerticalAlignment.Center };
        rateSlider.ValueChanged += (_, _) =>
        {
            var val = (int)rateSlider.Value;
            tts.Rate = val;
            rateLabel.Text = val > 0 ? $"+{val}" : $"{val}";
        };

        var pitchSlider = new Slider { Minimum = -10, Maximum = 10, Value = tts.Pitch, SmallChange = 1, LargeChange = 2, Width = 160 };
        var pitchLabel = new TextBlock { Text = $"{tts.Pitch}", Width = 45, VerticalAlignment = VerticalAlignment.Center };
        pitchSlider.ValueChanged += (_, _) =>
        {
            var val = (int)pitchSlider.Value;
            tts.Pitch = val;
            pitchLabel.Text = val > 0 ? $"+{val}" : $"{val}";
        };

        var maxLenNum = Number(tts.MaxLength, 30, 500, 10);
        maxLenNum.ValueChanged += (_, _) =>
        {
            if (maxLenNum.Value is { } v)
                tts.MaxLength = (int)v;
        };

        // Selector de modo de acción al pulsar un comentario
        var clickActionCombo = new ComboBox
        {
            ItemsSource = new[]
            {
                "Solo leer en voz alta con TTS",
                "Solo mostrar alerta en pantalla (fijar)",
                "Mostrar en pantalla y leer simultáneamente",
                "Mostrar en pantalla solo después de que se leyó por voz",
                "Leer por voz solo después de que se mostró en pantalla"
            },
            SelectedIndex = (int)tts.ClickAction,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        clickActionCombo.SelectionChanged += (_, _) =>
        {
            if (clickActionCombo.SelectedIndex >= 0)
                tts.ClickAction = (ChatClickAction)clickActionCombo.SelectedIndex;
        };

        // Frase de prueba
        var testBox = new TextBox { Text = tts.TestPhrase, HorizontalAlignment = HorizontalAlignment.Stretch };
        testBox.TextChanged += (_, _) => tts.TestPhrase = testBox.Text ?? "";

        var testBtn = new Button { Content = "Probar voz", Margin = new Thickness(8, 0, 0, 0) };
        testBtn.Click += (_, _) => tts.TestSpeakCommand.Execute(null);

        var testRow = new DockPanel { Children = { testBtn, testBox } };
        DockPanel.SetDock(testBtn, Dock.Right);

        // Selector de voces por plataforma
        ComboBox CreatePlatformVoiceCombo(TtsVoice? initial, Action<TtsVoice?> onSelected)
        {
            var cb = new ComboBox
            {
                ItemsSource = tts.AvailableVoices,
                SelectedItem = initial,
                Width = 220,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            cb.SelectionChanged += (_, _) => onSelected(cb.SelectedItem as TtsVoice);
            return cb;
        }

        var ytVoiceCombo = CreatePlatformVoiceCombo(tts.SelectedYouTubeVoice, v => tts.SelectedYouTubeVoice = v);
        var ttVoiceCombo = CreatePlatformVoiceCombo(tts.SelectedTikTokVoice, v => tts.SelectedTikTokVoice = v);
        var twVoiceCombo = CreatePlatformVoiceCombo(tts.SelectedTwitchVoice, v => tts.SelectedTwitchVoice = v);
        var fbVoiceCombo = CreatePlatformVoiceCombo(tts.SelectedFacebookVoice, v => tts.SelectedFacebookVoice = v);

        // Reglas de plataformas
        var ytCheck = new CheckBox { Content = "YouTube Live", IsChecked = tts.YoutubeEnabled };
        ytCheck.IsCheckedChanged += (_, _) => tts.YoutubeEnabled = ytCheck.IsChecked == true;
        var ytRow = new DockPanel { Children = { ytVoiceCombo, ytCheck } };
        DockPanel.SetDock(ytVoiceCombo, Dock.Right);

        var ttCheck = new CheckBox { Content = "TikTok LIVE", IsChecked = tts.TiktokEnabled };
        ttCheck.IsCheckedChanged += (_, _) => tts.TiktokEnabled = ttCheck.IsChecked == true;
        var ttRow = new DockPanel { Children = { ttVoiceCombo, ttCheck } };
        DockPanel.SetDock(ttVoiceCombo, Dock.Right);

        var twCheck = new CheckBox { Content = "Twitch", IsChecked = tts.TwitchEnabled };
        twCheck.IsCheckedChanged += (_, _) => tts.TwitchEnabled = twCheck.IsChecked == true;
        var twRow = new DockPanel { Children = { twVoiceCombo, twCheck } };
        DockPanel.SetDock(twVoiceCombo, Dock.Right);

        var twSubsCheck = new CheckBox { Content = "Solo suscriptores en Twitch", IsChecked = tts.TwitchSubsOnly, Margin = new Thickness(24, 0, 0, 0) };
        twSubsCheck.IsCheckedChanged += (_, _) => tts.TwitchSubsOnly = twSubsCheck.IsChecked == true;

        var fbCheck = new CheckBox { Content = "Facebook Live", IsChecked = tts.FacebookEnabled };
        fbCheck.IsCheckedChanged += (_, _) => tts.FacebookEnabled = fbCheck.IsChecked == true;
        var fbRow = new DockPanel { Children = { fbVoiceCombo, fbCheck } };
        DockPanel.SetDock(fbVoiceCombo, Dock.Right);

        // Control de sonido de alertas globales
        var soundAlertCheck = new CheckBox { Content = "Sonidos acústicos de alertas globales habilitados", IsChecked = !vm.Alerts.IsSoundMuted };
        soundAlertCheck.IsCheckedChanged += (_, _) => vm.Alerts.IsSoundMuted = soundAlertCheck.IsChecked != true;

        // Visualización de estado actual
        var nowPlayingBorder = new Border
        {
            Background = Brush.Parse("#FF1E293B"),
            BorderBrush = Brush.Parse("#FF3B82F6"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12),
        };
        var nowPlayingTitle = new TextBlock { Text = "En reproducción:", FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#FF93C5FD") };
        var nowPlayingBody = new TextBlock { Text = tts.CurrentText ?? "(Ningún mensaje en lectura actualmente)", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
        nowPlayingBorder.Child = new StackPanel { Spacing = 4, Children = { nowPlayingTitle, nowPlayingBody } };

        tts.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(tts.CurrentText) || e.PropertyName == nameof(tts.IsPlaying))
            {
                nowPlayingBody.Text = tts.IsPlaying && !string.IsNullOrEmpty(tts.CurrentText)
                    ? $"[{tts.CurrentPlatform}] {tts.CurrentAuthor}: {tts.CurrentText}"
                    : "(Ningún mensaje en lectura actualmente)";
            }
        };

        var mainTab = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16),
            Children =
            {
                Section("Control de reproducción y estado"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { enabledCheck, autoReadCheck } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pauseBtn, skipBtn, muteBtn, clearBtn } },
                nowPlayingBorder,

                Section("Voz y entonación"),
                Form(
                    ("Voz principal instalada", voiceCombo),
                    ("Volumen", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { volSlider, volLabel } }),
                    ("Velocidad", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { rateSlider, rateLabel } }),
                    ("Tono acústico (Pitch)", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { pitchSlider, pitchLabel } }),
                    ("Límite de caracteres", maxLenNum),
                    ("Acción al pulsar comentario", clickActionCombo),
                    ("Frase de prueba", testRow)
                ),

                Section("Filtros y voces por plataforma"),
                new TextBlock { Text = "Asigna una voz personalizada independiente por red social o usa la voz principal:", Classes = { "muted" }, FontSize = 11 },
                new StackPanel { Spacing = 6, Children = { ytRow, ttRow, twRow, twSubsCheck, fbRow } },

                Section("Sonidos acústicos de alertas"),
                soundAlertCheck
            }
        };

        var queueList = new ListBox
        {
            ItemsSource = tts.QueueItems,
            MinHeight = 220,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TtsUtterance>((utterance, _) =>
            {
                var panel = new DockPanel { Margin = new Thickness(4) };
                var del = new Button { Content = "Quitar", Padding = new Thickness(6, 2) };
                DockPanel.SetDock(del, Dock.Right);
                del.Click += (_, _) => tts.RemoveQueueItem(utterance);
                var text = new TextBlock
                {
                    Text = $"[{(utterance.IsManual ? "Manual" : utterance.Platform?.ToString() ?? "Estudio")}] {utterance.Author ?? "Anónimo"}: {utterance.Text}",
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                panel.Children.Add(del);
                panel.Children.Add(text);
                return panel;
            }),
        };

        var queueTab = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16),
            Children =
            {
                new TextBlock { Text = "Cola de mensajes pendientes de lectura por voz:", Classes = { "muted" } },
                queueList,
            }
        };

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Ajustes de voz y filtros", Content = new ScrollViewer { Content = mainTab } });
        tabs.Items.Add(new TabItem { Header = "Cola de reproducción", Content = new ScrollViewer { Content = queueTab } });

        var close = Primary("Cerrar");
        close.Click += (_, _) => window.Close();
        window.Content = Layout(tabs, close);
        await window.ShowDialog(owner);
    }

    // ---- Alertas de Donaciones, Regalos y Puntos de Canal ----------------------------------------

    public static async Task ShowMonetaryAlertsAsync(MainViewModel vm)
    {
        if (vm.Dialogs.Owner is not { } owner)
            return;

        var alertsVm = vm.MonetaryAlerts;
        alertsVm.RefreshRules();
        alertsVm.RefreshHistory();

        var window = NewWindow("Donaciones, regalos y recompensas", 840, 680);

        // Lista de reglas a la izquierda
        var rulesList = new ListBox
        {
            ItemsSource = alertsVm.Rules,
            SelectedItem = alertsVm.SelectedRule,
            Width = 240,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MonetaryAlertRule>((rule, _) =>
                new TextBlock { Text = $"{rule.Name} ({rule.Platform} - {rule.Kind})" }),
        };
        rulesList.SelectionChanged += (_, _) =>
        {
            if (rulesList.SelectedItem is MonetaryAlertRule r)
                alertsVm.SelectedRule = r;
        };

        var addRuleBtn = new Button { Content = "Nueva regla", HorizontalAlignment = HorizontalAlignment.Stretch };
        addRuleBtn.Click += (_, _) =>
        {
            alertsVm.RuleName = "Nueva regla " + (alertsVm.Rules.Count + 1);
            alertsVm.RuleMinAmount = 5.0m;
            alertsVm.RuleSpeechTemplate = "¡Muchas gracias {username} por tu apoyo de {amount} {currency}!";
            alertsVm.SaveCurrentRule();
        };

        var delRuleBtn = new Button { Content = "Eliminar regla", HorizontalAlignment = HorizontalAlignment.Stretch };
        delRuleBtn.Click += (_, _) => alertsVm.DeleteCurrentRule();

        var testRuleBtn = Primary("Probar alerta en directo");
        testRuleBtn.HorizontalAlignment = HorizontalAlignment.Stretch;
        testRuleBtn.Click += (_, _) => alertsVm.TestCurrentAlert();

        var leftBar = new DockPanel
        {
            Children =
            {
                new StackPanel { Spacing = 6, Children = { addRuleBtn, delRuleBtn, testRuleBtn }, Margin = new Thickness(0, 8, 0, 0) },
                rulesList
            }
        };
        DockPanel.SetDock(leftBar.Children[0], Dock.Bottom);

        // Editor de regla a la derecha
        var nameBox = new TextBox { Text = alertsVm.RuleName };
        nameBox.TextChanged += (_, _) => alertsVm.RuleName = nameBox.Text ?? "";

        var platformCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues<StreamPlatform>(),
            SelectedItem = alertsVm.RulePlatform,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 160,
        };
        platformCombo.SelectionChanged += (_, _) =>
        {
            if (platformCombo.SelectedItem is StreamPlatform p) alertsVm.RulePlatform = p;
        };

        var kindCombo = new ComboBox
        {
            ItemsSource = Enum.GetValues<AlertKind>(),
            SelectedItem = alertsVm.RuleKind,
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 160,
        };
        kindCombo.SelectionChanged += (_, _) =>
        {
            if (kindCombo.SelectedItem is AlertKind k) alertsVm.RuleKind = k;
        };

        var minAmountNum = Number((double)alertsVm.RuleMinAmount, 0, 100000, 1, "0.##");
        minAmountNum.ValueChanged += (_, _) =>
        {
            if (minAmountNum.Value is { } v) alertsVm.RuleMinAmount = (decimal)v;
        };

        var currBox = new TextBox { Text = alertsVm.RuleCurrency, Width = 100, HorizontalAlignment = HorizontalAlignment.Left };
        currBox.TextChanged += (_, _) => alertsVm.RuleCurrency = currBox.Text ?? "";

        var tplBox = new TextBox
        {
            Text = alertsVm.RuleSpeechTemplate,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 80,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        tplBox.TextChanged += (_, _) => alertsVm.RuleSpeechTemplate = tplBox.Text ?? "";

        var enabledCheck = new CheckBox { Content = "Regla activada", IsChecked = alertsVm.RuleEnabled };
        enabledCheck.IsCheckedChanged += (_, _) => alertsVm.RuleEnabled = enabledCheck.IsChecked == true;

        var saveRuleBtn = Primary("Guardar regla");
        saveRuleBtn.Click += (_, _) => alertsVm.SaveCurrentRule();

        alertsVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(alertsVm.SelectedRule))
            {
                nameBox.Text = alertsVm.RuleName;
                platformCombo.SelectedItem = alertsVm.RulePlatform;
                kindCombo.SelectedItem = alertsVm.RuleKind;
                minAmountNum.Value = (decimal)alertsVm.RuleMinAmount;
                currBox.Text = alertsVm.RuleCurrency;
                tplBox.Text = alertsVm.RuleSpeechTemplate;
                enabledCheck.IsChecked = alertsVm.RuleEnabled;
            }
        };

        var rightEditor = new StackPanel
        {
            Spacing = 10,
            Margin = new Thickness(12, 0, 0, 0),
            Children =
            {
                Section("Propiedades de la regla seleccionada"),
                Form(
                    ("Nombre descriptivo", nameBox),
                    ("Plataforma", platformCombo),
                    ("Tipo de evento monetario", kindCombo),
                    ("Importe o unidades mínimas", minAmountNum),
                    ("Código de divisa o unidad", currBox),
                    ("Plantilla de voz (TTS)", tplBox)
                ),
                new TextBlock
                {
                    Text = "Variables disponibles en la plantilla: {username}, {amount}, {currency}, {gift_name}, {gift_count}, {message}",
                    Classes = { "muted" },
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                },
                enabledCheck,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 10, 0, 0), Children = { saveRuleBtn } }
            }
        };

        var rulesGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("250,*"),
            Margin = new Thickness(12),
        };
        Grid.SetColumn(leftBar, 0);
        Grid.SetColumn(rightEditor, 1);
        rulesGrid.Children.Add(leftBar);
        rulesGrid.Children.Add(rightEditor);

        // Pestaña 2: Puntos de canal
        var pointsCheck = new CheckBox { Content = "Activar sistema de puntos virtuales de canal", IsChecked = alertsVm.PointsEnabled };
        pointsCheck.IsCheckedChanged += (_, _) => alertsVm.PointsEnabled = pointsCheck.IsChecked == true;

        var pointNameBox = new TextBox { Text = alertsVm.PointName, Width = 180, HorizontalAlignment = HorizontalAlignment.Left };
        pointNameBox.TextChanged += (_, _) => alertsVm.PointName = pointNameBox.Text ?? "";

        var ptsPerCurr = Number((double)alertsVm.PointsPerCurrency, 0, 10000, 10);
        ptsPerCurr.ValueChanged += (_, _) => { if (ptsPerCurr.Value is { } v) alertsVm.PointsPerCurrency = (decimal)v; };

        var ptsPerSub = Number(alertsVm.PointsPerSub, 0, 100000, 50);
        ptsPerSub.ValueChanged += (_, _) => { if (ptsPerSub.Value is { } v) alertsVm.PointsPerSub = (int)v; };

        var ptsPerGift = Number(alertsVm.PointsPerGift, 0, 100000, 10);
        ptsPerGift.ValueChanged += (_, _) => { if (ptsPerGift.Value is { } v) alertsVm.PointsPerGift = (int)v; };

        var historyList = new ListBox
        {
            ItemsSource = alertsVm.PointsHistory,
            MinHeight = 220,
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<VirtualPointsEntry>((entry, _) =>
                new TextBlock
                {
                    Text = $"{entry.Timestamp:HH:mm:ss} — +{entry.PointsAwarded} {alertsVm.PointName} para {entry.Username} ({entry.Platform}) por {entry.Reason}",
                    VerticalAlignment = VerticalAlignment.Center,
                }),
        };

        var pointsTab = new StackPanel
        {
            Spacing = 12,
            Margin = new Thickness(16),
            Children =
            {
                Section("Economía virtual de canal (Sin valor fiat)"),
                new TextBlock
                {
                    Text = "Los puntos de canal incentivan la participación de la comunidad y se acreditan automáticamente por donaciones, membresías o regalos.",
                    Classes = { "muted" },
                    TextWrapping = TextWrapping.Wrap,
                },
                pointsCheck,
                Form(
                    ("Nombre de los puntos", pointNameBox),
                    ("Puntos por unidad monetaria (ej. 1 USD)", ptsPerCurr),
                    ("Puntos por suscripción / miembro", ptsPerSub),
                    ("Puntos por regalo virtual", ptsPerGift)
                ),
                Section("Historial de puntos otorgados"),
                historyList
            }
        };

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Reglas y alertas monetarias", Content = rulesGrid });
        tabs.Items.Add(new TabItem { Header = "Puntos de canal y fidelidad", Content = new ScrollViewer { Content = pointsTab } });

        var close = Primary("Cerrar");
        close.Click += (_, _) => window.Close();
        window.Content = Layout(tabs, close);
        await window.ShowDialog(owner);
    }

    private static Scene? FindScene(this StudioProject project, Guid id) => project.Scenes.FirstOrDefault(s => s.Id == id);
}

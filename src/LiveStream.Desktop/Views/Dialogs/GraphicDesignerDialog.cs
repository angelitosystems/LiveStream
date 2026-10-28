using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using LiveStream.Application.Commands;
using LiveStream.Application.Graphics;
using LiveStream.Domain.Animation;
using LiveStream.Desktop.Components;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.Graphics;
using LiveStream.Infrastructure.Persistence;
using static LiveStream.Desktop.Views.Dialogs.StudioDialogs;

namespace LiveStream.Desktop.Views.Dialogs;

/// <summary>
/// Editor visual de gráficos: lienzo con selección (clic, recuadro, Mayús), arrastre, redimensionado y giro con
/// guías inteligentes y cuadrícula, lista de capas, propiedades del elemento, alineación, distribución, capas,
/// biblioteca de recursos, plantillas y exportación .lsgfx. El proyecto sigue siendo la fuente de verdad: cada
/// gesto entra como un paso del historial de deshacer; durante el arrastre se aplica una vista previa en directo
/// (sin pasos) y al soltar se guarda el gesto entero con su estado previo, así Ctrl+Z lo revierte de una vez.
/// </summary>
public sealed class GraphicDesignerDialog
{
    public static Task ShowAsync(MainViewModel vm, Guid graphicId) => new GraphicDesignerDialog(vm, graphicId).RunAsync();

    internal static IReadOnlyList<KeyValuePair<GraphicAnimation, string>> AnimationNames() =>
    [
        new(GraphicAnimation.Cut, "Corte"),
        new(GraphicAnimation.Fade, "Fundido"),
        new(GraphicAnimation.SlideFromLeft, "Deslizar desde la izquierda"),
        new(GraphicAnimation.SlideFromRight, "Deslizar desde la derecha"),
        new(GraphicAnimation.SlideFromBottom, "Deslizar desde abajo"),
        new(GraphicAnimation.SlideFromTop, "Deslizar desde arriba"),
        new(GraphicAnimation.Wipe, "Cortinilla (revelado)"),
        new(GraphicAnimation.Pop, "Aparición con rebote"),
    ];

    private GraphicDesignerDialog(MainViewModel vm, Guid graphicId)
    {
        _vm = vm;
        _id = graphicId;
        _committed = vm.Project.GetGraphic(graphicId);
        _previewTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, PreviewTick);
        _liveTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background, LiveTick);
    }

    private enum TextKind { Plain, Clock, Countdown }

    private readonly MainViewModel _vm;
    private readonly Guid _id;
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _liveTimer;

    private Window _window = null!;
    private DesignCanvas _canvas = null!;
    private ListBox _layers = null!;
    private StackPanel _properties = null!;
    private TextBlock _error = null!;
    private TextBlock _status = null!;
    private TextBlock _zoom = null!;

    /// <summary>Estado que coincide con el proyecto y el motor (las vistas previas también lo actualizan).</summary>
    private Graphic _committed;
    /// <summary>Estado al empezar el gesto actual: deshacer vuelve aquí aunque el gesto usara vistas previas.</summary>
    private Graphic? _gestureBase;
    private Graphic? _pendingPreview;
    private bool _previewBusy;
    private bool _suppress;
    private bool _interacting;

    private IReadOnlyList<DesignElement> Design => _canvas.Design;

    // ---- Construcción de la ventana -------------------------------------------------------------

    private async Task RunAsync()
    {
        _window = NewWindow($"Diseño — {_committed.Name}", 1320, 860);
        _window.MinWidth = 1000;
        _window.MinHeight = 620;
        _window.Closed += OnWindowClosed;
        _vm.Executor.StateChanged += OnStateChanged;

        _canvas = new DesignCanvas { CanvasSize = new Size(_vm.Project.Canvas.Width, _vm.Project.Canvas.Height) };
        _canvas.TextOf = LiveTextOf;
        _canvas.DesignChanged += OnDesignChanged;
        _canvas.SelectionChanged += OnCanvasSelectionChanged;
        _canvas.GestureStarted += OnGestureStarted;
        _canvas.ZoomChanged += RefreshZoomLabel;
        _canvas.ElementDoubleClicked += OnElementDoubleClicked;
        _canvas.SetDesign(_committed.Design ?? []);
        RefreshCanvasPlacement();

        _layers = new ListBox { Classes = { "studioList" }, SelectionMode = SelectionMode.Multiple };
        _layers.SelectionChanged += OnLayerSelected;
        // Avalonia puede reconstruir la plantilla con un elemento null al reciclar contenedores
        // (p. ej. al refrescar ItemsSource): devolvemos una fila vacía en vez de fallar.
        _layers.ItemTemplate = new FuncDataTemplate<DesignElement>((element, _) =>
            element is null ? new Panel { MinHeight = 28 } : LayerRow(element), false);

        _properties = new StackPanel { Spacing = 10 };
        _error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        _status = new TextBlock { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };

        _window.Content = BuildLayout(BuildToolbar());
        _window.KeyDown += OnKeyDown;

        RefreshAll();
        _liveTimer.Start();
        if (_vm.Dialogs.Owner is not { } owner)
        {
            _vm.Executor.StateChanged -= OnStateChanged;
            return;
        }
        await _window.ShowDialog(owner);
    }

    private Control BuildToolbar()
    {
        var add = MenuButton("Icon.Plus", "Añadir", AddMenu());
        var align = MenuButton("Icon.Sliders", "Alinear", AlignMenu());
        var distribute = MenuButton("Icon.Grid", "Distribuir", DistributeMenu());
        var order = MenuButton("Icon.Layers", "Capa", OrderMenu());

        var duplicate = IconButton("Icon.Copy", "Duplicar (Ctrl+D)");
        duplicate.Click += (_, _) => Mix(keys =>
        {
            var (design, keep) = DesignOps.Duplicate(Design, keys);
            return (design, keep);
        });
        var delete = IconButton("Icon.Trash", "Eliminar (Supr)");
        delete.Classes.Add("danger");
        delete.Click += (_, _) => Mix(keys => (Design.Where(e => !keys.Contains(e.Key)).ToList(), Array.Empty<string>()));

        var undo = IconButton("Icon.Undo", "Deshacer (Ctrl+Z)");
        undo.Click += (_, _) => _ = _vm.Executor.UndoAsync();
        var redo = IconButton("Icon.Redo", "Rehacer (Ctrl+Y)");
        redo.Click += (_, _) => _ = _vm.Executor.RedoAsync();

        _zoom = new TextBlock { Text = "Ajustar", VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" }, MinWidth = 52, TextAlignment = TextAlignment.Center };
        var fit = IconButton("Icon.Fit", "Encuadrar (Ctrl+0)");
        fit.Click += (_, _) => _canvas.ResetView();
        var grid = IconToggle("Icon.Grid", "Cuadrícula", _canvas.ShowGrid);
        grid.Click += (_, _) => _canvas.ShowGrid = grid.IsChecked == true;
        var snap = IconToggle("Icon.Magnet", "Guías magnéticas", _canvas.SnapEnabled);
        snap.Click += (_, _) => _canvas.SnapEnabled = snap.IsChecked == true;

        var play = IconButton("Icon.Play", "Vista previa de animación: entrada + permanencia + salida");
        play.Click += (_, _) => StartPreview();
        var stopPreview = IconButton("Icon.Stop", "Detener la vista previa");
        stopPreview.Click += (_, _) => StopPreview();

        var library = TextIconButton("Icon.Template", "Biblioteca…", "Recursos guardados: imágenes y videos reutilizables");
        library.Click += (_, _) => _ = ResourceLibraryDialog.ShowBrowserAsync(_vm);
        var export = TextIconButton("Icon.Send", "Exportar…", "Guardar el gráfico como archivo .lsgfx");
        export.Click += (_, _) => _ = _vm.ExportGraphicAsync(_id);
        var template = TextIconButton("Icon.Template", "Plantilla…", "Guardar como plantilla reutilizable");
        template.Click += (_, _) => _ = SaveTemplateAsync();

        var bar = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 6),
        };
        void Add(Control control)
        {
            control.Margin = new Thickness(0, 0, 4, 2);
            bar.Children.Add(control);
        }
        foreach (var section in new[]
                 {
                     new Control[] { add, align, distribute, order },
                     new Control[] { duplicate, delete },
                     new Control[] { undo, redo },
                     new Control[] { fit, _zoom, grid, snap },
                     new Control[] { play, stopPreview },
                     new Control[] { library, export, template },
                 })
        {
            foreach (var control in section)
                Add(control);
            Add(ToolbarSeparator());
        }

        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF16181E")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = bar,
        };
    }

    private static Button MenuButton(string iconKey, string text, MenuFlyout flyout)
    {
        var button = new Button
        {
            Padding = new Thickness(8, 4),
            Height = 30,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Glyph { Data = Geometry(iconKey), Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        FlyoutBase.SetAttachedFlyout(button, flyout);
        button.Click += (_, _) => FlyoutBase.ShowAttachedFlyout(button);
        return button;
    }

    private static Button TextIconButton(string iconKey, string text, string tooltip)
    {
        var button = new Button
        {
            Padding = new Thickness(8, 4),
            Height = 30,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children =
                {
                    new Glyph { Data = Geometry(iconKey), Width = 14, Height = 14, VerticalAlignment = VerticalAlignment.Center },
                    new TextBlock { Text = text, FontSize = 12, VerticalAlignment = VerticalAlignment.Center },
                },
            },
        };
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static Control ToolbarSeparator() =>
        new Border { Height = 20, Width = 1, Margin = new Thickness(2, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center, Background = new SolidColorBrush(Color.Parse("#FF3A3F4D")) };

    private static Button IconButton(string iconKey, string tooltip)
    {
        var button = new Button
        {
            Classes = { "icon" },
            Content = new Glyph { Data = Geometry(iconKey), Width = 16, Height = 16 },
        };
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private static ToggleButton IconToggle(string iconKey, string tooltip, bool isChecked)
    {
        var button = new ToggleButton
        {
            Classes = { "icon" },
            Content = new Glyph { Data = Geometry(iconKey), Width = 16, Height = 16 },
            IsChecked = isChecked,
        };
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        return button;
    }

    private MenuFlyout AddMenu()
    {
        var menu = new MenuFlyout();
        Item(menu, "Texto", () => AddElement(DesignElementKind.Text));
        Item(menu, "Texto vivo (reloj)", () => AddElement(DesignElementKind.Text, text: TextKind.Clock));
        Item(menu, "Texto vivo (cuenta atrás)", () => AddElement(DesignElementKind.Text, text: TextKind.Countdown));
        menu.Items.Add(new Separator());
        Item(menu, "Forma (rectángulo)", () => AddElement(DesignElementKind.Rect));
        Item(menu, "Imagen…", () => _ = PickResourceAsync(ResourceKind.Image));
        Item(menu, "Video…", () => _ = PickResourceAsync(ResourceKind.Video));
        menu.Items.Add(new Separator());
        Item(menu, "Biblioteca de recursos…", () =>
        {
            var picker = ResourceLibraryDialog.ShowPickerAsync(_vm, _window);
            _ = ShowLayersPickerAsync(picker);
        });
        return menu;
    }

    private async Task ShowLayersPickerAsync(Task<string?> pick)
    {
        if (await pick.ConfigureAwait(true) is { } path)
            await Dispatcher.UIThread.InvokeAsync(() => AddResourceAsync(path));
    }

    private MenuFlyout AlignMenu()
    {
        var menu = new MenuFlyout();
        Item(menu, "A la izquierda", () => Edit(keys => DesignOps.Align(Design, keys, DesignAlign.Left, _canvas.CanvasInDesign)));
        Item(menu, "Centro horizontal", () => Edit(keys => DesignOps.Align(Design, keys, DesignAlign.CenterH, _canvas.CanvasInDesign)));
        Item(menu, "A la derecha", () => Edit(keys => DesignOps.Align(Design, keys, DesignAlign.Right, _canvas.CanvasInDesign)));
        Item(menu, "Arriba", () => Edit(keys => DesignOps.Align(Design, keys, DesignAlign.Top, _canvas.CanvasInDesign)));
        Item(menu, "Al medio (vertical)", () => Edit(keys => DesignOps.Align(Design, keys, DesignAlign.Middle, _canvas.CanvasInDesign)));
        Item(menu, "Abajo", () => Edit(keys => DesignOps.Align(Design, keys, DesignAlign.Bottom, _canvas.CanvasInDesign)));
        return menu;
    }

    private MenuFlyout DistributeMenu()
    {
        var menu = new MenuFlyout();
        Item(menu, "Horizontal", () => Edit(keys => DesignOps.Distribute(Design, keys, horizontal: true)));
        Item(menu, "Vertical", () => Edit(keys => DesignOps.Distribute(Design, keys, horizontal: false)));
        return menu;
    }

    private MenuFlyout OrderMenu()
    {
        var menu = new MenuFlyout();
        Item(menu, "Al frente", () => Mix(keys => (DesignOps.ToEdge(Design, keys, front: true), keys)));
        Item(menu, "Al fondo", () => Mix(keys => (DesignOps.ToEdge(Design, keys, front: false), keys)));
        menu.Items.Add(new Separator());
        Item(menu, "Subir una capa", () => Mix(keys => (DesignOps.Reorder(Design, keys, +1), keys)));
        Item(menu, "Bajar una capa", () => Mix(keys => (DesignOps.Reorder(Design, keys, -1), keys)));
        return menu;
    }

    private Control BuildLayout(Control toolbar)
    {
        // Panel de capas: el relleno disponible lo adopta la lista (Grid y no StackPanel),
        // así usa su propio scroll y se puede redimensionar con el separador.
        var layersPane = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 10, 4, 12) };
        layersPane.Children.Add(new TextBlock { Text = "Capas (arriba = delante)", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) });
        Grid.SetRow(_layers, 1);
        layersPane.Children.Add(_layers);

        var propsPane = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), Margin = new Thickness(4, 10, 12, 12) };
        propsPane.Children.Add(new TextBlock { Text = "Inspector de propiedades", FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 8) });
        var propsScroll = new ScrollViewer { Content = _properties };
        Grid.SetRow(propsScroll, 1);
        propsPane.Children.Add(propsScroll);

        var canvas = new Border { Child = _canvas, ClipToBounds = true };
        var splitter1 = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = new SolidColorBrush(Color.Parse("#FF2A2E3A")), Margin = new Thickness(2, 0) };
        var splitter2 = new GridSplitter { ResizeDirection = GridResizeDirection.Columns, Background = new SolidColorBrush(Color.Parse("#FF2A2E3A")), Margin = new Thickness(2, 0) };

        var close = new Button { Content = "Cerrar", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        close.Click += (_, _) => _window?.Close();
        var footerStack = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        footerStack.Children.Add(_error);
        footerStack.Children.Add(_status);
        var footer = new DockPanel { Margin = new Thickness(16, 8) };
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        footer.Children.Add(footerStack);

        var columnDefs = new ColumnDefinitions("210,6,*,6,310");
        // Mínimos funcionales: las listas y el inspector nunca se estrechan por debajo de lo legible.
        columnDefs[0].MinWidth = 160;
        columnDefs[4].MinWidth = 250;
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            ColumnDefinitions = columnDefs,
        };
        Grid.SetColumnSpan(toolbar, 5);
        Grid.SetRow(layersPane, 1);
        Grid.SetColumn(layersPane, 0);
        Grid.SetRow(splitter1, 1);
        Grid.SetColumn(splitter1, 1);
        Grid.SetRow(canvas, 1);
        Grid.SetColumn(canvas, 2);
        Grid.SetRow(splitter2, 1);
        Grid.SetColumn(splitter2, 3);
        Grid.SetRow(propsPane, 1);
        Grid.SetColumn(propsPane, 4);
        Grid.SetRow(footer, 2);
        Grid.SetColumnSpan(footer, 5);

        grid.Children.Add(toolbar);
        grid.Children.Add(layersPane);
        grid.Children.Add(splitter1);
        grid.Children.Add(canvas);
        grid.Children.Add(splitter2);
        grid.Children.Add(propsPane);
        grid.Children.Add(footer);
        return grid;
    }

    private static Button FlyoutButton(string text, MenuFlyout flyout)
    {
        var button = new Button { Content = text };
        FlyoutBase.SetAttachedFlyout(button, flyout);
        return button;
    }

    private static void Item(MenuFlyout menu, string header, Action click)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => click();
        menu.Items.Add(item);
    }

    private static TextBlock Muted(string text) => new() { Text = text, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 11 };

    // ---- Estado del proyecto --------------------------------------------------------------------

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
            ReloadFromProject();
        else
            Dispatcher.UIThread.Post(ReloadFromProject);
    }

    private void ReloadFromProject()
    {
        if (_vm.Project.FindGraphic(_id) is not { } graphic)
        {
            _window.Title = "Diseño — eliminado";
            _properties.Children.Clear();
            _properties.Children.Add(Muted("El gráfico ya no existe (probablemente se deshizo). Cierra esta ventana."));
            return;
        }
        _committed = graphic;
        if (_interacting)
            return; // la sincronización del gesto termina en CommitAsync.
        _window.Title = $"Diseño — {graphic.Name}";
        if (!SameDesign(Design, graphic.Design))
            _canvas.SetDesign(graphic.Design ?? []);
        RefreshCanvasPlacement();
        RefreshAll();
    }

    private static bool SameDesign(IReadOnlyList<DesignElement> a, IReadOnlyList<DesignElement>? b) =>
        a.SequenceEqual(b ?? Array.Empty<DesignElement>());

    private void RefreshCanvasPlacement()
    {
        _canvas.GraphicScale = GraphicTemplates.Unit(_vm.Project.Canvas) * _committed.Scale;
        _canvas.Origin = new Point(_committed.X, _committed.Y);
        _canvas.InvalidateVisual();
    }

    // ---- Aplicación de cambios ------------------------------------------------------------------

    private void OnGestureStarted()
    {
        StopPreview(); // editar durante la reproducción daría saltos: se detiene la vista previa.
        _gestureBase = _committed;
        _interacting = true;
    }

    private void OnDesignChanged(IReadOnlyList<DesignElement> design, bool commit)
    {
        if (_suppress)
            return;
        var updated = _committed with { Design = design.ToArray() };
        if (!commit)
        {
            _pendingPreview = updated;
            if (!_previewTimer.IsEnabled)
                _previewTimer.Start();
            return;
        }
        _ = CommitAsync(updated);
    }

    private void PreviewTick(object? sender, EventArgs e)
    {
        if (_previewBusy)
            return;
        if (_pendingPreview is not { } pending)
        {
            _previewTimer.Stop();
            return;
        }
        _pendingPreview = null;
        _ = PreviewAsync(pending);
    }

    private async Task PreviewAsync(Graphic updated)
    {
        _previewBusy = true;
        try
        {
            var result = await _vm.Run(new PreviewGraphicCommand(updated));
            _error.Text = result.Success ? "" : result.Error;
            if (result.Success)
                _committed = updated;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _error.Text = ex.Message;
        }
        finally
        {
            _previewBusy = false;
        }
    }

    private async Task CommitAsync(Graphic updated)
    {
        _previewTimer.Stop();
        _pendingPreview = null;
        _interacting = false;
        try
        {
            var result = await _vm.Run(new UpdateGraphicCommand(updated, previous: _gestureBase));
            _error.Text = result.Success ? "" : result.Error;
            _gestureBase = null;
            if (!result.Success)
                ReloadFromProject();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _error.Text = ex.Message;
            _gestureBase = null;
            ReloadFromProject();
        }
    }

    private async Task ApplyAsync(IReadOnlyList<DesignElement> design, IReadOnlyList<string>? keepKeys)
    {
        if (keepKeys is not null)
            _canvas.Select(keepKeys);
        await CommitAsync(_committed with { Design = design.ToArray() });
    }

    private void Edit(Func<IReadOnlyList<string>, IReadOnlyList<DesignElement>> edit) => Mix(keys => (edit(keys), keys));

    private void Mix(Func<IReadOnlyList<string>, (IReadOnlyList<DesignElement> Design, IReadOnlyList<string> Keep)> edit)
    {
        var keys = _canvas.Selection.ToList();
        if (keys.Count == 0)
            return;
        var (design, keep) = edit(keys);
        _ = ApplyAsync(design, keep);
    }

    /// <summary>Cambia el gráfico entero (nombre, animación, escala, datos…): un paso de deshacer.</summary>
    private async Task UpdateGraphicAsync(Func<Graphic, Graphic> change)
    {
        var result = await _vm.Run(new UpdateGraphicCommand(change(_committed)));
        _error.Text = result.Success ? "" : result.Error;
        if (!result.Success)
            ReloadFromProject();
    }

    // ---- Elementos ------------------------------------------------------------------------------

    private void AddElement(DesignElementKind kind, string? path = null, TextKind text = TextKind.Plain)
    {
        var binding = text switch
        {
            TextKind.Clock => DesignBinding.Clock,
            TextKind.Countdown => DesignBinding.Countdown,
            _ => DesignBinding.None,
        };
        var center = _canvas.ViewCenterInDesign;
        var element = DesignOps.Create(kind, Design, center, _vm.Brand.Load().Style, path, binding);
        if (path is not null)
        {
            var (w, h) = KindSize(kind, path);
            element = element with { Width = w, Height = h };
        }
        var design = Design.ToList();
        design.Add(element);
        _ = ApplyAsync(design, [element.Key]);
    }

    private static (double W, double H) KindSize(DesignElementKind kind, string? path)
    {
        if (kind != DesignElementKind.Image || path is null || !File.Exists(path))
            return (320, 180);
        try
        {
            using var bitmap = new Bitmap(path);
            var shrink = Math.Min(1, Math.Min(960.0 / bitmap.PixelSize.Width, 540.0 / bitmap.PixelSize.Height));
            return (Math.Max(32, Math.Round(bitmap.PixelSize.Width * shrink)), Math.Max(32, Math.Round(bitmap.PixelSize.Height * shrink)));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return (320, 180);
        }
    }

    private async Task PickResourceAsync(ResourceKind kind)
    {
        var path = await _vm.Dialogs.PickFileAsync(kind == ResourceKind.Image ? "Añadir imagen" : "Añadir video",
            kind == ResourceKind.Image ? ResourceLibrary.ImageFilter : ResourceLibrary.VideoFilter);
        if (path is null)
            return;
        AddResourceAsync(path);
    }

    private void AddResourceAsync(string path)
    {
        if (ResourceLibrary.KindOf(path) is not { } kind)
        {
            _error.Text = "Formato no admitido: usa una imagen (png, jpg, gif, webp, bmp) o un video (mp4, mov, webm, mkv, m4v).";
            return;
        }
        AddElement(kind == ResourceKind.Video ? DesignElementKind.Video : DesignElementKind.Image, path);
    }

    private void OnElementDoubleClicked(DesignElement element)
    {
        if (element.Kind is DesignElementKind.Image or DesignElementKind.Video)
            _ = PickAndAssignResourceAsync(element);
    }

    private async Task PickAndAssignResourceAsync(DesignElement element)
    {
        var kind = element.Kind == DesignElementKind.Video ? ResourceKind.Video : ResourceKind.Image;
        var path = await _vm.Dialogs.PickFileAsync(kind == ResourceKind.Image ? "Cambiar imagen" : "Cambiar video",
            kind == ResourceKind.Image ? ResourceLibrary.ImageFilter : ResourceLibrary.VideoFilter);
        if (path is not null)
            Edit(_ => DesignOps.Update(Design, [element.Key], e => e with { Path = path }));
    }

    private async Task SaveTemplateAsync()
    {
        if (await _vm.Dialogs.PromptAsync("Guardar plantilla", "Nombre de la plantilla (reutilizable en otros proyectos):", _committed.Name) is not { } name)
            return;
        try
        {
            await _vm.Templates.SaveAsync(name, _committed, CancellationToken.None);
            _status.Text = $"Plantilla «{name}» guardada.";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            _error.Text = ex.Message;
        }
    }

    // ---- Capas ----------------------------------------------------------------------------------

    private static StreamGeometry? Geometry(string key) =>
        Avalonia.Application.Current?.TryGetResource(key, ThemeVariant.Default, out var value) == true && value is StreamGeometry g ? g : null;

    private Control LayerRow(DesignElement element)
    {
        if (element is null)
            return new Panel { MinHeight = 28 };
        var glyph = new Panel
        {
            Width = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Glyph { Data = Geometry(element.Kind switch { DesignElementKind.Rect => "Icon.Slideshow", DesignElementKind.Text => "Icon.Text", DesignElementKind.Image => "Icon.Image", _ => "Icon.Film" }), Width = 14, Height = 14, Opacity = 0.75 },
            },
        };
        var eye = new ToggleButton
        {
            Width = 24,
            Height = 24,
            MinWidth = 0,
            Padding = new Thickness(0),
            IsChecked = element.Visible,
            Content = new Panel
            {
                Children =
                {
                    new Glyph { Data = Geometry("Icon.Eye"), Width = 13, Height = 13, IsVisible = element.Visible },
                    new Glyph { Data = Geometry("Icon.EyeOff"), Width = 13, Height = 13, Opacity = 0.6, IsVisible = !element.Visible },
                },
            },
            Opacity = element.Visible ? 1 : 0.6,
        };
        AutomationProperties.SetName(eye, "Mostrar u ocultar la pieza");
        eye.Click += (_, _) => Edit(_ => DesignOps.Update(Design, [element.Key], e => e with { Visible = !e.Visible }));
        var lockit = new ToggleButton
        {
            Width = 24,
            Height = 24,
            MinWidth = 0,
            Padding = new Thickness(0),
            IsChecked = element.Locked,
            Content = new Panel
            {
                Children =
                {
                    new Glyph { Data = Geometry("Icon.Lock"), Width = 13, Height = 13, IsVisible = element.Locked },
                    new Glyph { Data = Geometry("Icon.Unlock"), Width = 13, Height = 13, Opacity = 0.45, IsVisible = !element.Locked },
                },
            },
        };
        AutomationProperties.SetName(lockit, "Bloquear o desbloquear la pieza");
        lockit.Click += (_, _) => Edit(_ => DesignOps.Update(Design, [element.Key], e => e with { Locked = !e.Locked }));
        var label = element.Binding switch
        {
            DesignBinding.Clock => "  (reloj)",
            DesignBinding.Countdown => "  (cuenta atrás)",
            _ => "",
        };
        var name = new TextBlock
        {
            Text = element.Name + label,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = element.Visible ? 1 : 0.5,
            FontSize = 12,
        };

        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,Auto,*"),
            ColumnSpacing = 6,
            MinHeight = 28,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(eye, 0);
        Grid.SetColumn(lockit, 1);
        Grid.SetColumn(glyph, 2);
        Grid.SetColumn(name, 3);
        row.Children.Add(eye);
        row.Children.Add(lockit);
        row.Children.Add(glyph);
        row.Children.Add(name);
        return row;
    }

    // ---- Paneles --------------------------------------------------------------------------------

    private void RefreshAll()
    {
        RefreshLayers();
        RefreshProperties();
        RefreshZoomLabel();
    }

    private void RefreshZoomLabel()
    {
        if (_zoom is null)
            return;
        var zoom = _canvas.Zoom * 100;
        _zoom.Text = Math.Abs(zoom - 100) < 0.5 ? "Ajustar" : $"Zoom {zoom:0} %";
    }

    private void RefreshLayers()
    {
        _suppress = true;
        try
        {
            _layers.ItemsSource = Design.Reverse().ToList();
            _layers.SelectedItems!.Clear();
            foreach (var element in Design.Where(e => _canvas.Selection.Contains(e.Key)).Reverse())
                _layers.SelectedItems!.Add(element);
        }
        finally
        {
            _suppress = false;
        }
        _status.Text = Design.Count == 0
            ? "Añade elementos con «Añadir»: texto, forma, imagen o video en bucle."
            : _vm.Session.Graphics.IsOnAir(_id) ? "El gráfico está al aire: la vista previa del lienzo se aplica en directo." : "";
    }

    private void OnLayerSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppress)
            return;
        _suppress = true;
        try
        {
            _canvas.Select(_layers.SelectedItems!.OfType<DesignElement>().Select(el => el.Key).ToList());
        }
        finally
        {
            _suppress = false;
        }
        RefreshProperties();
    }

    private void OnCanvasSelectionChanged()
    {
        if (_suppress)
            return;
        _suppress = true;
        try
        {
            _layers.SelectedItems!.Clear();
            foreach (var element in Design.Where(el => _canvas.Selection.Contains(el.Key)).Reverse())
                _layers.SelectedItems!.Add(element);
        }
        finally
        {
            _suppress = false;
        }
        RefreshProperties();
    }

    private void RefreshProperties()
    {
        _properties.Children.Clear();
        var selected = Design.Where(e => _canvas.Selection.Contains(e.Key)).ToList();
        if (selected.Count == 0)
        {
            var headerCard = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF1C2028")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF282D36")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            Spacing = 8,
                            Children =
                            {
                                new Glyph { Data = Geometry("Icon.Graphics"), Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center },
                                new TextBlock { Text = _committed.Name, FontWeight = FontWeight.Bold, FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
                            },
                        },
                        Muted($"Lienzo base: {_canvas.CanvasSize.Width:0} × {_canvas.CanvasSize.Height:0} (16:9) · Capas: {Design.Count}"),
                    },
                },
            };
            _properties.Children.Add(headerCard);

            var nameBox = new TextBox { Text = _committed.Name, HorizontalAlignment = HorizontalAlignment.Stretch };
            nameBox.LostFocus += (_, _) =>
            {
                var val = nameBox.Text?.Trim();
                if (!string.IsNullOrWhiteSpace(val) && val != _committed.Name)
                    _ = UpdateGraphicAsync(g => g with { Name = val });
            };
            _properties.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("90,*"),
                ColumnSpacing = 8,
                Children =
                {
                    new TextBlock { Text = "Nombre:", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 },
                    StudioDialogs.WithColumn(nameBox, 1),
                },
            });

            var animCombo = new ComboBox
            {
                ItemsSource = AnimationNames().Select(a => a.Value).ToList(),
                SelectedIndex = Math.Max(0, AnimationNames().ToList().FindIndex(a => a.Key == _committed.AnimationIn)),
                HorizontalAlignment = HorizontalAlignment.Stretch,
            };
            animCombo.SelectionChanged += (_, _) =>
            {
                if (animCombo.SelectedIndex >= 0)
                {
                    var anim = AnimationNames()[animCombo.SelectedIndex].Key;
                    _ = UpdateGraphicAsync(g => g with { AnimationIn = anim });
                }
            };
            _properties.Children.Add(new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("90,*"),
                ColumnSpacing = 8,
                Children =
                {
                    new TextBlock { Text = "Animación:", VerticalAlignment = VerticalAlignment.Center, FontSize = 12 },
                    StudioDialogs.WithColumn(animCombo, 1),
                },
            });

            var shortcutsCard = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF161A22")),
                BorderBrush = new SolidColorBrush(Color.Parse("#FF232832")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 10),
                Margin = new Thickness(0, 8, 0, 0),
                Child = new StackPanel
                {
                    Spacing = 6,
                    Children =
                    {
                        new TextBlock { Text = "Atajos del editor", FontWeight = FontWeight.SemiBold, FontSize = 12 },
                        Muted("• Clic: seleccionar elemento\n• Mayús + Clic: selección múltiple\n• Ctrl + D: duplicar selección\n• Supr: eliminar selección\n• Flechas: mover (Mayús: ×10)\n• Ctrl + Rueda: zoom · Rueda: encuadrar\n• Ctrl + 0: encuadrar todo"),
                    },
                },
            };
            _properties.Children.Add(shortcutsCard);
            return;
        }
        if (selected.Count > 1)
        {
            _properties.Children.Add(new TextBlock { Text = $"{selected.Count} elementos seleccionados" });
            _properties.Children.Add(Muted("Con varios: mover, alinear, distribuir, capas, duplicar y borrar. Las propiedades individuales se editan con un solo elemento."));
            return;
        }
        _properties.Children.Add(new GraphicPropertiesPanel(
            _vm,
            selected[0],
            _committed,
            editElement: element => { ApplyElement(element); return element; },
            editGraphic: graphic => { _ = UpdateGraphicAsync(_ => graphic); return graphic; }));
    }

    /// <summary>Aplica un elemento concreto (desde el panel de propiedades) como un paso de deshacer.</summary>
    private void ApplyElement(DesignElement element)
    {
        if (Design.All(e => e.Key != element.Key))
            return; // el elemento ya no existe (deshacer, etc.).
        var design = DesignOps.Update(Design, [element.Key], _ => element);
        _ = ApplyAsync(design, [element.Key]);
    }

    // ---- Vista previa de la animación ------------------------------------------------------------

    private enum PreviewPhase { Enter, Hold, Out }
    private DispatcherTimer? _animTimer;
    private Stopwatch? _animSw;
    private AnimationClip? _inAnimation;
    private AnimationClip? _outAnimation;
    private Rect _previewClip;
    private PreviewPhase _previewPhase;

    /// <summary>Reproduce la animación real sobre el lienzo: mismas curvas/pistas que genera el motor.</summary>
    private void StartPreview()
    {
        StopPreview();
        if (Design.Count == 0)
        {
            _error.Text = "Añade elementos para poder previsualizar la animación.";
            return;
        }
        try
        {
            var canvas = _vm.Project.Canvas;
            _inAnimation = GraphicAnimations.In(_committed, canvas);
            _outAnimation = GraphicAnimations.Out(_committed, canvas);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            _error.Text = "No se puede previsualizar: " + ex.Message;
            return;
        }
        _previewClip = ComputePreviewClip();
        _previewPhase = PreviewPhase.Enter;
        _animSw = Stopwatch.StartNew();
        _animTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Background, AnimTick);
        _animTimer.Start();
    }

    private void AnimTick(object? sender, EventArgs e)
    {
        if (_animSw is not { } sw)
            return;
        var t = sw.Elapsed.TotalSeconds;
        switch (_previewPhase)
        {
            case PreviewPhase.Enter:
            {
                var duration = _inAnimation?.EffectiveDuration ?? 0;
                if (duration < 0.01)
                    BeginPhase(PreviewPhase.Hold);
                else if (t < duration)
                    _canvas.SetPreview(ClipEvaluator.EvaluateClip(_inAnimation!.Tracks, t), _previewClip);
                else
                    BeginPhase(PreviewPhase.Hold);
                break;
            }
            case PreviewPhase.Hold:
            {
                if (t >= 1.5)
                    BeginPhase(PreviewPhase.Out);
                break;
            }
            case PreviewPhase.Out:
            {
                var duration = _outAnimation?.EffectiveDuration ?? 0;
                if (duration < 0.01)
                    StopPreview();
                else if (t < duration)
                    _canvas.SetPreview(ClipEvaluator.EvaluateClip(_outAnimation!.Tracks, t), _previewClip);
                else
                    StopPreview();
                break;
            }
        }
    }

    private void BeginPhase(PreviewPhase phase)
    {
        _previewPhase = phase;
        if (_previewPhase == PreviewPhase.Hold)
            _canvas.SetPreview(null, _previewClip); // reposo: estado ya sin transformación
        _animSw = Stopwatch.StartNew();
    }

    /// <summary>Caja del gráfico en unidades de diseño relativas a su ancla (donde va aplicado el desvelado).</summary>
    private Rect ComputePreviewClip()
    {
        try
        {
            var layout = GraphicTemplates.Layout(_committed, _vm.Project.Canvas);
            var (left, top, right, bottom) = layout.Bounds;
            var gs = Math.Max(0.001, _canvas.GraphicScale);
            return new Rect((left - _committed.X) / gs, (top - _committed.Y) / gs, (right - left) / gs, (bottom - top) / gs);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new Rect(0, 0, 1, 1);
        }
    }

    private void StopPreview()
    {
        _animTimer?.Stop();
        _animSw = null;
        _previewPhase = PreviewPhase.Enter;
        _inAnimation = null;
        _outAnimation = null;
        _canvas.SetPreview(null, default);
    }

    // ---- Texto vivo del lienzo ------------------------------------------------------------------

    /// <summary>Texto que se ve en un elemento del lienzo (campos {x}, reloj y cuenta atrás).</summary>
    private string LiveTextOf(DesignElement element) => element.Binding switch
    {
        DesignBinding.Clock => DateTime.Now.ToString(GraphicTemplates.SafeFormat(element.Text)),
        DesignBinding.Countdown when RemainingCountdown() <= TimeSpan.Zero => _committed.Field("finished"),
        DesignBinding.Countdown => GraphicTemplates.FormatRemaining(RemainingCountdown()),
        _ => GraphicTemplates.ReplaceTokens(element.Text, _committed.Fields),
    };

    private TimeSpan RemainingCountdown()
    {
        try
        {
            return _vm.Session.Graphics.GetCountdown(_id).Remaining;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return TimeSpan.Zero;
        }
    }

    private void LiveTick(object? sender, EventArgs e)
    {
        // El reloj y la cuenta atrás cambian cada segundo: se repinta el lienzo si hay texto vivo.
        if (Design.Any(element => element.Binding != DesignBinding.None))
            _canvas.InvalidateVisual();
    }

    // ---- Teclado --------------------------------------------------------------------------------

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Los campos del propio editor consumen sus teclas (escribir texto, cambiar números).
        if (e.Source is TextBox or ComboBox or NumericUpDown)
            return;
        switch (e.Key)
        {
            case Key.Delete or Key.Back:
                Mix(keys => (Design.Where(el => !keys.Contains(el.Key)).ToList(), Array.Empty<string>()));
                e.Handled = true;
                break;
            case Key.D when e.KeyModifiers.HasFlag(KeyModifiers.Control) && !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                Mix(keys =>
                {
                    var (design, keep) = DesignOps.Duplicate(Design, keys);
                    return (design, keep);
                });
                e.Handled = true;
                break;
            case Key.Z when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _ = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? _vm.Executor.RedoAsync() : _vm.Executor.UndoAsync();
                e.Handled = true;
                break;
            case Key.Y when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                _ = _vm.Executor.RedoAsync();
                e.Handled = true;
                break;
            case Key.Escape:
                _canvas.Select([]);
                e.Handled = true;
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down:
                var step = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 10 : 1;
                var dx = e.Key switch { Key.Left => -step, Key.Right => step, _ => 0 };
                var dy = e.Key switch { Key.Up => -step, Key.Down => step, _ => 0 };
                Mix(keys => (DesignOps.Move(Design, keys, dx, dy), keys));
                e.Handled = true;
                break;
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        StopPreview();
        _previewTimer.Stop();
        _liveTimer.Stop();
        _vm.Executor.StateChanged -= OnStateChanged;
    }
}

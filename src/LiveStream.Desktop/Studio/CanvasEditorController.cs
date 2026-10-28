using Avalonia.Threading;
using LiveStream.Application.Editing;
using LiveStream.Application.Sources;
using LiveStream.Desktop.Components;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.ValueObjects;

namespace LiveStream.Desktop.Studio;

/// <summary>
/// Editor visual sobre la vista previa nativa. libobs dibuja la imagen y la superposición (contornos, tiradores,
/// guías); este controlador interpreta el ratón con <see cref="CanvasGeometry"/>, mueve el motor en vivo durante el
/// gesto y emite un único <see cref="SetItemTransformCommand"/> al soltar, para que deshacer revierta el gesto entero.
/// </summary>
public sealed class CanvasEditorController : IDisposable
{
    private const double HandleRadiusPx = 7;
    private const double SnapDistancePx = 8;

    private readonly MainViewModel _vm;
    private readonly Guid _displayId = Guid.NewGuid();
    private readonly Dictionary<Guid, (int W, int H)> _sizes = [];
    private readonly DispatcherTimer _sizeTimer;
    private PreviewHost? _host;
    private nint _hwnd;
    private int _width = 1, _height = 1;
    private bool _displayCreated;
    private bool _fit = true;
    private ViewTransform _view = new(1, 0, 0);

    private DragGesture? _gesture;
    private Transform? _lastLive;
    private (double X, double Y)? _panStart;
    private ViewTransform _panView;

    public CanvasEditorController(MainViewModel vm)
    {
        _vm = vm;
        _vm.SelectionChanged += (_, _) => PushOverlay([], []);
        _vm.EditSceneChanged += async (_, _) => await ApplySourceAsync();
        _vm.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.EngineReady) && _vm.EngineReady)
                await EnsureDisplayAsync();
            else if (e.PropertyName == nameof(MainViewModel.CanvasText) && _fit && _displayCreated)
            {
                // Cambio de resolución de salida: en modo "ajustar" la vista sigue al nuevo lienzo.
                RecomputeFit();
                await ApplyViewAsync();
            }
            else if (e.PropertyName is nameof(MainViewModel.IsVerticalFormatActive) or nameof(MainViewModel.SelectedStreamMode))
            {
                PushOverlay([], []);
            }
        };
        // Los tamaños de las fuentes cambian solos (una cámara que arranca, una ventana redimensionada).
        _sizeTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, async (_, _) => await RefreshSizesAsync());
        _sizeTimer.Start();
    }

    public event Action<Guid>? ItemDoubleClicked;

    public string ZoomLabel => _fit ? "Ajustar" : $"{_view.Scale * 100:0}%";

    public void Attach(PreviewHost host)
    {
        _host = host;
        host.NativeHandleCreated += async (hwnd, w, h) =>
        {
            _hwnd = hwnd;
            (_width, _height) = (w, h);
            RecomputeFit();
            await EnsureDisplayAsync();
        };
        host.NativeHandleResized += async (w, h) =>
        {
            (_width, _height) = (w, h);
            RecomputeFit();
            if (_displayCreated && await Safe(() => _vm.Engine.ResizeDisplayAsync(_displayId, w, h)))
                await ApplyViewAsync();
        };
        host.NativeHandleDestroying += () =>
        {
            if (!_displayCreated)
                return;
            _displayCreated = false;
            try
            {
                // Síncrono a propósito: libobs debe soltar el HWND antes de que se destruya.
                _vm.Engine.DestroyDisplayAsync(_displayId).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
                // El motor ya se apagó; sp_engine_destroy libera los displays.
            }
        };
        host.CanvasPointerPressed += OnPressed;
        host.CanvasPointerMoved += OnMoved;
        host.CanvasPointerReleased += OnReleased;
        host.CanvasWheel += p => Zoom(p.X, p.Y, p.WheelDelta > 0 ? 1.15 : 1 / 1.15);
        host.CanvasDoubleClicked += p =>
        {
            if (CanvasGeometry.HitTest(Items(), ToCanvas(p)) is { } item)
                ItemDoubleClicked?.Invoke(item.Id);
        };
        host.FilesDropped += async (paths, x, y) =>
        {
            var canvas = ToCanvas(new CanvasPointer(x, y, CanvasButton.Left, CanvasModifiers.None));
            await new CanvasDropController(_vm).ImportFilesAsync(paths, canvas.X, canvas.Y);
        };
    }

    // ---- Vista ----------------------------------------------------------------------------------

    public void FitView()
    {
        _fit = true;
        RecomputeFit();
        _ = ApplyViewAsync();
    }

    public void Zoom(double factor) => Zoom(_width / 2.0, _height / 2.0, factor);

    private void Zoom(double x, double y, double factor)
    {
        _fit = false;
        _view = _view.ZoomAt(x, y, _view.Scale * factor);
        _ = ApplyViewAsync();
    }

    private void RecomputeFit()
    {
        if (_fit)
            _view = ViewTransform.Fit(_vm.Project.Canvas.Width, _vm.Project.Canvas.Height, _width, _height);
    }

    private async Task ApplyViewAsync()
    {
        _vm.ZoomText = ZoomLabel;
        if (_displayCreated)
            await Safe(() => _vm.Engine.SetDisplayViewAsync(_displayId, _fit ? 0 : _view.Scale, _view.OffsetX, _view.OffsetY));
    }

    private async Task EnsureDisplayAsync()
    {
        if (_displayCreated || _hwnd == 0 || !_vm.EngineReady)
            return;
        if (await Safe(() => _vm.Engine.CreateDisplayAsync(_displayId, _hwnd, _width, _height)))
        {
            _displayCreated = true;
            await ApplySourceAsync();
            await ApplyViewAsync();
            PushOverlay([], []);
        }
    }

    private async Task ApplySourceAsync()
    {
        if (_displayCreated && _vm.EditSceneId is { } scene)
            await Safe(() => _vm.Engine.SetDisplaySourceAsync(_displayId, scene));
        await RefreshSizesAsync();
    }

    // ---- Elementos ------------------------------------------------------------------------------

    private IReadOnlyList<EditorItem> Items()
    {
        if (_vm.EditSceneId is not { } id)
            return [];
        return _vm.Project.GetScene(id).Items.Select(i =>
        {
            var (w, h) = _sizes.TryGetValue(i.SourceId, out var s) ? s : (0, 0);
            return new EditorItem(i.Id, i.Transform, w, h, i.Visible, i.Locked);
        }).ToList();
    }

    private async Task RefreshSizesAsync()
    {
        if (!_vm.EngineReady || _vm.EditSceneId is not { } id)
            return;
        var changed = false;
        foreach (var sourceId in _vm.Project.GetScene(id).Items.Select(i => i.SourceId).Distinct().ToList())
        {
            try
            {
                var size = await _vm.Engine.GetSourceSizeAsync(sourceId, CancellationToken.None);
                if (!_sizes.TryGetValue(sourceId, out var old) || old != size)
                {
                    _sizes[sourceId] = size;
                    changed = true;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or Native.Engine.NativeEngineException)
            {
                // La fuente pudo eliminarse entre la lista y la consulta.
            }
        }
        if (changed)
            PushOverlay([], []);
    }

    // ---- Ratón ----------------------------------------------------------------------------------

    private Point2 ToCanvas(CanvasPointer p) => _view.ToCanvas(p.X, p.Y);

    private void OnPressed(CanvasPointer p)
    {
        if (p.Button == CanvasButton.Middle)
        {
            _fit = false;
            _panStart = (p.X, p.Y);
            _panView = _view;
            return;
        }
        if (p.Button != CanvasButton.Left)
            return;

        var point = ToCanvas(p);
        var items = Items();
        var radius = HandleRadiusPx / _view.Scale;
        var additive = p.Modifiers.HasFlag(CanvasModifiers.Control);

        // Primero los tiradores de lo ya seleccionado (tienen prioridad sobre lo que haya debajo).
        foreach (var selected in items.Where(i => _vm.SelectedItems.Contains(i.Id) && !i.Locked).Reverse())
        {
            var handle = CanvasGeometry.HitHandle(selected, point, radius);
            if (handle is not Handle.None and not Handle.Body)
            {
                StartGesture(selected, handle, point, items);
                return;
            }
        }

        var hit = CanvasGeometry.HitTest(items, point);
        if (hit is null)
        {
            if (!additive)
                _vm.SelectFromCanvas(null, false);
            return;
        }
        if (additive)
        {
            _vm.SelectFromCanvas(hit.Id, true);
            return;
        }
        if (!_vm.SelectedItems.Contains(hit.Id))
            _vm.SelectFromCanvas(hit.Id, false);
        if (!hit.Locked)
            StartGesture(hit, Handle.Body, point, items);
    }

    private void StartGesture(EditorItem item, Handle handle, Point2 point, IReadOnlyList<EditorItem> items)
    {
        IReadOnlyList<double>? extraTargetsX = null;
        if (_vm.IsVerticalFormatActive || _vm.SelectedStreamMode != StreamOutputMode.Horizontal)
        {
            var w = _vm.Project.Canvas.Width;
            var h = _vm.Project.Canvas.Height;
            if (w >= h)
            {
                var (left, center, right) = CanvasGeometry.ComputeVerticalGuideX(w, h);
                extraTargetsX = [left, center, right];
            }
        }
        _gesture = new DragGesture(item, handle, point, _vm.Project.Canvas.Width, _vm.Project.Canvas.Height, items, extraTargetsX);
        _lastLive = null;
    }

    private void OnMoved(CanvasPointer p)
    {
        if (_panStart is { } start)
        {
            _view = _panView with { OffsetX = _panView.OffsetX + p.X - start.X, OffsetY = _panView.OffsetY + p.Y - start.Y };
            _ = ApplyViewAsync();
            return;
        }

        var point = ToCanvas(p);
        if (_gesture is null)
        {
            UpdateCursor(point);
            return;
        }

        var snap = _vm.SnapEnabled && !p.Modifiers.HasFlag(CanvasModifiers.Control) ? SnapDistancePx / _view.Scale : 0;
        var update = _gesture.Update(point, snap, crop: p.Modifiers.HasFlag(CanvasModifiers.Alt), freeAspect: p.Modifiers.HasFlag(CanvasModifiers.Shift));
        if (update.Transform == _lastLive)
            return;
        _lastLive = update.Transform;
        // En vivo directamente al motor; el dominio solo cambia al confirmar.
        var itemId = _gesture.ItemId;
        _ = Safe(() => _vm.Engine.SetItemTransformAsync(itemId, update.Transform, CancellationToken.None));
        PushOverlay(update.GuidesX, update.GuidesY);
    }

    private async void OnReleased(CanvasPointer p)
    {
        if (p.Button == CanvasButton.Middle)
        {
            _panStart = null;
            return;
        }
        if (_gesture is not { } gesture)
            return;
        _gesture = null;
        PushOverlay([], []);
        if (_lastLive is { } final && final != gesture.StartTransform)
        {
            var result = await _vm.Run(new SetItemTransformCommand(gesture.ItemId, final, previous: gesture.StartTransform));
            if (!result.Success)
                await Safe(() => _vm.Engine.SetItemTransformAsync(gesture.ItemId, gesture.StartTransform, CancellationToken.None));
        }
    }

    private void UpdateCursor(Point2 point)
    {
        if (_host is null)
            return;
        var items = Items();
        var radius = HandleRadiusPx / _view.Scale;
        foreach (var selected in items.Where(i => _vm.SelectedItems.Contains(i.Id) && !i.Locked).Reverse())
        {
            var handle = CanvasGeometry.HitHandle(selected, point, radius);
            if (handle is Handle.None)
                continue;
            _host.NativeCursor = handle switch
            {
                Handle.TopLeft or Handle.BottomRight => CanvasCursor.SizeNwse,
                Handle.TopRight or Handle.BottomLeft => CanvasCursor.SizeNesw,
                Handle.Left or Handle.Right => CanvasCursor.SizeWe,
                Handle.Top or Handle.Bottom => CanvasCursor.SizeNs,
                _ => CanvasCursor.Move,
            };
            return;
        }
        _host.NativeCursor = CanvasGeometry.HitTest(items, point) is { Locked: false } ? CanvasCursor.Move : CanvasCursor.Arrow;
    }

    private void PushOverlay(IReadOnlyList<double> guidesX, IReadOnlyList<double> guidesY)
    {
        if (!_displayCreated)
            return;
        var selected = _vm.SelectedItems.ToList();
        var gx = new List<double>(guidesX);
        var gy = new List<double>(guidesY);

        if (_vm.IsVerticalFormatActive || _vm.SelectedStreamMode != StreamOutputMode.Horizontal)
        {
            var w = _vm.Project.Canvas.Width;
            var h = _vm.Project.Canvas.Height;
            if (w >= h)
            {
                var (left, center, right) = CanvasGeometry.ComputeVerticalGuideX(w, h);
                if (!gx.Contains(left)) gx.Add(left);
                if (!gx.Contains(center)) gx.Add(center);
                if (!gx.Contains(right)) gx.Add(right);
            }
        }

        _ = Safe(() => _vm.Engine.SetDisplayOverlayAsync(_displayId, selected, gx, gy));
    }

    private async Task<bool> Safe(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Native.Engine.NativeEngineException or ObjectDisposedException)
        {
            Infrastructure.Diagnostics.AppLog.Warn("lienzo", ex.ToString());
            _vm.StatusMessage = ex.Message;
            return false;
        }
    }

    public void Dispose() => _sizeTimer.Stop();
}

/// <summary>Display de solo lectura que muestra la salida de programa (modo estudio).</summary>
public sealed class ProgramDisplayController(MainViewModel vm)
{
    private readonly Guid _displayId = Guid.NewGuid();
    private nint _hwnd;
    private int _width = 1, _height = 1;
    private bool _created;

    public void Attach(PreviewHost host)
    {
        host.NativeHandleCreated += async (hwnd, w, h) =>
        {
            (_hwnd, _width, _height) = (hwnd, w, h);
            await Logged("Crear la salida de programa", EnsureAsync);
        };
        host.NativeHandleResized += async (w, h) =>
        {
            (_width, _height) = (w, h);
            if (_created)
                await Logged("Redimensionar la salida de programa", () => vm.Engine.ResizeDisplayAsync(_displayId, w, h));
        };
        host.NativeHandleDestroying += () =>
        {
            if (!_created)
                return;
            _created = false;
            try
            {
                vm.Engine.DestroyDisplayAsync(_displayId).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException)
            {
            }
        };
        vm.PropertyChanged += async (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.EngineReady))
                await Logged("Crear la salida de programa", EnsureAsync);
        };
    }

    private async Task EnsureAsync()
    {
        if (_created || _hwnd == 0 || !vm.EngineReady)
            return;
        await vm.Engine.CreateDisplayAsync(_displayId, _hwnd, _width, _height);
        await vm.Engine.SetDisplaySourceAsync(_displayId, null); // salida de programa
        _created = true;
    }

    /// <summary>Estos manejadores son async void: un fallo del motor se registra y se muestra en vez de cerrar la app.</summary>
    private async Task Logged(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or Native.Engine.NativeEngineException or ObjectDisposedException)
        {
            Infrastructure.Diagnostics.AppLog.Error("lienzo", what, ex);
            vm.StatusMessage = $"{what}: {ex.Message}";
        }
    }
}

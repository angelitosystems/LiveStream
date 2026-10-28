using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.Entities;
using LiveStream.Infrastructure.Diagnostics;
using LiveStream.Native.Engine;

namespace LiveStream.Desktop.Components;

/// <summary>
/// Contenedor de vista previa acelerada por hardware D3D11 para una fuente específica.
/// Crea un display nativo dedicado y una escena temporal de previsualización en libobs.
/// Incluye modo de comparación antes/después (conmutación temporal de filtros en tiempo real)
/// e indicadores de resolución, FPS y estado del motor.
/// </summary>
public sealed class LiveSourcePreview : UserControl, IAsyncDisposable
{
    private readonly Guid _displayId = Guid.NewGuid();
    private readonly Guid _previewSceneId = Guid.NewGuid();
    private readonly Guid _previewItemId = Guid.NewGuid();

    private readonly PreviewHost _previewHost;
    private readonly Border _statusBadge;
    private readonly TextBlock _statusText;
    private readonly TextBlock _resolutionText;
    private readonly Button? _compareButton;
    private readonly Grid _overlayHeader;
    private readonly Border _loadingPlaceholder;
    private readonly TextBlock _loadingText;

    private MainViewModel? _vm;
    private Guid _sourceId;
    private Source? _source;
    private bool _displayCreated;
    private bool _sceneCreated;
    private bool _isComparingOriginal;
    private bool _disposed;
    private nint _hwnd;
    private int _width = 640;
    private int _height = 360;

    public event Action<bool>? CompareModeChanged;

    public bool IsComparingOriginal => _isComparingOriginal;

    public LiveSourcePreview()
    {
        _previewHost = new PreviewHost
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        _previewHost.NativeHandleCreated += OnNativeHandleCreated;
        _previewHost.NativeHandleResized += OnNativeHandleResized;
        _previewHost.NativeHandleDestroying += OnNativeHandleDestroying;

        _statusText = new TextBlock
        {
            Text = "● En vivo",
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#FF34D399")),
            VerticalAlignment = VerticalAlignment.Center
        };

        _statusBadge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF162E22")),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = _statusText
        };

        _resolutionText = new TextBlock
        {
            Text = "1920×1080",
            FontSize = 10,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            Classes = { "muted" },
            VerticalAlignment = VerticalAlignment.Center
        };

        _compareButton = new Button
        {
            Content = "👁️ Ver original",
            FontSize = 10.5,
            Padding = new Thickness(8, 3),
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse("#FF232832")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF343C4B")),
            BorderThickness = new Thickness(1),
            Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
            VerticalAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(_compareButton, "Pulsa para alternar entre el resultado procesado con filtros y la imagen original sin procesar.");
        _compareButton.Click += async (_, _) => await ToggleCompareAsync();

        _overlayHeader = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"),
            ColumnSpacing = 8,
            Margin = new Thickness(10, 8),
            VerticalAlignment = VerticalAlignment.Top,
            Children =
            {
                _statusBadge,
                WithCol(_resolutionText, 1),
                WithCol(_compareButton, 3)
            }
        };

        _loadingText = new TextBlock
        {
            Text = "Iniciando señal de vídeo...",
            Classes = { "muted" },
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        _loadingPlaceholder = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF0F1115")),
            Child = new StackPanel
            {
                Spacing = 8,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children =
                {
                    new TextBlock
                    {
                        Text = "📹",
                        FontSize = 26,
                        HorizontalAlignment = HorizontalAlignment.Center
                    },
                    _loadingText
                }
            }
        };

        var rootGrid = new Grid
        {
            Children =
            {
                _loadingPlaceholder,
                _previewHost,
                _overlayHeader
            }
        };

        Content = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF0E1014")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF262B34")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = rootGrid
        };
    }

    private static Control WithCol(Control c, int col)
    {
        Grid.SetColumn(c, col);
        return c;
    }

    public void Attach(MainViewModel vm, Guid sourceId, bool allowCompare = true)
    {
        _vm = vm;
        _sourceId = sourceId;
        _source = vm.Project.FindSource(sourceId);

        if (_compareButton is not null)
            _compareButton.IsVisible = allowCompare;

        if (_source is { } s)
        {
            _loadingText.Text = $"Conectando con «{s.Name}»...";
        }

        if (_hwnd != 0 && !_displayCreated)
        {
            _ = Dispatcher.UIThread.InvokeAsync(EnsureDisplayAsync);
        }
    }

    private async void OnNativeHandleCreated(nint hwnd, int w, int h)
    {
        _hwnd = hwnd;
        _width = Math.Max(1, w);
        _height = Math.Max(1, h);
        await EnsureDisplayAsync();
    }

    private async void OnNativeHandleResized(int w, int h)
    {
        _width = Math.Max(1, w);
        _height = Math.Max(1, h);
        if (_displayCreated && _vm is { EngineReady: true })
        {
            await Logged("Redimensionar la vista previa", () => _vm.Engine.ResizeDisplayAsync(_displayId, _width, _height));
        }
    }

    private void OnNativeHandleDestroying()
    {
        _hwnd = 0;
        if (_displayCreated && _vm is { EngineReady: true })
        {
            _displayCreated = false;
            try
            {
                // Síncrono a propósito: libobs debe soltar el HWND antes de que se destruya (igual que el lienzo).
                _vm.Engine.DestroyDisplayAsync(_displayId).GetAwaiter().GetResult();
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NativeEngineException)
            {
                AppLog.Warn("vista-previa", $"Liberar el display de la vista previa: {ex.Message}");
            }
        }
        _ = DisposeAsync();
    }

    private async Task EnsureDisplayAsync()
    {
        if (_disposed || _displayCreated || _hwnd == 0 || _vm is null || !_vm.EngineReady)
            return;

        try
        {
            await _vm.Engine.CreateDisplayAsync(_displayId, _hwnd, _width, _height);
            _displayCreated = true;

            // Crear escena temporal de previsualización para aislar la fuente
            await _vm.Engine.CreateSceneAsync(_previewSceneId, "__temp_source_preview__", CancellationToken.None);
            _sceneCreated = true;

            await _vm.Engine.AddItemAsync(_previewSceneId, _previewItemId, _sourceId, CancellationToken.None);
            await _vm.Engine.SetDisplaySourceAsync(_displayId, _previewSceneId, CancellationToken.None);
            await _vm.Engine.SetDisplayViewAsync(_displayId, 0, 0, 0); // Ajustar a ventana

            try
            {
                var (sw, sh) = await _vm.Engine.GetSourceSizeAsync(_sourceId, CancellationToken.None);
                if (sw > 0 && sh > 0)
                    _resolutionText.Text = $"{sw}×{sh}";
            }
            catch (Exception ex) when (ex is InvalidOperationException or NativeEngineException)
            {
                AppLog.Warn("vista-previa", $"Tamaño de la fuente {_sourceId}: {ex.Message}");
                _resolutionText.Text = $"{_vm.Project.Canvas.Width}×{_vm.Project.Canvas.Height}";
            }

            _loadingPlaceholder.IsVisible = false;
        }
        catch (Exception ex)
        {
            AppLog.Error("vista-previa", $"Vista previa de la fuente {_sourceId}", ex);
            _loadingPlaceholder.IsVisible = true;
            _loadingText.Text = $"Vista previa no disponible: {ex.Message}";
        }
    }

    public async Task ToggleCompareAsync()
    {
        if (_vm is null || !_vm.EngineReady || _source is null)
            return;

        _isComparingOriginal = !_isComparingOriginal;
        if (_compareButton is not null)
        {
            _compareButton.Content = _isComparingOriginal ? "🔄 Restaurar filtros" : "👁️ Ver original";
            _compareButton.Background = new SolidColorBrush(Color.Parse(_isComparingOriginal ? "#FF3B2D14" : "#FF232832"));
            _compareButton.BorderBrush = new SolidColorBrush(Color.Parse(_isComparingOriginal ? "#FFF5A524" : "#FF343C4B"));
            _statusText.Text = _isComparingOriginal ? "● Sin procesar" : "● En vivo";
            _statusText.Foreground = new SolidColorBrush(Color.Parse(_isComparingOriginal ? "#FFF5A524" : "#FF34D399"));
            _statusBadge.Background = new SolidColorBrush(Color.Parse(_isComparingOriginal ? "#FF2E2310" : "#FF162E22"));
        }

        foreach (var filter in _source.Filters)
        {
            // Si comparamos con el original: desactivar en motor. Si restauramos: devolver su estado real
            await Logged($"Filtro «{filter.Name}»", () => _vm.Engine.SetFilterEnabledAsync(filter.Id, _isComparingOriginal ? false : filter.Enabled, CancellationToken.None));
        }

        CompareModeChanged?.Invoke(_isComparingOriginal);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_isComparingOriginal && _source is not null && _vm is { EngineReady: true })
        {
            foreach (var filter in _source.Filters)
            {
                await Logged($"Restaurar filtro «{filter.Name}»", () => _vm.Engine.SetFilterEnabledAsync(filter.Id, filter.Enabled, CancellationToken.None));
            }
        }

        if (_vm is { EngineReady: true })
        {
            if (_displayCreated)
            {
                await Logged("Liberar el display de la vista previa", () => _vm.Engine.DestroyDisplayAsync(_displayId, CancellationToken.None));
                _displayCreated = false;
            }

            if (_sceneCreated)
            {
                await Logged("Quitar la escena de la vista previa", () => _vm.Engine.RemoveSceneAsync(_previewSceneId, CancellationToken.None));
                _sceneCreated = false;
            }
        }
    }

    /// <summary>Fallos del motor en la vista previa: se registran y no interrumpen el diálogo.</summary>
    private static async Task Logged(string what, Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or NativeEngineException)
        {
            AppLog.Warn("vista-previa", $"{what}: {ex.Message}");
        }
    }
}

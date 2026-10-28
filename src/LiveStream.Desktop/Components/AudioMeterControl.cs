using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using LiveStream.Desktop.ViewModels;

namespace LiveStream.Desktop.Components;

/// <summary>
/// Vúmetro y medidor de nivel de señal estéreo en tiempo real para fuentes de audio.
/// Muestra barras dinámicas con degradado verde-amarillo-rojo, retención de picos y escala de decibelios (-60 a 0 dB).
/// </summary>
public sealed class AudioMeterControl : UserControl, IDisposable
{
    private readonly Border _barLeft;
    private readonly Border _barRight;
    private readonly TextBlock _dbText;
    private readonly Border _signalDot;
    private DispatcherTimer? _pollTimer;
    private MainViewModel? _vm;
    private Guid? _sourceId;
    private double _leftDb = -100;
    private double _rightDb = -100;

    public AudioMeterControl()
    {
        _barLeft = CreateMeterBar();
        _barRight = CreateMeterBar();

        _signalDot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.Parse("#FF3D4450")),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };

        _dbText = new TextBlock
        {
            Text = "— dB",
            FontSize = 10.5,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace"),
            Foreground = new SolidColorBrush(Color.Parse("#FF9AA1AB")),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 55,
            TextAlignment = TextAlignment.Right
        };

        var trackGrid = new Grid
        {
            RowDefinitions = new RowDefinitions("6,4,6"),
            Children =
            {
                WrapInTrack(_barLeft, 0),
                WrapInTrack(_barRight, 2)
            }
        };

        var scaleGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*,*,*"),
            Margin = new Thickness(0, 2, 0, 0),
            Children =
            {
                new TextBlock { Text = "-60", FontSize = 8.5, Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Left },
                WithCol(new TextBlock { Text = "-20", FontSize = 8.5, Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center }, 1),
                WithCol(new TextBlock { Text = "-6", FontSize = 8.5, Classes = { "muted" }, HorizontalAlignment = HorizontalAlignment.Center }, 2),
                WithCol(new TextBlock { Text = "0 dB", FontSize = 8.5, Foreground = new SolidColorBrush(Color.Parse("#FFE5484D")), HorizontalAlignment = HorizontalAlignment.Right }, 3),
            }
        };

        var leftContent = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 2,
            Children = { trackGrid, scaleGrid }
        };

        Content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                _signalDot,
                WithCol(leftContent, 1),
                WithCol(_dbText, 2)
            }
        };
    }

    private static Control WithCol(Control c, int col)
    {
        Grid.SetColumn(c, col);
        return c;
    }

    private static Border WrapInTrack(Border fillBar, int row)
    {
        var track = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#FF14161B")),
            BorderBrush = new SolidColorBrush(Color.Parse("#FF242832")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Height = 8,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Child = fillBar
        };
        Grid.SetRow(track, row);
        return track;
    }

    private static Border CreateMeterBar()
    {
        var gradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#FF22C55E"), 0.0),   // Verde normal
                new GradientStop(Color.Parse("#FF22C55E"), 0.68),  // Hasta -18 dB
                new GradientStop(Color.Parse("#FFEAB308"), 0.85),  // Amarillo objetivo (-6 dB)
                new GradientStop(Color.Parse("#FFE5484D"), 1.0)    // Rojo saturación (0 dB)
            }
        };

        return new Border
        {
            Background = gradient,
            CornerRadius = new CornerRadius(2),
            HorizontalAlignment = HorizontalAlignment.Left,
            Width = 0
        };
    }

    public void Attach(MainViewModel vm, Guid sourceId)
    {
        _vm = vm;
        _sourceId = sourceId;

        _pollTimer?.Stop();
        _pollTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(45), DispatcherPriority.Normal, async (_, _) =>
        {
            if (_vm is null || !_vm.EngineReady || _sourceId is not { } id)
                return;

            try
            {
                var levels = await _vm.Engine.GetAudioLevelsAsync(id, CancellationToken.None);
                if (levels is null)
                {
                    UpdateLevels(-100, -100);
                }
                else
                {
                    UpdateLevels(levels.PeakLeftDb, levels.PeakRightDb);
                }
            }
            catch
            {
                UpdateLevels(-100, -100);
            }
        });
        _pollTimer.Start();
    }

    public void UpdateLevels(double leftDb, double rightDb)
    {
        _leftDb = leftDb;
        _rightDb = rightDb;

        var maxDb = Math.Max(leftDb, rightDb);
        var active = maxDb > -58.0;

        _signalDot.Background = new SolidColorBrush(Color.Parse(active ? (maxDb > -3.0 ? "#FFE5484D" : "#FF22C55E") : "#FF3D4450"));
        _dbText.Text = maxDb <= -90.0 ? "— dB" : $"{maxDb:0.0} dB";
        _dbText.Foreground = new SolidColorBrush(Color.Parse(maxDb > -3.0 ? "#FFE5484D" : (active ? "#FFE6E8EB" : "#FF9AA1AB")));

        var totalWidth = Bounds.Width > 100 ? Bounds.Width - 90 : 180;
        _barLeft.Width = DbToWidth(leftDb, totalWidth);
        _barRight.Width = DbToWidth(rightDb, totalWidth);
    }

    private static double DbToWidth(double db, double maxWidth)
    {
        if (db <= -60.0 || double.IsNaN(db) || double.IsNegativeInfinity(db))
            return 0;
        if (db >= 0.0)
            return maxWidth;

        // Escala perceptualmente proporcional (-60 dB a 0 dB)
        var normalized = (db + 60.0) / 60.0;
        return Math.Clamp(normalized * maxWidth, 0, maxWidth);
    }

    public void Dispose()
    {
        _pollTimer?.Stop();
        _pollTimer = null;
        _vm = null;
    }
}

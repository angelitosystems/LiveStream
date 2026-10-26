using CommunityToolkit.Mvvm.ComponentModel;
using LiveStream.Application.Audio;
using LiveStream.Domain.Entities;

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace LiveStream.Desktop.ViewModels;

public sealed partial class SceneRowViewModel(Guid id, string name) : ObservableObject
{
    public Guid Id { get; } = id;
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private bool _isProgram;
    [ObservableProperty] private bool _isPreview;

    /// <summary>Nombre que anuncian los lectores de pantalla para la fila.</summary>
    public override string ToString() => IsProgram ? $"{Name} (programa)" : Name;
}

/// <summary>Elemento de la escena en edición, tal como lo muestra el panel de fuentes (arriba = capa superior).</summary>
public sealed partial class SourceRowViewModel(Guid itemId, Guid sourceId, SourceKind kind) : ObservableObject
{
    public Guid ItemId { get; } = itemId;
    public Guid SourceId { get; } = sourceId;
    public SourceKind Kind { get; } = kind;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _visible = true;
    [ObservableProperty] private bool _locked;

    public string KindLabel => SourceCatalog.Label(Kind);

    public override string ToString() => $"{Name}, {KindLabel}{(Visible ? "" : ", oculta")}";
}

/// <summary>Canal del mezclador. Los niveles se actualizan ~30 veces por segundo desde el motor.</summary>
public sealed partial class MixerChannelViewModel(Guid sourceId) : ObservableObject
{
    public const double MinDb = -60;

    public Guid SourceId { get; } = sourceId;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _muted;
    [ObservableProperty] private double _volumeDb;
    [ObservableProperty] private AudioMonitoring _monitoring;
    [ObservableProperty] private bool _isGlobal;
    [ObservableProperty] private double _peakLeft = -100;
    [ObservableProperty] private double _peakRight = -100;
    [ObservableProperty] private double _magnitudeLeft = -100;
    [ObservableProperty] private double _magnitudeRight = -100;

    /// <summary>Evita reenviar al motor un valor que acaba de llegar del dominio.</summary>
    internal bool Syncing { get; set; }

    internal Action<MixerChannelViewModel, double>? VolumeRequested { get; set; }
    internal Action<MixerChannelViewModel, AudioMonitoring>? MonitoringRequested { get; set; }

    public string VolumeText => VolumeDb <= MinDb ? "-inf dB" : $"{VolumeDb:0.0} dB";

    public bool IsMonitored => Monitoring != AudioMonitoring.None;

    partial void OnVolumeDbChanged(double value)
    {
        OnPropertyChanged(nameof(VolumeText));
        if (!Syncing)
            VolumeRequested?.Invoke(this, value);
    }

    partial void OnMonitoringChanged(AudioMonitoring value) => OnPropertyChanged(nameof(IsMonitored));

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    private void SetMonitoring(AudioMonitoring monitoring) => MonitoringRequested?.Invoke(this, monitoring);

    public static double ToDb(double multiplier) => Math.Max(MinDb, SetVolumeCommand.MultiplierToDb(multiplier));

    public static double ToMultiplier(double db) => db <= MinDb ? 0 : SetVolumeCommand.DbToMultiplier(db);
}

/// <summary>Catálogo de tipos de fuente que se pueden añadir desde la UI, con su descripción y categoría.</summary>
public static class SourceCatalog
{
    public sealed record Entry(SourceKind Kind, string Category, string Label, string Description, string DefaultName);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        // Captura
        new(SourceKind.DisplayCapture, "Captura", "Captura de pantalla", "Un monitor completo.", "Pantalla"),
        new(SourceKind.WindowCapture, "Captura", "Captura de ventana", "Una ventana concreta de una aplicación.", "Ventana"),
        new(SourceKind.GameCapture, "Captura", "Captura de juego", "Juegos DirectX/OpenGL/Vulkan a pantalla completa con el mínimo impacto.", "Juego"),
        new(SourceKind.VideoCaptureDevice, "Captura", "Dispositivo de video", "Webcam o capturadora HDMI.", "Cámara"),
        // Audio
        new(SourceKind.AudioInput, "Audio", "Entrada de audio", "Micrófono o interfaz de audio.", "Micrófono"),
        new(SourceKind.AudioOutput, "Audio", "Audio de salida", "Lo que suena en el equipo (escritorio, juego, llamadas).", "Audio del escritorio"),
        // Contenido visual
        new(SourceKind.Image, "Contenido visual", "Imagen", "PNG, JPG, GIF animado... Útil para logos y marcas de agua.", "Imagen"),
        new(SourceKind.ImageSlideshow, "Contenido visual", "Presentación de imágenes", "Varias imágenes en secuencia.", "Presentación"),
        new(SourceKind.Media, "Contenido visual", "Video o audio", "Archivo multimedia local o URL de red.", "Multimedia"),
        new(SourceKind.Text, "Contenido visual", "Texto", "Texto con fuente, color, borde y fondo.", "Texto"),
        new(SourceKind.ColorSource, "Contenido visual", "Color sólido", "Rectángulo de color: fondos, bandas y formas simples.", "Color"),
    ];

    public static string Label(SourceKind kind) => Entries.FirstOrDefault(e => e.Kind == kind)?.Label ?? kind.ToString();

    public static string Category(SourceKind kind) => Entries.FirstOrDefault(e => e.Kind == kind)?.Category ?? "Otras";

    /// <summary>Clave de recurso del icono 24x24 de la familia propia para cada tipo de fuente.</summary>
    public static string IconKey(SourceKind kind) => kind switch
    {
        SourceKind.DisplayCapture => "Icon.Display",
        SourceKind.WindowCapture => "Icon.Window",
        SourceKind.GameCapture => "Icon.Gamepad",
        SourceKind.VideoCaptureDevice => "Icon.Camera",
        SourceKind.AudioInput => "Icon.Mic",
        SourceKind.AudioOutput => "Icon.Speaker",
        SourceKind.Image => "Icon.Image",
        SourceKind.ImageSlideshow => "Icon.Slideshow",
        SourceKind.Media => "Icon.Film",
        SourceKind.Text => "Icon.Text",
        SourceKind.ColorSource => "Icon.Palette",
        _ => "Icon.Layers",
    };

    /// <summary>Geometría del icono (null si el recurso no está cargado: se oculta el icono, no explota).</summary>
    public static StreamGeometry? Geometry(SourceKind kind) =>
        Avalonia.Application.Current?.TryGetResource(IconKey(kind), ThemeVariant.Default, out var value) == true && value is StreamGeometry g
            ? g : null;
}

/// <summary>Gráfico de la capa superpuesta tal como lo muestra el panel de gráficos (arriba = capa superior).</summary>
public sealed partial class GraphicRowViewModel(Guid id, Domain.Graphics.GraphicKind kind) : ObservableObject
{
    public Guid Id { get; } = id;
    public Domain.Graphics.GraphicKind Kind { get; } = kind;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private bool _onAir;
    [ObservableProperty] private string _detail = string.Empty;
    [ObservableProperty] private bool _countdownRunning;

    public string KindLabel => Application.Graphics.GraphicTemplates.DisplayName(Kind);
    /// <summary>Tiene cuenta atrás (la plantilla o un diseño libre con un texto de cuenta atrás).</summary>
    [ObservableProperty] private bool _isCountdown = kind == Domain.Graphics.GraphicKind.Countdown;
    public bool IsScoreboard => Kind == Domain.Graphics.GraphicKind.Scoreboard;

    public override string ToString() => $"{Name}{(OnAir ? ", al aire" : "")}";
}

/// <summary>Opción del menú de gráficos: una plantilla o la entrada de importación (Kind null → importar .lsgfx).</summary>
public sealed record GraphicKindOption(Domain.Graphics.GraphicKind? Kind, string Label);

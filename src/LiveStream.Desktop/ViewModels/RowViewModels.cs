using CommunityToolkit.Mvvm.ComponentModel;
using LiveStream.Application.Audio;
using LiveStream.Domain.Entities;

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

/// <summary>Catálogo de tipos de fuente que se pueden añadir desde la UI, con su descripción.</summary>
public static class SourceCatalog
{
    public sealed record Entry(SourceKind Kind, string Label, string Description, string DefaultName);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new(SourceKind.DisplayCapture, "Captura de pantalla", "Un monitor completo.", "Pantalla"),
        new(SourceKind.WindowCapture, "Captura de ventana", "Una ventana concreta de una aplicación.", "Ventana"),
        new(SourceKind.GameCapture, "Captura de juego", "Juegos DirectX/OpenGL/Vulkan a pantalla completa con el mínimo impacto.", "Juego"),
        new(SourceKind.VideoCaptureDevice, "Dispositivo de video", "Webcam o capturadora HDMI.", "Cámara"),
        new(SourceKind.AudioInput, "Entrada de audio", "Micrófono o interfaz de audio.", "Micrófono"),
        new(SourceKind.AudioOutput, "Audio de salida", "Lo que suena en el equipo (escritorio, juego, llamadas).", "Audio del escritorio"),
        new(SourceKind.Image, "Imagen", "PNG, JPG, GIF animado... Útil para logos y marcas de agua.", "Imagen"),
        new(SourceKind.ImageSlideshow, "Presentación de imágenes", "Varias imágenes en secuencia.", "Presentación"),
        new(SourceKind.Media, "Video o audio", "Archivo multimedia local o URL de red.", "Multimedia"),
        new(SourceKind.Text, "Texto", "Texto con fuente, color, borde y fondo.", "Texto"),
        new(SourceKind.ColorSource, "Color sólido", "Rectángulo de color: fondos, bandas y formas simples.", "Color"),
    ];

    public static string Label(SourceKind kind) => Entries.FirstOrDefault(e => e.Kind == kind)?.Label ?? kind.ToString();
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

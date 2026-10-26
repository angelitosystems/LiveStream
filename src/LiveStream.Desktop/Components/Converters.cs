using Avalonia.Data.Converters;
using Avalonia.Media;
using LiveStream.Desktop.ViewModels;
using LiveStream.Domain.Entities;

namespace LiveStream.Desktop.Components;

public static class Converters
{
    /// <summary>Opacidad plena si es verdadero, atenuada si no (iconos de estado activado/desactivado).</summary>
    public static readonly IValueConverter OnOpacity = new FuncValueConverter<bool, double>(on => on ? 1.0 : 0.45);

    // Nombres accesibles de los botones que cambian de texto según el estado.
    public static readonly IValueConverter StreamActionName = new FuncValueConverter<bool, string>(on => on ? "Detener transmisión" : "Iniciar transmisión");
    public static readonly IValueConverter RecordActionName = new FuncValueConverter<bool, string>(on => on ? "Detener grabación" : "Iniciar grabación");
    public static readonly IValueConverter StudioActionName = new FuncValueConverter<bool, string>(on => on ? "Salir del modo estudio" : "Modo estudio");

    /// <summary>Icono de la familia propia para el tipo de fuente (para las filas del panel y los selectores).</summary>
    public static readonly IValueConverter SourceKindIcon = new FuncValueConverter<SourceKind, StreamGeometry?>(SourceCatalog.Geometry);

    /// <summary>Nombre del tipo para lectores de pantalla y tooltips.</summary>
    public static readonly IValueConverter SourceKindName = new FuncValueConverter<SourceKind, string>(SourceCatalog.Label);
}

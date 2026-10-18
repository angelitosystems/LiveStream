using Avalonia.Data.Converters;

namespace StreamPilot.Desktop.Components;

public static class Converters
{
    /// <summary>Opacidad plena si es verdadero, atenuada si no (iconos de estado activado/desactivado).</summary>
    public static readonly IValueConverter OnOpacity = new FuncValueConverter<bool, double>(on => on ? 1.0 : 0.45);
}

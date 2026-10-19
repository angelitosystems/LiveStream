using LiveStream.Application.Abstractions;
using LiveStream.Domain.Entities;

namespace LiveStream.Application.Commands;

public sealed class StudioContext(StudioProject project, IStreamingEngine engine)
{
    public StudioProject Project { get; } = project;
    public IStreamingEngine Engine { get; } = engine;

    /// <summary>Transición cargada ahora en el motor (la general o la de la última escena con transición propia).</summary>
    public TransitionSettings? AppliedTransition { get; set; }

    /// <summary>
    /// Texto en vivo de una pieza de gráfico (tiempo de la cuenta atrás, hora del reloj) o null para usar los
    /// campos. Lo aporta el servicio de gráficos para que las ediciones no pisen el valor que corre.
    /// </summary>
    public Func<Domain.Graphics.Graphic, string, string?>? GraphicText { get; set; }
}

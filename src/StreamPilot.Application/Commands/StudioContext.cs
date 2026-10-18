using StreamPilot.Application.Abstractions;
using StreamPilot.Domain.Entities;

namespace StreamPilot.Application.Commands;

public sealed class StudioContext(StudioProject project, IStreamingEngine engine)
{
    public StudioProject Project { get; } = project;
    public IStreamingEngine Engine { get; } = engine;
}

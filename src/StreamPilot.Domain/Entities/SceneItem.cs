using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.Domain.Entities;

public sealed class SceneItem(Guid id, Guid sourceId, Transform transform)
{
    public Guid Id { get; } = id;
    public Guid SourceId { get; } = sourceId;
    public Transform Transform { get; set; } = transform;
    public bool Visible { get; set; } = true;
    public bool Locked { get; set; }
}

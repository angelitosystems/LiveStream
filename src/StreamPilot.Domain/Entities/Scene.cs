using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.Domain.Entities;

public sealed class Scene(Guid id, string name)
{
    private readonly List<SceneItem> _items = [];

    public Guid Id { get; } = id;
    public string Name { get; private set; } = !string.IsNullOrWhiteSpace(name) ? name : throw new ArgumentException("Nombre vacío.", nameof(name));

    /// <summary>Elementos ordenados de abajo (índice 0) hacia arriba.</summary>
    public IReadOnlyList<SceneItem> Items => _items;

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public SceneItem AddItem(Guid sourceId, Transform? transform = null)
    {
        var item = new SceneItem(Guid.NewGuid(), sourceId, transform ?? Transform.Identity);
        _items.Add(item);
        return item;
    }

    public void MoveItem(Guid itemId, int newIndex)
    {
        var item = _items.FirstOrDefault(i => i.Id == itemId) ?? throw new KeyNotFoundException($"Elemento {itemId} no encontrado.");
        _items.Remove(item);
        _items.Insert(Math.Clamp(newIndex, 0, _items.Count), item);
    }
}

using StreamPilot.Domain.ValueObjects;

namespace StreamPilot.Domain.Entities;

public sealed class Scene(Guid id, string name)
{
    private readonly List<SceneItem> _items = [];

    public Guid Id { get; } = id;
    public string Name { get; private set; } = !string.IsNullOrWhiteSpace(name) ? name : throw new ArgumentException("Nombre vacío.", nameof(name));

    /// <summary>Transición para entrar en esta escena; null = la transición general del proyecto.</summary>
    public TransitionSettings? TransitionOverride { get; set; }

    /// <summary>Elementos ordenados de abajo (índice 0) hacia arriba.</summary>
    public IReadOnlyList<SceneItem> Items => _items;

    public void Rename(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
    }

    public SceneItem AddItem(Guid sourceId, Transform? transform = null, Guid? itemId = null)
    {
        var item = new SceneItem(itemId ?? Guid.NewGuid(), sourceId, transform ?? Transform.Identity);
        _items.Add(item);
        return item;
    }

    /// <summary>Reinserta un elemento existente (usado por deshacer).</summary>
    public void InsertItem(SceneItem item, int index) => _items.Insert(Math.Clamp(index, 0, _items.Count), item);

    public SceneItem GetItem(Guid itemId) =>
        _items.FirstOrDefault(i => i.Id == itemId) ?? throw new KeyNotFoundException($"Elemento {itemId} no encontrado.");

    public bool ContainsItem(Guid itemId) => _items.Any(i => i.Id == itemId);

    public int IndexOf(Guid itemId) => _items.FindIndex(i => i.Id == itemId);

    public SceneItem RemoveItem(Guid itemId)
    {
        var item = GetItem(itemId);
        _items.Remove(item);
        return item;
    }

    public void MoveItem(Guid itemId, int newIndex)
    {
        var item = GetItem(itemId);
        _items.Remove(item);
        _items.Insert(Math.Clamp(newIndex, 0, _items.Count), item);
    }
}

// ==================================================================================================
// Jatayu Engine - Scene & ECS
// Module: Entity.cs
// Role: Lightweight entity handle wrapping underlying Arch ECS entity with component helper methods.
// ==================================================================================================

using System.Runtime.CompilerServices;
using Arch.Core;
using Arch.Core.Extensions;

namespace Jatayu.Scene;

/// <summary>
/// Lightweight, zero-overhead value-type handle representing an entity in the active <see cref="Scene"/>.
/// </summary>
public readonly struct Entity : IEquatable<Entity>
{
    private readonly Arch.Core.Entity _raw;
    private readonly Arch.Core.World _world;

    /// <summary>
    /// Constructs an entity handle around an Arch entity and its owning world.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Entity(Arch.Core.Entity raw, Arch.Core.World world)
    {
        _raw = raw;
        _world = world;
    }

    /// <summary>
    /// Gets the unique integer ID of this entity.
    /// </summary>
    public int Id => _raw.Id;

    /// <summary>
    /// Gets the underlying Arch entity structure.
    /// </summary>
    public Arch.Core.Entity RawEntity => _raw;

    /// <summary>
    /// Gets the world that owns this entity.
    /// </summary>
    public Arch.Core.World World => _world;

    /// <summary>
    /// Gets whether this entity is valid and currently alive in its world.
    /// </summary>
    public bool IsAlive => _world != null && _world.IsAlive(_raw);

    /// <summary>
    /// Adds a component struct to this entity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity Add<T>(in T component)
    {
        _world.Add(_raw, component);
        return this;
    }

    /// <summary>
    /// Checks if this entity possesses a component of type <typeparamref name="T"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has<T>() => _world.Has<T>(_raw);

    /// <summary>
    /// Retrieves a mutable reference to the entity's component of type <typeparamref name="T"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get<T>() => ref _world.Get<T>(_raw);

    /// <summary>
    /// Removes a component from this entity.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Remove<T>() => _world.Remove<T>(_raw);

    /// <summary>
    /// Destroys this entity and removes it from its owning world.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Destroy() => _world.Destroy(_raw);

    public bool Equals(Entity other) => _raw.Equals(other._raw) && Equals(_world, other._world);

    public override bool Equals(object? obj) => obj is Entity other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_raw, _world);

    public static bool operator ==(Entity left, Entity right) => left.Equals(right);

    public static bool operator !=(Entity left, Entity right) => !left.Equals(right);

    public static readonly Entity Null = default;
}

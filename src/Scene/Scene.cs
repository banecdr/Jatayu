// ==================================================================================================
// Jatayu Engine - Scene & ECS
// Module: Scene.cs
// Role: High-level scene container managing ECS world, entity lifecycle, and render pass extraction.
// ==================================================================================================

using System.Runtime.CompilerServices;
using Arch.Core;
using Arch.Core.Extensions;
using ArchWorld = Arch.Core.World;
using Jatayu.Core.Events;
using Jatayu.Core.Mathematics;
using Jatayu.Renderer.Camera;
using Jatayu.Scene.Components;
using System.Numerics;

namespace Jatayu.Scene;

/// <summary>
/// Container managing the active entity world, spatial transforms, and scene systems.
/// Built on top of Arch archetype ECS for zero-allocation performance.
/// </summary>
public sealed class Scene : IDisposable
{
    private static readonly QueryDescription TransformQuery = new QueryDescription().WithAll<Transform>();

    private readonly ArchWorld _world;
    private readonly Jatayu.Core.Events.EventHandler<OriginShiftedEvent> _originShiftHandler;
    private bool _isDisposed;

    private struct OriginShiftJob : IForEach<Transform>
    {
        public Vector3 Delta;

        public void Update(ref Transform transform)
        {
            transform.Position -= Delta;
        }
    }

    /// <summary>
    /// Gets the underlying Arch World instance.
    /// </summary>
    public ArchWorld World => _world;

    /// <summary>
    /// Creates a new empty scene with an initialized ECS world.
    /// </summary>
    public Scene()
    {
        _world = ArchWorld.Create();
        _originShiftHandler = OnOriginShifted;
        EventBus.Subscribe(_originShiftHandler);
    }

    private void OnOriginShifted(in OriginShiftedEvent e)
    {
        var job = new OriginShiftJob
        {
            Delta = new Vector3((float)e.DeltaX, (float)e.DeltaY, (float)e.DeltaZ),
        };
        _world.InlineQuery<OriginShiftJob, Transform>(in TransformQuery, ref job);
    }

    /// <summary>
    /// Creates a new entity in this scene.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity CreateEntity()
    {
        var raw = _world.Create();
        return new Entity(raw, _world);
    }

    /// <summary>
    /// Creates a new spatial entity with a transform.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity CreateEntity(in Transform transform)
    {
        var raw = _world.Create(transform);
        return new Entity(raw, _world);
    }

    /// <summary>
    /// Creates a new renderable entity with a transform and mesh renderer.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Entity CreateEntity(in Transform transform, in MeshRenderer meshRenderer)
    {
        var raw = _world.Create(transform, meshRenderer);
        return new Entity(raw, _world);
    }

    /// <summary>
    /// Destroys an entity and reclaims its ID.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void DestroyEntity(Entity entity)
    {
        if (entity.IsAlive)
        {
            _world.Destroy(entity.RawEntity);
        }
    }

    /// <summary>
    /// Executes an inlined zero-allocation struct query over components of type <typeparamref name="T0"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Query<TJob, T0>(in QueryDescription query, ref TJob job)
        where TJob : struct, IForEach<T0>
    {
        _world.InlineQuery<TJob, T0>(in query, ref job);
    }

    /// <summary>
    /// Executes an inlined zero-allocation struct query over components of types <typeparamref name="T0"/> and <typeparamref name="T1"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Query<TJob, T0, T1>(in QueryDescription query, ref TJob job)
        where TJob : struct, IForEach<T0, T1>
    {
        _world.InlineQuery<TJob, T0, T1>(in query, ref job);
    }

    /// <summary>
    /// Executes an inlined zero-allocation struct query over components of types <typeparamref name="T0"/>, <typeparamref name="T1"/>, and <typeparamref name="T2"/>.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Query<TJob, T0, T1, T2>(in QueryDescription query, ref TJob job)
        where TJob : struct, IForEach<T0, T1, T2>
    {
        _world.InlineQuery<TJob, T0, T1, T2>(in query, ref job);
    }

    /// <summary>
    /// Retrieves the primary active camera in the scene, if one exists.
    /// </summary>
    public bool TryGetActiveCamera(out CameraComponent camera, out Transform transform)
    {
        camera = default;
        transform = default;

        var query = new QueryDescription().WithAll<CameraComponent, Transform>();
        var enumerator = _world.Query(in query);

        foreach (ref var chunk in enumerator)
        {
            var cameras = chunk.GetSpan<CameraComponent>();
            var transforms = chunk.GetSpan<Transform>();

            for (int i = 0; i < chunk.Size; i++)
            {
                if (cameras[i].IsActive)
                {
                    camera = cameras[i];
                    transform = transforms[i];
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Clears all entities in the scene.
    /// </summary>
    public void Clear()
    {
        _world.Clear();
    }

    /// <summary>
    /// Disposes the underlying world and releases unmanaged archetype memories.
    /// </summary>
    public void Dispose()
    {
        if (!_isDisposed)
        {
            EventBus.Unsubscribe(_originShiftHandler);
            ArchWorld.Destroy(_world);
            _isDisposed = true;
        }
    }
}

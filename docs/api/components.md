# Component Architecture

XRENGINE composes behaviour through components that attach to `SceneNode`s. Each component inherits from `XRComponent`, gains access to the world tick scheduler, and reacts to transform changes without owning its own hierarchy. This document explains how the runtime actually builds, activates, and executes components.

## Anatomy of `XRComponent`
- Components are instantiated through `SceneNode.AddComponent`. The engine creates the object with `FormatterServices.GetUninitializedObject`, binds it to the node, calls its private constructor, fires `OnTransformChanged`, and finally assigns `World` so replication and ticks are available before any user code runs.
- `SceneNode.ComponentAdded` / `ComponentRemoved` invoke the component’s `AddedToSceneNode` / `RemovedFromSceneNode` methods, which default to no-op but allow subclasses to hook world-level resources.
- `IsActive` controls whether the component participates in ticks and interface binding. Flipping the flag drives `OnComponentActivated` and `OnComponentDeactivated`. The helper property `IsActiveInHierarchy` additionally checks the owning node’s activation state.
- Transform changes propagate through `OnTransformChanging` / `OnTransformChanged`. By default the component subscribes to `Transform.RenderMatrixChanged`, so renderable components receive render-thread matrices without polling. See [Transform Architecture](transforms.md) for device- and thread-safe matrix publication details.

## Tick Scheduling
- All world objects, including components, inherit `RegisterTick` / `UnregisterTick` from `XRWorldObjectBase`. Components typically call `RegisterTick(ETickGroup, ETickOrder, Engine.TickList.DelTick)` inside `OnComponentActivated` and rely on the default `UnregisterTicksOnStop=true` to remove them when deactivated.
- Tick groups reflect the engine’s frame stages: `Normal`, `Late`, `PrePhysics`, `DuringPhysics`, and `PostPhysics`. Each group is ordered by large integer bands (`Timers`, `Input`, `Animation`, `Logic`, `Scene`). Custom orders can be supplied by passing an explicit integer instead of `ETickOrder`.
- `VerifyInterfacesOnStart` automatically binds `IRenderable.RenderedObjects` to the active world when a component becomes active; `VerifyInterfacesOnStop` clears those bindings on shutdown.

## Component Attributes and Dependencies
- `RequireComponentsAttribute` automatically ensures required sibling components exist. When a component with this attribute is added, missing dependencies are instantiated through the same `SceneNode.AddComponent` path before the new component is finalised.
- `OneComponentAllowedAttribute` prevents multiple instances of the same type on a node. If the node already contains one, the engine aborts the add and returns the existing instance.
- Custom attributes can inherit from `XRComponentAttribute` to enforce additional policies when components are attached.

## Integration with the Scene Graph
- `SceneNode` keeps a thread-safe `EventList<XRComponent>`; components are removed automatically when destroyed or when `RemoveComponent` is called.
- Sibling lookups (`GetSiblingComponent`, `TryGetSiblingComponent`, `GetSiblingComponents`) are wrappers over the node collection and honour the attributes described above.
- Root systems (rendering, physics, UI) subscribe to `SceneNode.ComponentAdded` and `ComponentRemoved` to maintain caches. Components should therefore perform their world registrations inside `OnComponentActivated` to avoid dependence on construction order.

## Lifecycle Summary
1. `SceneNode.AddComponent<T>` constructs the component, attaches it to the node, validates attributes, and sets `World`.
2. If the node and component are active, `OnComponentActivated` fires immediately, registering ticks and binding interfaces.
3. Each frame the engine executes registered ticks according to group/order. Components may toggle `IsActive` or `UnregisterTicksOnStop` to refine participation.
4. When the node is deactivated or the component is removed, `OnComponentDeactivated` fires, ticks are cleared (unless configured otherwise), and `ComponentDestroyed` raises before the object is disposed.

## Extending the System
- Derive from `XRComponent` and override lifecycle hooks to manage resources. Always call base implementations when overriding `OnComponentActivated` / `OnComponentDeactivated` unless you intend to bypass interface verification.
- Use `RegisterTick` overloads that accept generic callbacks (`RegisterAnimationTick<T>`) when you need strongly typed `this` references without allocations.
- Prefer interfacing via `IRenderable`, `IPhysicsBody`, or other engine interfaces instead of hard-coding subsystem dependencies. `VerifyInterfacesOnStart` handles binding for known interfaces.
- Combine attributes to enforce policies: e.g., `[OneComponentAllowed]` plus `[RequireComponents(typeof(CameraComponent))]` yields a camera rig that ensures only one controller exists per node.

## Best Practices
- Keep tick handlers lightweight and avoid per-frame allocations. If work is sporadic, unregister the tick until needed.
- Treat `OnTransformRenderWorldMatrixChanged` (documented in [Transform Architecture](transforms.md)) as the preferred hook for responding to transform motion; it provides the final render matrix after interpolation.
- Use `GetOrAddComponent` or `TryAddComponent` when building prefab graphs so that repeated initialisation remains idempotent.
- When authoring editor tools, rely on the global `XRComponent.ComponentCreated` / `ComponentDestroyed` events instead of scanning the scene graph.

## Related Documentation
- [Scene Architecture](scene.md)
- [Transform Architecture](transforms.md)
- [Rendering Architecture](rendering.md)
- [Physics Architecture](physics.md)
- [Animation Architecture](animation.md)
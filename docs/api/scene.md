# Scene Architecture

XRENGINE organises gameplay objects as a scene graph that can be loaded, streamed, and rendered across multiple viewports. Every node in the graph owns a transform and a component list, while worlds and scenes provide asset-level grouping for runtime instances.

## Scene Graph Fundamentals
- `SceneNode` is the building block of the hierarchy. Each node owns a `TransformBase` derivative and an `EventList<XRComponent>` that stays thread-safe while gameplay or editor code mutates the graph.
- Setting `SceneNode.World` cascades the world pointer into the transform and all attached components so replicated properties and tick registrations become active immediately.
- Activation is explicit: toggling `IsActiveSelf` invokes `OnActivated` / `OnDeactivated`, which call `XRComponent.OnComponentActivated/Deactivated` for enabled components and propagate to child nodes whose own `IsActiveSelf` flag remains true.
- Component add/remove operations raise `ComponentAdded` / `ComponentRemoved` and call `XRComponent.AddedToSceneNode` / `RemovedFromSceneNode`, giving components a single place to acquire or release world resources.

## Transform System
- Every node owns a `TransformBase` (or subclass) that manages the local/world/render matrix stack, emits change events, and keeps cached direction vectors in sync. See [Transform Architecture](transforms.md) for the full lifecycle, dirty flag flow, and render publication details.
- Parent changes and matrix recalculation run through engine-managed queues (`TransformBase.ProcessParentReassignments`, `XRWorldInstance.AddDirtyTransform`) to keep hierarchy updates thread-safe while still allowing gameplay code to request changes from any thread.
- Nodes can swap transform implementations via `SceneNode.SetTransform`, preserving parents or children by supplying flags such as `RetainCurrentParent` and `RetainedChildrenMaintainWorldTransform`; common subclasses include `Transform`, `UITransform`, `CopyTransform`, `OrbitTransform`, and `TransformNone`.

## Worlds, Scenes, and Instances
- `XRWorld` is an asset describing the playable space. It bundles `XRScene` assets, optional `WorldSettings` (bounds, gravity, debug previews), and an optional default `GameMode`.
- `XRScene` is a lightweight asset containing a list of root `SceneNode`s. Each scene exposes an `IsVisible` flag; `XRWorldInstance.LoadScene` honours the flag to attach or detach root nodes at runtime.
- `XRWorldInstance` represents a running world bound to a window. It owns `RootNodeCollection`, the active `VisualScene3D`, the physics scene, and light caches. Timer hooks established in `BeginPlay` (`PreUpdate`, `PostUpdate`, `SwapBuffers`, etc.) drive transform recomputation, visibility gathering, and render submission.
- `RootNodeCollection` keeps the runtime copy of root nodes and caches components as they enter or leave the world. Cache callbacks are how rendering, physics, and UI subsystems register interest without scanning the entire graph every frame.

## Traversal and Queries
- `SceneNode.IterateHierarchy` and `IterateComponents<T>` provide recursive traversal utilities that respect the transform hierarchy. `HasComponent`, `TryGetComponent`, and `GetComponents` operate on the thread-safe component list without exposing internal locks.
- Path helpers (`GetPath`, `FindDescendant`, `FindDescendantByName`) offer deterministic navigation using `/`-delimited names, which is used extensively by editor tooling and serialization.
- Root-level searches typically start from `XRWorldInstance.RootNodes`, but systems that mirror nodes (for example rendering) subscribe to the cache actions exposed by `RootNodeCollection` to maintain their own spatial structures (octree or GPU buffers).

## Lifecycle Summary
1. A world is created or loaded as an `XRWorld` asset that references one or more `XRScene`s.
2. `XRWorldInstance.BeginPlay` instantiates the world, links timer callbacks, and sets each root node’s world pointer before activating nodes and components.
3. During updates, modified transforms enqueue recalculation work via `XRWorldInstance.AddDirtyTransform`. `PostUpdate` flushes the recalculations, and `GlobalSwapBuffers` publishes the new render matrices alongside any pending parent changes.
4. Rendering and physics systems consume the cached component lists while `SceneNode` events keep them in sync when gameplay code adds or removes content.

## Extending the Scene System
- Derive from `XRComponent` to add gameplay behaviour. Use the lifecycle hooks (`AddedToSceneNode`, `OnComponentActivated`, `OnComponentDeactivated`) to manage external resources and ticks.
- Implement custom transforms by extending `TransformBase` when bespoke matrix generation or debug rendering is required. Call `MarkLocalModified` / `MarkWorldModified` when overriding setters so dependent systems get notified.
- For tooling, prefer `SceneNode.IterateHierarchy` or the cache actions on `RootNodeCollection` instead of manual recursion; they avoid race conditions with background editing and maintain thread safety.

## Related Documentation
- [Rendering Architecture](rendering.md)
- [Component System](components.md)
- [Physics Architecture](physics.md)
- [Animation Architecture](animation.md)
- [VR Development Notes](vr-development.md)
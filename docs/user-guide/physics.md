# Physics

[Back to user guide](README.md)

Use this page when you want to enable physics, pick a backend, add physics components, or debug collisions. For backend internals, see [Physics Architecture](../architecture/physics/overview.md). For code-facing API usage, see [Physics API](../developer-guides/physics/physics-api.md).

## Backend Choice

PhysX is the primary physics backend. Jolt and Jitter2 integrations exist for experimentation and parity work, but PhysX is the path to use for current editor and gameplay work.

When GPU PhysX features are enabled, make missing GPU support visible during development. Do not assume a silent CPU fallback unless a task explicitly requests fallback behavior.

## Common Components

Attach physics behavior through scene components:

- dynamic rigid bodies for moving simulated actors,
- static rigid bodies for world collision,
- character controller components for controlled movement,
- physics chain components for hair, cloth, ropes, tails, and other lightweight chain simulations,
- physics chain collider components for sphere, box, capsule, and plane constraints.

Most rigid body components expect a compatible transform and automatically register or unregister their backend actor as the node is activated, deactivated, or removed.

## Queries

Use physics scene queries for gameplay selection, placement tests, and editor picking:

- raycasts for line-of-sight and pointer hits,
- sweeps for volume movement,
- overlaps for area checks,
- layer masks to filter results.

Query results resolve back to owning engine components where possible, so gameplay and tools can operate on scene objects instead of raw backend handles.

## Debugging

Enable physics debug visualization from engine or editor rendering settings. PhysX debug output can visualize shapes, contacts, joints, controllers, and other solver data.

For physics chain performance diagnostics, see [Physics Chain Performance](../developer-guides/rendering/physics-chain-performance.md).

## Deeper Docs

- [Physics Architecture](../architecture/physics/overview.md)
- [Physics API](../developer-guides/physics/physics-api.md)
- [Component System](components.md)
- [Scene System](scene.md)

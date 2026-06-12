# Components

[Back to user guide](README.md)

Components add behavior to scene nodes. Add them in the editor or from code when a node needs rendering, physics, animation, audio, networking, scripting, or tooling behavior. For lifecycle and extension details, see [Component API](../developer-guides/components/component-api.md).

## Using Components

Most workflows follow the same pattern:

1. Select or create a scene node.
2. Add the component that owns the behavior.
3. Assign assets, settings, and references in the inspector.
4. Enable debug drawing or logs when validating the result.

Some components require sibling components. The engine can add required dependencies automatically when a component declares them.

## Common Areas

- Rendering components display meshes, sky, fog, gizmos, and debug output.
- Physics components attach rigid bodies, controllers, or chain simulation.
- Animation components play clips, state machines, humanoid rigs, and IK.
- Networking components connect HTTP, WebSocket, TCP, UDP, or webhook workflows.
- UI components render and interact with native UI layouts.

## Deeper Docs

- [Component API](../developer-guides/components/component-api.md)
- [Scene System](scene.md)
- [Transforms](transforms.md)

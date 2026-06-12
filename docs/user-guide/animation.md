# Animation

[Back to user guide](README.md)

Use animation features to drive clips, state machines, humanoid rigs, blendshapes, and IK targets. For the runtime system and code-facing details, see [Animation API](../developer-guides/animation/animation-api.md).

## Common Workflows

- Play a single authored clip with an animation clip component.
- Drive gameplay or character state through an animation state machine.
- Use humanoid components to map a skeleton, apply neutral poses, and expose IK targets.
- Use VR IK components to map headset, hand, and tracker poses onto a character.
- Use blendshape parameters for face tracking or procedural expressions.

## Authoring Notes

Animation assets are reusable. Clips can target arbitrary properties, not only skeletal transforms. Blend trees and state machines combine clips and procedural values with parameter-driven transitions.

For editor clip inspection, use the animation clip editor panel where available.

## Deeper Docs

- [Animation API](../developer-guides/animation/animation-api.md)
- [VR Development](vr-development.md)
- [Component System](components.md)

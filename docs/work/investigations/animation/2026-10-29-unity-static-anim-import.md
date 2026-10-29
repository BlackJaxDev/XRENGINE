# Unity static animation import context

Status: open. `SerializedAnimationClip.Load3rdPartyStatic` constructs a
`StaticDeserializerBuilder` with `SourceStaticContext`. The context implements
`IsKnownType`, but its resolver, factory, and type inspector overrides are not
implemented.

A disposable Linux probe with a checked-in Unity animation failed in builder
construction with `NotImplementedException` from `StaticContext.GetTypeResolver`.
The failure occurs before the importer's direct file read. The probe build and
output are in the active agent validation run under `UnityAnimProbe` and
`unity-anim-probe.log`. The attempted file-read change was removed because the
existing import path could not exercise it.

The regular `AnimationClip` `.anim` route uses `AnimYamlImporter` instead. That
importer reads the main animation, reference headers, and `.meta` files through
separate direct host-file calls. A future extraction must coordinate those reads
and the captured source across recursive additive references. It must also retain
the import manifest and the existing reference and encoding behavior.

The static context needs a complete AOT-compatible resolver, factory, and type
inspector, or a separately approved authoring-only design. Switching to a
reflection-based builder would require a trimming and AOT review. This finding
does not change the current reference runtime or complete the file inventory.

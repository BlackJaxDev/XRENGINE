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

## Static generator investigation

An isolated probe used `Vecc.YamlDotNet.Analyzers.StaticGenerator` 18.1.0,
which matches the installed YamlDotNet version. Explicit `List<T>` registrations
made the generator emit invalid indexer assignments. Without those registrations,
the generated context loaded four checked-in walk clips and a synthetic nonempty
object mapping. A fifth walk clip failed on the unmodeled
`m_CorrespondingSourceObject` field. The probe did not validate NativeAOT.

An isolated copy of Data then compiled the generated context in the same
assembly as `SerializedAnimationClip`. Its normal portable source guard cannot
identify copied source paths, so the feasibility build disabled that guard for
the copy only. The generated code failed to compile because it assigned
`XRAsset.IsDirty`, whose setter is private. A shadowed read-only property did
not fix the generated assignment. These experiments did not change the branch,
its package references, or its publication gate. A safe repair needs a reviewed
static data model or generator change, full graph coverage, and the normal
portable and AOT checks. Host-file extraction remains open for this importer.

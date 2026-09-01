# Humanoid conformance avatar fixtures

These three original XRENGINE fixtures exercise the humanoid body-root
compensation Phase 10 corpus. They contain no Jax, Mitsuki, Unity sample, or
other third-party avatar data, and are available under the repository's
controlling [`LICENSE.md`](../../../../LICENSE.md).

They are binary FBX files generated locally from synthetic skeletons and a
small skinned marker mesh by `Tools/Unity/Phase10FixtureGenerator.cs` using the
Unity FBX Exporter. The arbitrary-axis fixture is then converted to Maya Z-up
with the Autodesk FBX SDK so importer axis normalization is exercised by an
authored FBX axis declaration rather than an importer-only switch. The
fixtures intentionally contain no animation takes: the corpus pairs these rigs
with the checked-in `.anim` fixtures.

| Fixture | Mapping mode | Deliberate distinction |
| --- | --- | --- |
| `conventional-standard.fbx` | automatic | Standard role names, balanced limbs, upper chest and toes. |
| `arbitrary-axes.ascii.fbx` | persisted corrections | Non-semantic names, Z-up authored axes, and a deterministic all-required-role sidecar. The historical filename remains for compatibility. |
| `lean-optional-absent.fbx` | automatic | Conventional role names but a short/lean torso, canted hips and arms, and absent optional upper chest, toes, eyes, and jaw. |

`arbitrary-axes.mapping-corrections.json` identifies the source FBX by hash and
maps each required role relative to the path-independent imported hierarchy
root (`.`), so moving or renaming the FBX does not invalidate structural role
addresses. Its
`expectedAvatarDefinitionSignature` is intentionally marked pending until the
runtime conformance probe writes the content-addressed signature; do not use
this sidecar for a passing conformance run until that value is replaced.

The `*.metadata.json` sidecars describe fixture facts only. The authoritative
corpus declaration, hashes, playback matrix, and known-answer contract live in
the parent `manifest.json`.

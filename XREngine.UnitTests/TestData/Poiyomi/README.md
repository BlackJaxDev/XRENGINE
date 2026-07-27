# Poiyomi Conversion Fixture Policy

The catalog inventory uses an original, synthetic fixture corpus for Poiyomi conversion
tests. Materials, animation clips, shader metadata, and tiny reference textures
added under this directory must be authored specifically for XRENGINE and
released under CC0-1.0.

The corpus must not copy example avatars, materials, textures, icons, or other
artwork from the Poiyomi repository. Tests that need the upstream shader source
must consume a user-provided checkout at the commit pinned by
`poiyomi-toon-9.3.64.json`; XRENGINE does not redistribute that shader.

The parity-validation corpus completes those fixture roles through
`ParityCorpus/corpus-manifest.json`. The versioned manifest records:

- unlocked and optimizer-generated/locked material pairs;
- focused feature-family and render-preset materials;
- maximal practical interaction combinations;
- UV0-UV3, vertex-color, tangent, skinning, morph, mirrored, and non-uniform
  mesh cases;
- color, linear-data, normal, mask, packed, 2D-array, and cubemap textures;
- scalar, vector, color, texture, repeated-slot, and renamed animation bindings;
- schema annotations and inactive lookalikes;
- authoring payload versions and multi-material compatibility relationships;
- fixed visual conditions, image thresholds, and performance budgets.

The PPM files are small analytical references authored for XRENGINE.
`ParityCorpus/UnityReferences/` contains the three authoritative, pinned Unity
2022.3.22f1/Poiyomi 9.3.64 captures used for visual comparisons; their capture
metadata records the upstream commit, shader, camera poses, and licenses. Live
OpenGL/Vulkan screenshots and profiler logs are disposable validation evidence
and must be written under `Build/_AgentValidation/` by
`Tools/Validate-PoiyomiParity.ps1`.

Fixture license: [CC0-1.0](https://creativecommons.org/publicdomain/zero/1.0/).

# Poiyomi Conversion Fixture Policy

Phase 0 selects an original, synthetic fixture corpus for Poiyomi conversion
tests. Materials, animation clips, shader metadata, and tiny reference textures
added under this directory must be authored specifically for XRENGINE and
released under CC0-1.0.

The corpus must not copy example avatars, materials, textures, icons, or other
artwork from the Poiyomi repository. Tests that need the upstream shader source
must consume a user-provided checkout at the commit pinned by
`poiyomi-toon-9.3.64.json`; XRENGINE does not redistribute that shader.

Required fixture roles for later phases:

- Unlocked and optimizer-generated material pairs with equivalent semantics.
- Color, linear-data, normal, height, mask, 2D-array, and cubemap texture roles.
- Opaque, cutout, transparent, additive, multiplicative, and outline states.
- Static, animated, and optimizer-renamed property bindings.
- Deliberately unsupported integration values for diagnostic coverage.

Fixture license: [CC0-1.0](https://creativecommons.org/publicdomain/zero/1.0/).

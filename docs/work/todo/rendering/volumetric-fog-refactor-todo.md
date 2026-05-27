# Volumetric Fog Refactor TODO

This phase ledger has been collapsed into the production design:

- [Volumetric Fog Production Design](../../design/rendering/volumetric-fog-production-design.md)

The separated half-resolution scatter, temporal reprojection, bilateral upscale,
and post-process composite are now the baseline architecture. Remaining polish,
XR parity, dual-lobe Henyey-Greenstein phase, optional powder brightening,
shadow integration, and froxel future work are tracked in the production design
instead of this historical TODO.

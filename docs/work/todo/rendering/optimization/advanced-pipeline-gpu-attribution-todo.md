# Advanced pipeline GPU attribution

Status: Open, October 1, 2026.
Owner: Rendering with Profiler.

The [cumulative publication investigation](../../../investigations/rendering/2026-10-01-cumulative-publication-validation.md)
retains substantial coarse GPU time after CPU publication fixes. Coarse timing
does not identify a shader or effect to optimize. The July measurements in the
[Default pipeline tracker](default-pipeline-gpu-hotspots-todo.md) describe a
different pipeline and cannot establish this workload's hotspots.

October 1 CPU follow-up: the
[continuous-camera investigation](../../../investigations/rendering/2026-10-01-cpu-stall-attribution.md)
validated program-invalidation and compact-uniform corrections, reducing sampled
allocation from 4.269 to 2.093 MB per completed present against their matched
control. Sealed snapshots and recurring temporary construction remain CPU work;
GC tails and displayed motion are unresolved. These results provide no new dense
GPU attribution, effect-specific speedup or visual acceptance. This GPU item stays
Open, and the parent cumulative gate remains NOT PASSED. Use the latest retained
CPU candidate when establishing a new frozen GPU comparison; do not mix its
measurements with historical binaries or change CPU owners during GPU pairs.

- [ ] Freeze an accepted Advanced fixture, source and observer binary. Preserve
  native/canonical row coverage, AA, output/internal resolution and quality.
- [ ] Validate dense timestamp overhead against the coarse observer in repeated
  stationary and continuous-motion pairs. Retain query identity, availability,
  disjoint/missing/duplicate samples and coverage; do not count a repeated query
  result as a new frame.
- [ ] Correlate executed passes and resources with GPU timings. Camera preferences
  and realized texture names alone do not prove that an effect executed. Repair
  unavailable camera-state metadata before interpreting false feature fields.
- [ ] Measure one effect at a time, restoring each setting before the next
  comparison. Separate AO, exposure, bloom, temporal reconstruction and required
  lighting work. Keep these diagnostic reductions out of acceptance comparisons.
- [ ] Choose a bounded shader/resource/quality-policy fix only after attribution;
  retain a matched image and temporal-quality gate, including disocclusion.
- [ ] Recheck editor/desktop and applicable multi-view paths, and report physical
  XR coverage separately. Return cumulative CPU/present/GPU tails and resource
  evidence to the parent investigation.

This item authorizes no silent quality reduction and carries no GPU speedup or
production-acceptance claim. CPU allocation/GC and command-encoding attribution
remain with the [stall-remediation tracker](../vulkan-stall-remediation-todo.md#s13i-prove-the-cumulative-fix-on-the-reported-workload)
and its linked CPU investigation.

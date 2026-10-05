# Spectator and calibration-feedback validation — 2026-09-30

## Implemented behavior

The local-player spectator uses a separate monoscopic camera, body-yaw follow
state, existing sphere-sweep boom, and the existing completion-gated offscreen
renderer. Two owned output slots preserve completed-frame consumers without
writing storage that still has readers. Output capture is optional/background
work with nonblocking completion polling. Direct desktop selection changes a
viewport camera, not possession, XR eye ownership, the tracking origin, or audio.

Eye visibility uses an explicitly reserved avatar layer, not global mesh
activation. Calibration feedback consists of a world-space text canvas,
preallocated labeled tracker markers that read the capture assignment preview,
and floor footprints. Factory attachment belongs to shared avatar composition.
See [the runtime guide](../../../developer-guides/vr/spectator-camera.md).

## Deterministic results

The focused headless build completed with **0 warnings and 0 errors**. The final
cohort passed **19/19 tests**, with no skips:

- Follow offset sign, body yaw, fallback heading, exponential damping, camera cuts,
  degenerate vectors, bounded settings, and boom recovery
- Explicit world-pose application under a rotated, translated, uniformly scaled
  parent, using `world * inverseParent` and refreshed simulation matrices
- Eye-mask hiding/restoration and calibration visibility
- Assignment labels reading the same humanoid source slots as capture
- No output publication before completion
- Pending, rejected, and failed GPU-fence states; recoverable retry versus quarantine
- Orphaned slot-writer draining and discarding completed frames from an older history

The fence cohort controls an `XRGpuFence` implementation while executing the
production capture completion and spectator orchestration paths. It is not a
physical GPU race test.

Disposable evidence under
`Build/_AgentValidation/20260930-211606-linux-calibration/`:

- `logs/spectator-build-final3.log`
- `logs/spectator-tests-final3.log`
- `reports/spectator-final3.xml`

## Native renderer regression check

The production offscreen software-Vulkan executable was rebuilt against the
integrated rendering changes with **0 warnings and 0 errors**. All **5 checks**
passed on Mesa lavapipe (llvmpipe LLVM 19.1.7). Standard and synchronization
validation were enabled, with zero errors, warnings, or diagnostic overflow.

Evidence: `logs/spectator-vulkan-build.log` and
`logs/spectator-vulkan-smoke.log` under the same run root. The run used the explicit
Debian shaderc 2025.2 compiler override documented in the
[software Vulkan guide](../../../developer-guides/testing/software-vulkan-validation.md).
The bundled Linux compiler remains unqualified for the production Vulkan 1.4 target.

## Review corrections

Independent review identified recoverable rejected-writer retention and
activation-boundary ownership risks. The implementation now distinguishes pending,
rejected, and quarantined writers; retains/drains native work across activation;
drains orphaned accepted submissions; rejects stale-history publication; serializes
cold lifecycle changes with a nonblocking render-thread gate; and explicitly
retires owned slot nodes on destruction. Final review found no remaining blocker
in that ownership slice.

## Explicit remaining acceptance

The native smoke exercises clears, a fullscreen fixture, compute, and validation.
It does **not** render a complete spectator/avatar/UI scene. Real-camera orientation,
aspect ratio, color space, post-processing, full-avatar visibility, collision visuals,
headset-readable UI, XR coexistence, focus/input/audio behavior, and performance
remain unverified pending live visual and named-hardware acceptance. No encoder
or video-recording feature is implemented.

The headless graph does not build the full desktop Bootstrap application. Its new
factory helpers and shared application wiring require the integrator's application
build and live acceptance; the focused result is not a whole-editor build pass.

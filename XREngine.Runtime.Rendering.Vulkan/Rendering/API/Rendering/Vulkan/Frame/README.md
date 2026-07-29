# Vulkan Frame

Owns explicit Vulkan frame lifecycle: swapchain acquire/recreate,
synchronization objects, frame timing, resource retirement, and the top-level
desktop render loop.

Command recording and renderer-facing command APIs stay under `Commands/`.
Backend wrapper creation stays under `BackendObjects/`.

## Desktop frame-loop ownership

`VulkanDesktopFrameCoordinator` is the durable owner.
`VulkanRenderer.FrameLoop.cs` delegates `WindowRenderCallback` to it. The
coordinator enters one atomic desktop activity publication, creates a
stack-only `VulkanFrameAttempt`, calls the phases in lifecycle order, and
always runs telemetry/finalization before releasing activity.

| File | Ownership |
| --- | --- |
| `Desktop/VulkanDesktopFrameCoordinator.cs` | Desktop phase order and the single outer exception/finalization boundary. |
| `VulkanRenderer.FrameLoop.cs` | Short renderer-facade delegation from `WindowRenderCallback`. |
| `VulkanRenderer.FrameLoop.State.cs` | Cross-phase desktop slot, accepted-attempt counter, observed-tick timestamp, and atomic activity accessors. |
| `VulkanFrameAttempt.cs` and the focused frame outcome files | Stack-only attempt identity, timing, phase, flow, disposition, command handles, and typed ownership transitions. |
| `VulkanRenderer.FrameLoop.FaultInjection.cs`, `VulkanDesktopFrameFaultInjectionState.cs`, `EVulkanDesktopFrameFaultPoint.cs` | Renderer-local, allocation-free deterministic phase fault injection without retained delegates. |
| `DesktopFrameIdentity.cs`, `DesktopFrameActivityState.cs`, `DesktopFrameActivitySnapshot.cs` | Immutable attempt identity and coherent cross-thread activity publication. |
| `VulkanDesktopFramePolicy.cs`, `VulkanDesktop*Outcome.cs`, `EVulkanDesktop*.cs` | Allocation-free preflight/acquire/present/recovery classification, ownership, reason, and transition contracts. |
| `VulkanRenderer.FrameLoop.Preflight.cs` | Surface/resource/Streamline compatibility checks and pre-acquire dispositions. |
| `VulkanRenderer.FrameLoop.Preflight.Policy.cs` | Frame-op cleanup for pre-acquire skips. |
| `VulkanRenderer.FrameLoop.SwapchainPolicy.cs` | Resize debounce, extent mismatch, recreate scheduling, and surface-loss policy. |
| `VulkanRenderer.FrameLoop.FrameSlots.cs` | Captured-slot wait, image reuse preparation, timing samples, and dynamic-uniform reset. |
| `VulkanRenderer.FrameLoop.FrameSlots.Retirement.cs` | Captured-slot retirement drain used by skipped preflight paths. |
| `VulkanRenderer.FrameLoop.Acquire.cs`, `VulkanDesktopAcquireAvailabilityTracker.cs` | Native/Streamline acquire dispatch, typed result policy, bounded timeout/not-ready recovery, and immediate acquire ownership publication. |
| `VulkanRenderer.FrameLoop.Recording.cs` | Scene, ImGui, and dynamic-text recording plus dirty-generation validation. |
| `VulkanRenderer.FrameLoop.Recording.Failures.cs` | Recording failure classification. |
| `VulkanRenderer.FrameLoop.Recovery.cs` | Common post-acquire settlement and auxiliary-failure recovery. |
| `VulkanRenderer.FrameLoop.Recovery.Policy.cs` | Pure rejected-frame recovery decisions. |
| `VulkanRenderer.FrameLoop.Recovery.Recording.cs` | Upload cancellation and recording exception settlement. |
| `VulkanRenderer.FrameLoop.Recovery.Submission.cs` | Abort-layout command construction and recovery submit bookkeeping. |
| `VulkanRenderer.FrameLoop.Recovery.SubmissionBridge.cs` | Tracked acquire-semaphore bridge submit. |
| `VulkanRenderer.FrameLoop.Recovery.Presentation.cs` | Rejected-image presentation through the shared presentation primitive. |
| `VulkanRenderer.FrameLoop.Submission.cs` | Stack-built submit arrays, tracked queue dispatch, timeline/upload publication, and collect release. |
| `VulkanRenderer.FrameLoop.Presentation.cs` | Native/Streamline present dispatch, typed result policy, and slot completion. |
| `VulkanRenderer.FrameLoop.Telemetry.cs` | Final ownership invariants and lifecycle timing publication. |
| `VulkanRenderer.FrameLoop.Telemetry.Output.cs` | Gated size/acquire/submit/present/overlay diagnostics. |
| `Features/Upscaling/VulkanRenderer.StreamlineFrameLifecycle.cs` | Streamline PCL marker and frame-generation lifecycle integration shared by submit/present. |
| `Commands/VulkanRenderer.FrameOpApi.cs` | Renderer-facing frame-op APIs such as memory barriers and framebuffer publication. |
| `Commands/VulkanRenderer.RenderStateApi.cs` | Generic renderer state APIs for color masks, clear color, render area, and indexed viewport/scissor state. |
| `BackendObjects/VulkanRenderer.RenderObjectFactory.cs` | Generic render object to Vulkan wrapper dispatch. |

## Invariants

- Attempt identity is immutable: frame number, desktop in-flight slot, start
  timestamp, and activity token are captured once.
- `SuboptimalKhr` acquires an image and semaphore; ownership must still reach
  exactly one terminal transition.
- Surface loss is a visible renderer failure/restart condition. A
  swapchain-only recreate is not described as surface recreation.
- Submit success publishes acquire consumption, timeline values, and upload
  ownership before marker, trim, or diagnostic work that may throw.
- Collect-visible release occurs before desktop presentation.
- Device loss prevents recovery from issuing additional queue work.
- OpenXR reads one coherent activity snapshot and skips the active desktop slot
  while draining completed desktop slots. Desktop in-flight slots and OpenXR
  eye frame-data slots are separate index domains.
- Desktop attempt entry/exit and OpenXR's retirement check-and-drain interval
  share `_desktopFrameRetirementGate`, so a new desktop attempt cannot enter
  between slot classification and retired-resource destruction.
- Diagnostic fault injection stores only a packed renderer-local request and
  probes explicit phase boundaries without installing delegates or allocating
  in the normal frame path.

Low-level command-buffer recording remains under `Commands/`; swapchain,
synchronization, resource-retirement, and device-loss partials remain
authoritative for their existing subsystems.

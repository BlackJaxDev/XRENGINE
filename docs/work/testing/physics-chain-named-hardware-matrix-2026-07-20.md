# Physics Chain Named-Hardware Matrix — 2026-07-20

## Primary local target

- CPU: AMD Ryzen 9 7950X3D, 16 physical cores / 32 logical processors.
- Memory: 48 GiB installed as 2 × 24 GiB Corsair
  `CMH48GX5M2B7200C36`, configured at 4800 MHz.
- GPU: NVIDIA GeForce RTX 3090, 24 GiB, driver 610.74,
  PCI bus `00000000:01:00.0`.
- Display: 2560 × 1440 at 144 Hz.
- OS: Windows 11 Pro 10.0.26200, build 26200.
- Power plan: High performance.
- SDK: .NET 10.0.301.
- Baseline commit: `12dd9359dfbbcd887c7986a6104d33c35715705c`.

The machine inventory is also stored as ignored raw evidence at
`Build/_AgentValidation/20260720-physics-chain-benchmark-contract/reports/named-hardware.json`.

## Required run controls

Every accepted run must use the benchmark preflight contract and record:

- Release build with no debugger attached;
- validation layers, verbose logging, debug displays, editor inspection, and
  capture instrumentation disabled for timing runs;
- explicit OpenGL or Vulkan backend, resolution, refresh rate, and VSync state;
- a separate profiler/capture run when hardware counters are required;
- at least 1,000 steady-state frames and 30 seconds after settle/warmup;
- three or more matched runs for each accepted matrix point;
- cold-start, structural-churn, and steady-state evidence as separate records.

## Additional target classes still required

The primary machine is suitable for the first OpenGL/Vulkan high-end desktop
gate. Cross-vendor GPU and lower-tier CPU/GPU acceptance targets remain open;
they must be named before cross-vendor and low-count latency gates can close.

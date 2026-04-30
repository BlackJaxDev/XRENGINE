# GPU Rendering Host Domains

This folder groups host-side GPU rendering orchestration by responsibility.

## Domains

- `Policy/` — feature/profile policy and selection decisions.
- `Resources/` — buffer/descriptor layout and resource ownership helpers.
- `Validation/` — parity checks and correctness validators.

Planned domains (`Dispatch/`, `Telemetry/`) are tracked in the Vulkan GPU-driven refactor TODO.

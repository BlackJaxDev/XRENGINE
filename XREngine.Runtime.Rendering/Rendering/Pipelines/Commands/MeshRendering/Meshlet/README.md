# Meshlet Rendering

Meshlet rendering files implement mesh/task/cluster-driven rendering path behavior.

## Scope

- Meshlet draw setup and submission flow.
- Meshlet-specific pass wiring and feature handling.
- Meshlet-only optimizations and dispatch patterns.

## Not in scope

- Traditional indexed mesh draw behavior.
- Global render policy/state that belongs in shared or `GPURendering` domains.

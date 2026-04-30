# Shared Mesh Rendering

This folder contains shared orchestration and utilities used by both traditional and meshlet paths.

## Scope

- Shared contracts and orchestration glue.
- Common helpers used by both rendering paths.
- Path-agnostic setup and sequencing.

## Rules

- Keep path-specific branches out of this folder.
- If a file primarily serves one path, move it to that path folder.

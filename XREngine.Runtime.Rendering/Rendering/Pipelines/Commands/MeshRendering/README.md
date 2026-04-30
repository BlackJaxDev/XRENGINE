# Mesh Rendering Command Paths

This folder contains mesh rendering command orchestration split by execution path intent.

## Subfolders

- `Traditional/` — indexed + indirect mesh draw path.
- `Meshlet/` — meshlet/task/cluster draw path.
- `Shared/` — shared orchestration and cross-path helpers.

## Where to edit

- Modify traditional draw behavior in `Traditional/`.
- Modify meshlet draw behavior in `Meshlet/`.
- Keep shared behavior and common contracts in `Shared/`.

Do not place path-specific logic in `Shared/`.

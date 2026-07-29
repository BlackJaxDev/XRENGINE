"""Export exact render-target bytes and metadata from an open rdc-cli replay.

Run after ``rdc open <capture.rdc>``:

    rdc script Tools/RenderDoc/export_phase3_targets.py \
        --arg eid=791 --arg output=Build/_AgentValidation/.../eid791 --json

The script intentionally exports native bytes instead of display-mapped PNGs.
That preserves integer identity attachments such as R32_UINT for bit-exact
Phase 3 CPU/GPU comparisons.
"""

from __future__ import annotations

import hashlib
import json
import struct
from pathlib import Path
from typing import Any


def _subresource() -> Any:
    sub = rd.Subresource()
    sub.mip = 0
    sub.slice = 0
    sub.sample = 0
    return sub


def _texture_description(resource_id: Any) -> Any:
    numeric_id = int(resource_id)
    for texture in adapter.get_textures():
        if int(texture.resourceId) == numeric_id:
            return texture
    raise RuntimeError(f"Texture {numeric_id} is not present in the replay.")


def _format_name(texture: Any) -> str:
    texture_format = texture.format
    if hasattr(texture_format, "Name"):
        return str(texture_format.Name())
    return str(getattr(texture_format, "name", texture_format))


def _resource_name(resource_id: Any) -> str:
    numeric_id = int(resource_id)
    return str(state.res_names.get(numeric_id, ""))


def _uint32_statistics(raw: bytes) -> dict[str, Any]:
    if len(raw) % 4 != 0:
        raise RuntimeError(
            f"R32_UINT payload has {len(raw)} bytes, which is not word aligned."
        )

    values = struct.iter_unpack("<I", raw)
    nonzero = 0
    minimum: int | None = None
    maximum = 0
    unique: set[int] = set()
    for (value,) in values:
        if value:
            nonzero += 1
        minimum = value if minimum is None else min(minimum, value)
        maximum = max(maximum, value)
        if len(unique) <= 4096:
            unique.add(value)

    return {
        "minimum": minimum or 0,
        "maximum": maximum,
        "nonzero_count": nonzero,
        "unique_count": len(unique),
        "unique_count_capped": len(unique) > 4096,
    }


def _export(resource_id: Any, label: str, output: Path) -> dict[str, Any]:
    texture = _texture_description(resource_id)
    raw = bytes(controller.GetTextureData(resource_id, _subresource()))
    raw_path = output / f"{label}.raw"
    raw_path.write_bytes(raw)

    format_name = _format_name(texture)
    row: dict[str, Any] = {
        "label": label,
        "resource_id": int(resource_id),
        "resource_name": _resource_name(resource_id),
        "format": format_name,
        "width": int(texture.width),
        "height": int(texture.height),
        "depth": int(getattr(texture, "depth", 1)),
        "array_size": int(getattr(texture, "arraysize", 1)),
        "sample_count": int(getattr(texture, "msSamp", 1)),
        "byte_count": len(raw),
        "sha256": hashlib.sha256(raw).hexdigest(),
        "raw_path": str(raw_path),
    }
    if "R32_UINT" in format_name.upper().replace(" ", "_"):
        row["uint32"] = _uint32_statistics(raw)
    return row


eid = int(args.get("eid", "0"))
if eid <= 0:
    raise ValueError("--arg eid=<positive event id> is required.")

output = Path(args.get("output", "")).resolve()
if not str(args.get("output", "")).strip():
    raise ValueError("--arg output=<directory> is required.")
output.mkdir(parents=True, exist_ok=True)

adapter.set_frame_event(eid, True)
pipeline = adapter.get_pipeline_state()
targets = pipeline.GetOutputTargets()

exports: list[dict[str, Any]] = []
for index, target in enumerate(targets):
    if int(target.resource) == 0:
        continue
    exports.append(_export(target.resource, f"color{index}", output))

depth = pipeline.GetDepthTarget()
if int(depth.resource) != 0:
    exports.append(_export(depth.resource, "depth", output))

manifest = {
    "schema_version": 1,
    "event_id": eid,
    "targets": exports,
}
manifest_path = output / "targets.json"
manifest_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
manifest["manifest_path"] = str(manifest_path)
result = manifest

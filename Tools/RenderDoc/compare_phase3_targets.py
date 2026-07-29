"""Compare exact Phase 3 CPU/GPU render-target exports.

The inputs are ``targets.json`` manifests produced by
``export_phase3_targets.py``. The comparator enforces the workstream-03 gates:

* exact R32_UINT render identity;
* exact finite-depth coverage;
* linear-color RMSE <= 0.5/255 and maximum channel error <= 2/255.

``--negative-control omit-object`` deterministically removes one rendered
identity from the candidate data in memory. The command succeeds only when the
comparison detects that seeded omission.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import math
import random
import struct
from collections import Counter
from pathlib import Path
from typing import Any, Iterable


RMSE_LIMIT = 0.5 / 255.0
MAX_CHANNEL_ERROR_LIMIT = 2.0 / 255.0


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--reference", required=True, type=Path)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument(
        "--negative-control",
        choices=("none", "omit-object"),
        default="none",
    )
    parser.add_argument("--seed", type=int, default=3003)
    return parser.parse_args()


def load_targets(manifest_path: Path) -> tuple[dict[str, Any], dict[str, bytes]]:
    manifest_path = manifest_path.resolve()
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    targets: dict[str, bytes] = {}
    for target in manifest["targets"]:
        raw_path = Path(target["raw_path"])
        if not raw_path.is_absolute():
            raw_path = manifest_path.parent / raw_path
        raw = raw_path.read_bytes()
        expected_hash = target["sha256"]
        actual_hash = hashlib.sha256(raw).hexdigest()
        if actual_hash != expected_hash:
            raise RuntimeError(
                f"{raw_path} hash mismatch: expected {expected_hash}, got {actual_hash}"
            )
        targets[target["label"]] = raw
    return manifest, targets


def target_rows(manifest: dict[str, Any]) -> dict[str, dict[str, Any]]:
    return {row["label"]: row for row in manifest["targets"]}


def normalized_format(value: str) -> str:
    return value.upper().replace(" ", "_")


def srgb_to_linear(value: int) -> float:
    encoded = value / 255.0
    if encoded <= 0.04045:
        return encoded / 12.92
    return ((encoded + 0.055) / 1.055) ** 2.4


SRGB_LUT = tuple(srgb_to_linear(value) for value in range(256))
UNORM8_LUT = tuple(value / 255.0 for value in range(256))


def decode_color(format_name: str, raw: bytes) -> Iterable[float]:
    fmt = normalized_format(format_name)
    if fmt == "R8G8B8A8_SRGB":
        for index, value in enumerate(raw):
            yield SRGB_LUT[value] if index % 4 != 3 else UNORM8_LUT[value]
        return
    if fmt == "R8G8B8A8_UNORM":
        for value in raw:
            yield UNORM8_LUT[value]
        return
    if fmt == "R16G16_FLOAT":
        for values in struct.iter_unpack("<ee", raw):
            yield from values
        return
    if fmt == "R16G16B16A16_FLOAT":
        for values in struct.iter_unpack("<eeee", raw):
            yield from values
        return
    if fmt == "R32G32B32A32_FLOAT":
        for values in struct.iter_unpack("<ffff", raw):
            yield from values
        return
    raise ValueError(f"Unsupported color format for tolerance comparison: {format_name}")


def compare_color(format_name: str, reference: bytes, candidate: bytes) -> dict[str, Any]:
    if reference == candidate:
        return {
            "native_bytes_exact": True,
            "rmse": 0.0,
            "maximum_channel_error": 0.0,
            "within_tolerance": True,
        }

    squared_error = 0.0
    maximum_error = 0.0
    sample_count = 0
    for expected, actual in zip(
        decode_color(format_name, reference),
        decode_color(format_name, candidate),
        strict=True,
    ):
        difference = abs(expected - actual)
        squared_error += difference * difference
        maximum_error = max(maximum_error, difference)
        sample_count += 1

    rmse = math.sqrt(squared_error / max(sample_count, 1))
    return {
        "native_bytes_exact": False,
        "rmse": rmse,
        "maximum_channel_error": maximum_error,
        "within_tolerance": (
            rmse <= RMSE_LIMIT
            and maximum_error <= MAX_CHANNEL_ERROR_LIMIT
        ),
    }


def depth_values(format_name: str, raw: bytes, pixel_count: int) -> Iterable[float]:
    fmt = normalized_format(format_name)
    if fmt == "D24S8" and len(raw) == pixel_count * 5:
        for offset in range(0, len(raw), 5):
            yield struct.unpack_from("<f", raw, offset)[0]
        return
    if fmt in {"D32_FLOAT", "D32S8"} and len(raw) in {
        pixel_count * 4,
        pixel_count * 5,
    }:
        stride = len(raw) // pixel_count
        for offset in range(0, len(raw), stride):
            yield struct.unpack_from("<f", raw, offset)[0]
        return
    raise ValueError(
        f"Unsupported depth payload: format={format_name}, bytes={len(raw)}, "
        f"pixels={pixel_count}"
    )


def finite_depth_coverage(value: float) -> bool:
    return math.isfinite(value) and value < 1.0


def compare_depth(
    format_name: str,
    reference: bytes,
    candidate: bytes,
    pixel_count: int,
) -> dict[str, Any]:
    mismatch_count = sum(
        finite_depth_coverage(expected) != finite_depth_coverage(actual)
        for expected, actual in zip(
            depth_values(format_name, reference, pixel_count),
            depth_values(format_name, candidate, pixel_count),
            strict=True,
        )
    )
    return {
        "native_bytes_exact": reference == candidate,
        "finite_depth_coverage_mismatch_count": mismatch_count,
        "finite_depth_coverage_bit_exact": mismatch_count == 0,
    }


def apply_omit_object_control(
    rows: dict[str, dict[str, Any]],
    data: dict[str, bytes],
    seed: int,
) -> tuple[dict[str, bytes], dict[str, Any]]:
    identity_row = next(
        row
        for row in rows.values()
        if normalized_format(row["format"]) == "R32_UINT"
    )
    identity_label = identity_row["label"]
    identity = bytearray(data[identity_label])
    values = [value[0] for value in struct.iter_unpack("<I", identity)]
    counts = Counter(value for value in values if value != 0)
    if not counts:
        raise RuntimeError("Negative control requires at least one non-zero identity.")

    selected_identity = random.Random(seed).choice(sorted(counts))
    selected_pixels = [
        index for index, value in enumerate(values) if value == selected_identity
    ]
    mutated = {label: bytearray(raw) for label, raw in data.items()}

    for pixel_index in selected_pixels:
        struct.pack_into("<I", mutated[identity_label], pixel_index * 4, 0)
        for label, row in rows.items():
            fmt = normalized_format(row["format"])
            if label == identity_label:
                continue
            if fmt in {"R8G8B8A8_SRGB", "R8G8B8A8_UNORM"}:
                mutated[label][pixel_index * 4 : pixel_index * 4 + 4] = b"\0\0\0\0"
            elif fmt == "R16G16_FLOAT":
                mutated[label][pixel_index * 4 : pixel_index * 4 + 4] = b"\0\0\0\0"
            elif fmt == "D24S8" and len(mutated[label]) == len(values) * 5:
                struct.pack_into("<f", mutated[label], pixel_index * 5, 1.0)

    return (
        {label: bytes(raw) for label, raw in mutated.items()},
        {
            "kind": "omit-object",
            "seed": seed,
            "selected_identity": selected_identity,
            "selected_pixel_count": len(selected_pixels),
        },
    )


def compare(args: argparse.Namespace) -> dict[str, Any]:
    reference_manifest, reference_data = load_targets(args.reference)
    candidate_manifest, candidate_data = load_targets(args.candidate)
    reference_rows = target_rows(reference_manifest)
    candidate_rows = target_rows(candidate_manifest)

    labels_match = set(reference_rows) == set(candidate_rows)
    if not labels_match:
        raise RuntimeError(
            "Target labels differ: "
            f"reference={sorted(reference_rows)}, candidate={sorted(candidate_rows)}"
        )

    shape_and_format_match = all(
        (
            reference_rows[label]["format"],
            reference_rows[label]["width"],
            reference_rows[label]["height"],
            reference_rows[label]["sample_count"],
        )
        == (
            candidate_rows[label]["format"],
            candidate_rows[label]["width"],
            candidate_rows[label]["height"],
            candidate_rows[label]["sample_count"],
        )
        for label in reference_rows
    )
    if not shape_and_format_match:
        raise RuntimeError("Target format, dimensions, or sample count differ.")

    control: dict[str, Any] = {"kind": "none"}
    if args.negative_control == "omit-object":
        candidate_data, control = apply_omit_object_control(
            candidate_rows,
            candidate_data,
            args.seed,
        )

    identity_results: dict[str, Any] = {}
    color_results: dict[str, Any] = {}
    depth_results: dict[str, Any] = {}
    for label, row in reference_rows.items():
        fmt = normalized_format(row["format"])
        reference = reference_data[label]
        candidate = candidate_data[label]
        if fmt == "R32_UINT":
            mismatch_count = sum(
                expected != actual
                for expected, actual in zip(
                    struct.iter_unpack("<I", reference),
                    struct.iter_unpack("<I", candidate),
                    strict=True,
                )
            )
            identity_results[label] = {
                "native_bytes_exact": reference == candidate,
                "mismatch_count": mismatch_count,
                "bit_exact": mismatch_count == 0,
            }
        elif fmt.startswith("D"):
            pixel_count = int(row["width"]) * int(row["height"])
            depth_results[label] = compare_depth(
                row["format"],
                reference,
                candidate,
                pixel_count,
            )
        else:
            color_results[label] = compare_color(
                row["format"],
                reference,
                candidate,
            )

    gates = {
        "shape_and_format_match": shape_and_format_match,
        "identity_bit_exact": all(
            result["bit_exact"] for result in identity_results.values()
        ),
        "finite_depth_coverage_bit_exact": all(
            result["finite_depth_coverage_bit_exact"]
            for result in depth_results.values()
        ),
        "linear_color_within_tolerance": all(
            result["within_tolerance"] for result in color_results.values()
        ),
    }
    comparison_passed = all(gates.values())
    expected_outcome_met = (
        comparison_passed
        if args.negative_control == "none"
        else not comparison_passed
    )
    return {
        "schema_version": 1,
        "reference": str(args.reference.resolve()),
        "candidate": str(args.candidate.resolve()),
        "thresholds": {
            "linear_color_rmse": RMSE_LIMIT,
            "maximum_channel_error": MAX_CHANNEL_ERROR_LIMIT,
        },
        "negative_control": control,
        "identity": identity_results,
        "depth": depth_results,
        "color": color_results,
        "gates": gates,
        "comparison_passed": comparison_passed,
        "expected_outcome_met": expected_outcome_met,
    }


def main() -> int:
    args = parse_args()
    report = compare(args)
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, indent=2), encoding="utf-8")
    print(json.dumps(report, indent=2))
    return 0 if report["expected_outcome_met"] else 1


if __name__ == "__main__":
    raise SystemExit(main())

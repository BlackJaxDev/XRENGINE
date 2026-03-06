#!/usr/bin/env python3
"""Render 3D visualizations of Unity humanoid IK curves from a text .anim file.

This parser intentionally targets the serialized YAML shape Unity writes for
AnimationClip.m_FloatCurves and m_EditorCurves.
"""

from __future__ import annotations

import argparse
import bisect
import math
import re
from dataclasses import dataclass, field
from pathlib import Path
from typing import Dict, List, Optional, Sequence, Tuple


IK_ATTR_RE = re.compile(r"^(Left|Right)(Hand|Foot)(T|Q)\.([xyzw])$")
GOAL_ORDER = ("LeftHand", "RightHand", "LeftFoot", "RightFoot")
GOAL_COLORS = {
    "LeftHand": "#d7263d",
    "RightHand": "#1b998b",
    "LeftFoot": "#2d3047",
    "RightFoot": "#ff9f1c",
}


@dataclass
class ScalarCurve:
    attribute: str = ""
    keyframes: List[Tuple[float, float]] = field(default_factory=list)


def parse_curve_section(lines: Sequence[str], section_name: str) -> List[ScalarCurve]:
    section_header = f"  {section_name}:"
    in_section = False
    curves: List[ScalarCurve] = []
    current: Optional[ScalarCurve] = None
    pending_time: Optional[float] = None

    for raw in lines:
        line = raw.rstrip("\n")

        if not in_section:
            if line == section_header:
                in_section = True
            continue

        if line.startswith("  m_") and line != section_header:
            break

        if line.strip() == "- curve:":
            if current is not None and current.attribute:
                curves.append(current)
            current = ScalarCurve()
            pending_time = None
            continue

        if current is None:
            continue

        stripped = line.strip()

        if stripped.startswith("time:"):
            pending_time = float(stripped.split(":", 1)[1].strip())
            continue

        if stripped.startswith("value:") and pending_time is not None:
            value = float(stripped.split(":", 1)[1].strip())
            current.keyframes.append((pending_time, value))
            pending_time = None
            continue

        if stripped.startswith("attribute:"):
            current.attribute = stripped.split(":", 1)[1].strip()

    if in_section and current is not None and current.attribute:
        curves.append(current)

    return curves


def parse_ik_curves(anim_path: Path, section_mode: str) -> Dict[str, Dict[str, Dict[str, List[Tuple[float, float]]]]]:
    lines = anim_path.read_text(encoding="utf-8").splitlines()

    all_curves: List[ScalarCurve] = []
    if section_mode in ("float", "both"):
        all_curves.extend(parse_curve_section(lines, "m_FloatCurves"))
    if section_mode in ("editor", "both"):
        all_curves.extend(parse_curve_section(lines, "m_EditorCurves"))

    goals: Dict[str, Dict[str, Dict[str, List[Tuple[float, float]]]]] = {}
    for curve in all_curves:
        match = IK_ATTR_RE.match(curve.attribute)
        if not match:
            continue

        side, limb, kind_suffix, component = match.groups()
        goal = f"{side}{limb}"
        kind = "translation" if kind_suffix == "T" else "rotation"

        by_kind = goals.setdefault(goal, {"translation": {}, "rotation": {}})
        # Keep first occurrence when duplicated between sections.
        by_kind[kind].setdefault(component, sorted(curve.keyframes, key=lambda kv: kv[0]))

    return goals


def sample_curve(curve: Sequence[Tuple[float, float]], t: float) -> float:
    if not curve:
        return 0.0

    times = [kv[0] for kv in curve]
    values = [kv[1] for kv in curve]

    idx = bisect.bisect_left(times, t)
    if idx < len(times) and times[idx] == t:
        return values[idx]
    if idx <= 0:
        return values[0]
    if idx >= len(times):
        return values[-1]

    t0, t1 = times[idx - 1], times[idx]
    v0, v1 = values[idx - 1], values[idx]
    if t1 == t0:
        return v1
    alpha = (t - t0) / (t1 - t0)
    return v0 + (v1 - v0) * alpha


def build_vector_path(components: Dict[str, List[Tuple[float, float]]], names: Sequence[str]) -> List[Tuple[float, ...]]:
    if any(name not in components for name in names):
        return []

    times = sorted({t for name in names for t, _ in components[name]})
    out: List[Tuple[float, ...]] = []
    for t in times:
        values = tuple(sample_curve(components[name], t) for name in names)
        out.append((t, *values))
    return out


def quat_rotate_forward(x: float, y: float, z: float, w: float) -> Tuple[float, float, float]:
    # Normalize to avoid visual artifacts from non-unit quaternions.
    length = math.sqrt(x * x + y * y + z * z + w * w)
    if length > 1e-8:
        x /= length
        y /= length
        z /= length
        w /= length

    # Rotate forward vector (0,0,1) by quaternion.
    # Equivalent to q * v * q^-1 expanded for v=(0,0,1).
    fx = 2.0 * (x * z + w * y)
    fy = 2.0 * (y * z - w * x)
    fz = 1.0 - 2.0 * (x * x + y * y)
    return fx, fy, fz


def render_plot(goals: Dict[str, Dict[str, Dict[str, List[Tuple[float, float]]]]], title: str, output: Path, show: bool) -> None:
    try:
        import matplotlib.pyplot as plt
    except ImportError as exc:
        raise SystemExit(
            "matplotlib is required. Install with: pip install matplotlib"
        ) from exc

    fig = plt.figure(figsize=(14, 7))
    ax_t = fig.add_subplot(1, 2, 1, projection="3d")
    ax_r = fig.add_subplot(1, 2, 2, projection="3d")

    plotted_any_t = False
    plotted_any_r = False

    for goal in GOAL_ORDER:
        if goal not in goals:
            continue

        color = GOAL_COLORS[goal]
        t_path = build_vector_path(goals[goal]["translation"], ("x", "y", "z"))
        if t_path:
            xs = [p[1] for p in t_path]
            ys = [p[2] for p in t_path]
            zs = [p[3] for p in t_path]
            ax_t.plot(xs, ys, zs, color=color, linewidth=2.0, label=goal)
            ax_t.scatter(xs[0], ys[0], zs[0], color=color, marker="o", s=36)
            ax_t.scatter(xs[-1], ys[-1], zs[-1], color=color, marker="^", s=42)
            plotted_any_t = True

        q_path = build_vector_path(goals[goal]["rotation"], ("x", "y", "z", "w"))
        if q_path:
            fx, fy, fz = [], [], []
            for _, qx, qy, qz, qw in q_path:
                dx, dy, dz = quat_rotate_forward(qx, qy, qz, qw)
                fx.append(dx)
                fy.append(dy)
                fz.append(dz)
            ax_r.plot(fx, fy, fz, color=color, linewidth=2.0, label=goal)
            ax_r.scatter(fx[0], fy[0], fz[0], color=color, marker="o", s=36)
            ax_r.scatter(fx[-1], fy[-1], fz[-1], color=color, marker="^", s=42)
            plotted_any_r = True

    ax_t.set_title("IK Translation Curves (T)")
    ax_t.set_xlabel("X")
    ax_t.set_ylabel("Y")
    ax_t.set_zlabel("Z")
    if plotted_any_t:
        ax_t.legend(loc="best")

    ax_r.set_title("IK Rotation Forward Vectors (Q)")
    ax_r.set_xlabel("X")
    ax_r.set_ylabel("Y")
    ax_r.set_zlabel("Z")
    ax_r.set_xlim(-1.1, 1.1)
    ax_r.set_ylim(-1.1, 1.1)
    ax_r.set_zlim(-1.1, 1.1)
    if plotted_any_r:
        ax_r.legend(loc="best")

    fig.suptitle(title)
    fig.tight_layout()

    output.parent.mkdir(parents=True, exist_ok=True)
    fig.savefig(output, dpi=160)
    print(f"Saved plot: {output}")

    if show:
        plt.show()


def main() -> None:
    parser = argparse.ArgumentParser(description="Visualize Unity humanoid IK curves from a .anim file")
    parser.add_argument(
        "--anim",
        type=Path,
        default=Path("Assets/Walks/Sexy Walk.anim"),
        help="Path to Unity .anim file",
    )
    parser.add_argument(
        "--section",
        choices=("float", "editor", "both"),
        default="float",
        help="Curve section to parse",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=Path("Build/Logs/sexy-walk-ik-curves.png"),
        help="Output image path",
    )
    parser.add_argument(
        "--show",
        action="store_true",
        help="Display interactive plot window",
    )

    args = parser.parse_args()

    if not args.anim.exists():
        raise SystemExit(f"Anim file not found: {args.anim}")

    goals = parse_ik_curves(args.anim, args.section)
    if not goals:
        raise SystemExit("No IK channels found. Check file/section selection.")

    title = f"Unity IK Curves: {args.anim.name} [{args.section}]"
    render_plot(goals, title, args.output, args.show)


if __name__ == "__main__":
    main()

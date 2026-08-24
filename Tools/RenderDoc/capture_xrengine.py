"""Capture an XRENGINE Vulkan frame with explicit cohort environment settings.

rdc-cli 0.5.6 uses POSIX command-line quoting on Windows and does not expose
RenderDoc's environment-modification list. This launcher calls the same local
RenderDoc Python API directly so cohort selection is deterministic.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import shutil
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

from rdc.capture_core import run_target_control_loop, terminate_process
from rdc.discover import find_renderdoc


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--settings", required=True, type=Path)
    parser.add_argument(
        "--editor",
        type=Path,
        default=Path(
            "Build/Editor/Release/AnyCPU/Release/"
            "net10.0-windows7.0/XREngine.Editor.exe"
        ),
    )
    parser.add_argument("--run-root", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--frame", type=int, default=900)
    parser.add_argument(
        "--trigger",
        action="store_true",
        help="Trigger the capture immediately after scene/camera settling instead of queuing an absolute frame.",
    )
    parser.add_argument("--timeout", type=float, default=240.0)
    parser.add_argument("--strategy", default="GpuIndirectZeroReadback")
    parser.add_argument("--material-path", default="BindlessMaterialTable")
    parser.add_argument("--occlusion-mode", default="Disabled")
    parser.add_argument("--profile-scene", default="")
    parser.add_argument("--profile-camera", default="")
    parser.add_argument("--profile-lights", default="")
    parser.add_argument("--profile-viewport", default="")
    parser.add_argument("--mcp-port", type=int)
    parser.add_argument("--camera-position", nargs=3, type=float)
    parser.add_argument("--camera-look-at", nargs=3, type=float)
    parser.add_argument("--scene-settle-seconds", type=float, default=0.0)
    parser.add_argument("--camera-settle-seconds", type=float, default=2.0)
    return parser.parse_args()


def resolve_repo_path(repo_root: Path, path: Path) -> Path:
    path = path.expanduser()
    if not path.is_absolute():
        path = repo_root / path
    return path.resolve()


def make_environment_modifications(rd: object, values: dict[str, str]) -> list[object]:
    modifications = []
    for name, value in values.items():
        modification = rd.EnvironmentModification()
        modification.name = name
        modification.value = value
        modification.mod = rd.EnvMod.Set
        modification.sep = rd.EnvSep.NoSep
        modifications.append(modification)
    return modifications


def discover_target(rd: object, timeout: float = 5.0) -> int:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        latest = 0
        ident = rd.EnumerateRemoteTargets("localhost", 0)
        while ident:
            latest = ident
            ident = rd.EnumerateRemoteTargets("localhost", ident)
        if latest:
            return latest
        time.sleep(0.25)
    return 0


def call_mcp(port: int, method: str, params: dict[str, object] | None = None) -> dict:
    body: dict[str, object] = {
        "jsonrpc": "2.0",
        "id": str(uuid.uuid4()),
        "method": method,
    }
    if params is not None:
        body["params"] = params

    request = urllib.request.Request(
        f"http://localhost:{port}/mcp/",
        data=json.dumps(body).encode("utf-8"),
        headers={"Content-Type": "application/json"},
        method="POST",
    )
    with urllib.request.urlopen(request, timeout=10.0) as response:
        payload = json.loads(response.read().decode("utf-8"))
    if "error" in payload:
        raise RuntimeError(f"MCP {method} failed: {payload['error']}")
    return payload


def raise_for_mcp_tool_error(response: dict) -> None:
    result = response.get("result")
    if not isinstance(result, dict) or not result.get("isError", False):
        return

    messages = []
    content = result.get("content")
    if isinstance(content, list):
        for item in content:
            if isinstance(item, dict) and isinstance(item.get("text"), str):
                messages.append(item["text"])
    detail = "; ".join(messages) or "MCP tool reported isError=true"
    raise RuntimeError(detail)


def wait_for_mcp(port: int, timeout: float = 60.0) -> None:
    deadline = time.monotonic() + timeout
    last_error: Exception | None = None
    while time.monotonic() < deadline:
        try:
            call_mcp(port, "ping")
            return
        except (OSError, RuntimeError, urllib.error.URLError) as error:
            last_error = error
            time.sleep(0.25)
    raise RuntimeError(f"MCP did not become ready on port {port}: {last_error}")


def set_fixed_camera(args: argparse.Namespace) -> dict:
    if args.mcp_port is None:
        raise ValueError("--camera-position requires --mcp-port")
    if args.camera_look_at is None:
        raise ValueError("--camera-position requires --camera-look-at")

    wait_for_mcp(args.mcp_port)
    if args.scene_settle_seconds > 0:
        time.sleep(args.scene_settle_seconds)

    position = args.camera_position
    look_at = args.camera_look_at
    deadline = time.monotonic() + args.timeout
    while True:
        try:
            response = call_mcp(
                args.mcp_port,
                "tools/call",
                {
                    "name": "set_editor_camera_view",
                    "arguments": {
                        "position_x": position[0],
                        "position_y": position[1],
                        "position_z": position[2],
                        "look_at_x": look_at[0],
                        "look_at_y": look_at[1],
                        "look_at_z": look_at[2],
                        "duration": 0.0,
                    },
                },
            )
            raise_for_mcp_tool_error(response)
            continuous_render_response = call_mcp(
                args.mcp_port,
                "tools/call",
                {
                    "name": "set_editor_camera_render_on_demand",
                    "arguments": {"enabled": False, "invalidate_view": True},
                },
            )
            raise_for_mcp_tool_error(continuous_render_response)
            break
        except RuntimeError as error:
            detail = str(error)
            transient_camera_state = (
                "No active world instance" in detail
                or "No editor camera pawn available" in detail
                or "Editor camera transform is unavailable" in detail
                or "requires unavailable capabilities: World" in detail
            )
            if not transient_camera_state:
                raise
            if time.monotonic() >= deadline:
                raise RuntimeError(
                    "The editor MCP server became ready, but no active world instance "
                    "was available before the capture timeout."
                ) from error
            time.sleep(0.25)
    if args.camera_settle_seconds > 0:
        time.sleep(args.camera_settle_seconds)
    return response


def finalize_capture(source: Path, output: Path) -> bool:
    """Copy a numbered RenderDoc capture and remove only its verified duplicate."""
    if source == output:
        return False

    shutil.copy2(source, output)
    if source.stat().st_size != output.stat().st_size:
        raise RuntimeError("Copied RenderDoc capture size does not match its source")

    with source.open("rb") as source_stream, output.open("rb") as output_stream:
        source_hash = hashlib.file_digest(source_stream, "sha256")
        output_hash = hashlib.file_digest(output_stream, "sha256")
    if source_hash.digest() != output_hash.digest():
        raise RuntimeError("Copied RenderDoc capture hash does not match its source")

    source.unlink()
    return True

def main() -> int:
    args = parse_args()
    repo_root = Path(__file__).resolve().parents[2]
    settings = resolve_repo_path(repo_root, args.settings)
    editor = resolve_repo_path(repo_root, args.editor)
    run_root = resolve_repo_path(repo_root, args.run_root)
    output = resolve_repo_path(repo_root, args.output)

    if not settings.is_file():
        raise FileNotFoundError(f"Unit-test settings file does not exist: {settings}")
    if not editor.is_file():
        raise FileNotFoundError(f"Editor executable does not exist: {editor}")
    if args.frame < 1:
        raise ValueError("--frame must be at least 1")
    if args.timeout <= 0:
        raise ValueError("--timeout must be positive")
    if args.mcp_port is not None and not 1 <= args.mcp_port <= 65535:
        raise ValueError("--mcp-port must be between 1 and 65535")
    if (args.camera_position is None) != (args.camera_look_at is None):
        raise ValueError("--camera-position and --camera-look-at must be provided together")
    if args.scene_settle_seconds < 0 or args.camera_settle_seconds < 0:
        raise ValueError("settle durations cannot be negative")

    run_root.mkdir(parents=True, exist_ok=True)
    output.parent.mkdir(parents=True, exist_ok=True)

    environment = {
        "XRE_WORLD_MODE": "UnitTesting",
        "XRE_UNIT_TEST_WORLD_KIND": "Default",
        "XRE_UNIT_TEST_WORLD_SETTINGS_PATH": str(settings),
        "XRE_UNIT_TEST_RENDER_API": "Vulkan",
        "XRE_FORCE_MESH_SUBMISSION_STRATEGY": args.strategy,
        "XRE_ZERO_READBACK_MATERIAL_DRAW_PATH": args.material_path,
        "XRE_VULKAN_DIAGNOSTIC_PRESET": "RenderDocFriendly",
        "XRE_VULKAN_COMMAND_BUFFER_LABELS": "1",
        "XRE_AGENT_VALIDATION_RUN_ROOT": str(run_root),
        "XRE_OCCLUSION_CULLING_MODE": args.occlusion_mode,
    }
    optional_environment = {
        "XRE_PROFILE_SCENE": args.profile_scene,
        "XRE_PROFILE_CAMERA": args.profile_camera,
        "XRE_PROFILE_LIGHTS": args.profile_lights,
        "XRE_PROFILE_VIEWPORT": args.profile_viewport,
    }
    environment.update(
        (name, value)
        for name, value in optional_environment.items()
        if value.strip()
    )

    editor_arguments = ["--unit-testing"]
    if args.mcp_port is not None:
        editor_arguments.extend(
            ["--mcp", "--mcp-allow-all", "--mcp-port", str(args.mcp_port)]
        )

    rd = find_renderdoc()
    if rd is None:
        raise RuntimeError("RenderDoc Python API is unavailable; run `rdc doctor`")

    capture_options = rd.GetDefaultCaptureOptions()
    capture_options.refAllResources = True
    environment_modifications = make_environment_modifications(rd, environment)
    command_line = subprocess.list2cmdline(editor_arguments)

    launch = rd.ExecuteAndInject(
        str(editor),
        str(repo_root),
        command_line,
        environment_modifications,
        str(output),
        capture_options,
        False,
    )
    if launch.result != 0:
        raise RuntimeError(f"RenderDoc injection failed with code {launch.result}")

    ident = launch.ident or discover_target(rd)
    if not ident:
        raise RuntimeError("RenderDoc injected the editor but returned no target ident")

    target_control = rd.CreateTargetControl("", ident, "xrengine-capture", True)
    if target_control is None:
        raise RuntimeError(f"Could not connect to RenderDoc target {ident}")

    pid = 0
    camera_response: dict | None = None
    try:
        pid = target_control.GetPID()
        if args.camera_position is not None:
            camera_response = set_fixed_camera(args)
        result = run_target_control_loop(
            target_control,
            frame=None if args.trigger else args.frame,
            timeout=args.timeout,
        )
        if not result.success:
            raise RuntimeError(result.error or "RenderDoc capture failed")

        source = Path(result.path).resolve()
        source_capture_removed = finalize_capture(source, output)

        print(
            json.dumps(
                {
                    "capture": str(output),
                    "source_capture": str(source),
                    "source_capture_removed": source_capture_removed,
                    "capture_trigger": "immediate" if args.trigger else "absolute_frame",
                    "requested_frame": None if args.trigger else args.frame,
                    "frame": result.frame,
                    "bytes": output.stat().st_size,
                    "api": result.api,
                    "ident": ident,
                    "pid": pid,
                    "environment": environment,
                    "camera_response": camera_response,
                },
                indent=2,
            )
        )
        return 0
    finally:
        target_control.Shutdown()
        if pid:
            terminate_process(pid)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except Exception as error:
        print(f"error: {error}", file=sys.stderr)
        raise SystemExit(1)

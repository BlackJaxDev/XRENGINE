#!/usr/bin/env python3
"""Trigger and copy a RenderDoc capture from a live target-control connection."""

import argparse
import os
import sys
import time
from datetime import datetime
from pathlib import Path
from typing import Optional


def import_renderdoc(renderdoc_python_path: Optional[str]):
    if renderdoc_python_path:
        sys.path.insert(0, renderdoc_python_path)

    try:
        import renderdoc as rd  # type: ignore[import-not-found]
    except ImportError as exc:
        raise SystemExit(
            "Could not import the RenderDoc Python module. Set "
            "RENDERDOC_PYTHON_PATH to RenderDoc's Python module directory or "
            "pass --renderdoc-python-path."
        ) from exc

    return rd


def observe_pretrigger_captures(rd, target, timeout_sec: float) -> set[int]:
    """Collect capture announcements that arrive before the trigger timestamp is recorded."""
    announced_capture_ids: set[int] = set()
    deadline = time.monotonic() + timeout_sec

    while time.monotonic() < deadline:
        msg = target.ReceiveMessage(None)
        if msg is None or msg.type == rd.TargetControlMessageType.Noop:
            time.sleep(0.05)
            continue

        if msg.type == rd.TargetControlMessageType.NewCapture:
            capture = msg.newCapture
            announced_capture_ids.add(int(capture.captureId))
            print(
                "discarded pre-trigger capture: "
                f"id={capture.captureId} frame={capture.frameNumber} path={capture.path!r}"
            )
        elif msg.type == rd.TargetControlMessageType.Disconnected:
            raise SystemExit("RenderDoc target disconnected before capture trigger.")

    return announced_capture_ids


def wait_for_new_capture(
    rd,
    target,
    pretrigger_capture_ids: set[int],
    trigger_timestamp_utc: int,
    timeout_sec: float,
) -> int:
    deadline = time.monotonic() + timeout_sec

    while time.monotonic() < deadline:
        msg = target.ReceiveMessage(None)
        if msg is None or msg.type == rd.TargetControlMessageType.Noop:
            time.sleep(0.05)
            continue

        if msg.type == rd.TargetControlMessageType.NewCapture:
            capture = msg.newCapture
            capture_id = int(capture.captureId)
            capture_timestamp_utc = int(capture.timestamp)
            if capture_id in pretrigger_capture_ids or capture_timestamp_utc < trigger_timestamp_utc:
                print(
                    "discarded stale capture announcement: "
                    f"id={capture.captureId} frame={capture.frameNumber} "
                    f"timestamp={capture_timestamp_utc} path={capture.path!r}"
                )
                continue
            print(
                f"capture ready: id={capture.captureId} frame={capture.frameNumber} "
                f"timestamp={capture_timestamp_utc} path={capture.path!r}"
            )
            return capture_id

        if msg.type == rd.TargetControlMessageType.CaptureProgress:
            print(f"capture progress: {msg.capProgress:.2f}")
        elif msg.type == rd.TargetControlMessageType.Busy:
            print(f"target busy: owned by {msg.busy.clientName!r}")
        elif msg.type == rd.TargetControlMessageType.Disconnected:
            raise SystemExit("RenderDoc target disconnected while waiting for capture.")

        time.sleep(0.05)

    raise SystemExit(f"No capture was reported within {timeout_sec:.0f} seconds.")


def wait_for_copy(rd, target, capture_id: int, timeout_sec: float) -> None:
    deadline = time.monotonic() + timeout_sec

    while time.monotonic() < deadline:
        msg = target.ReceiveMessage(None)
        if msg is None or msg.type == rd.TargetControlMessageType.Noop:
            time.sleep(0.05)
            continue

        if msg.type == rd.TargetControlMessageType.CaptureCopied:
            copied = msg.newCapture
            if int(copied.captureId) == capture_id:
                print(f"capture copied: id={copied.captureId} path={copied.path!r}")
                return
        elif msg.type == rd.TargetControlMessageType.Disconnected:
            raise SystemExit("RenderDoc target disconnected while copying capture.")

        time.sleep(0.05)

    raise SystemExit(f"Capture copy did not complete within {timeout_sec:.0f} seconds.")


def default_output_path() -> Path:
    stamp = datetime.now().strftime("%Y%m%d-%H%M%S")
    return Path("McpCaptures") / "rdc" / f"capture-{stamp}.rdc"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--host", default="localhost", help="RenderDoc target-control host")
    parser.add_argument("--ident", type=int, required=True, help="RenderDoc target-control ident")
    parser.add_argument("--client-name", default="xrengine-rdc-agent", help="RenderDoc client name")
    parser.add_argument("--output", type=Path, default=default_output_path(), help="Destination .rdc path")
    parser.add_argument("--capture-timeout", type=float, default=60.0, help="Seconds to wait for capture completion")
    parser.add_argument("--copy-timeout", type=float, default=120.0, help="Seconds to wait for copy completion")
    parser.add_argument(
        "--pretrigger-observation-timeout",
        type=float,
        default=1.0,
        help="Seconds to observe delayed pre-existing capture announcements before triggering",
    )
    parser.add_argument(
        "--renderdoc-python-path",
        default=os.environ.get("RENDERDOC_PYTHON_PATH"),
        help="Directory containing renderdoc.py; defaults to RENDERDOC_PYTHON_PATH",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    rd = import_renderdoc(args.renderdoc_python_path)

    output = args.output
    output.parent.mkdir(parents=True, exist_ok=True)

    target = rd.CreateTargetControl(args.host, args.ident, args.client_name, True)
    if target is None:
        raise SystemExit(f"Could not connect to RenderDoc target {args.host}:{args.ident}.")

    try:
        print(f"connected: api={target.GetAPI()!r} pid={target.GetPID()} target={target.GetTarget()!r}")
        pretrigger_capture_ids = observe_pretrigger_captures(
            rd, target, args.pretrigger_observation_timeout)
        print(f"pre-trigger capture baseline: {sorted(pretrigger_capture_ids)}")
        trigger_timestamp_utc = int(time.time())
        target.TriggerCapture(1)
        print(f"capture trigger sent: timestamp>={trigger_timestamp_utc}")

        capture_id = wait_for_new_capture(
            rd, target, pretrigger_capture_ids, trigger_timestamp_utc, args.capture_timeout)
        target.CopyCapture(capture_id, str(output))
        print(f"copy requested: {output}")

        wait_for_copy(rd, target, capture_id, args.copy_timeout)
    finally:
        target.Shutdown()

    print(f"done: {output} ({output.stat().st_size if output.exists() else 0} bytes)")


if __name__ == "__main__":
    main()

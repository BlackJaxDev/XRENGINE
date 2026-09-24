"""Collect bounded live-editor evidence; this does not certify visual correctness.

Uses only the Python standard library and the repository's named-session manager.
Refuses to attach to a running editor. Mutations affect the disposable session;
no scene or material assets are saved. Detailed replies stay in the output tree.
"""

import argparse
import datetime as dt
import hashlib
import json
import shutil
import subprocess
import time
import urllib.request
from pathlib import Path


REPO = Path(__file__).resolve().parents[1]
SESSIONS = REPO / "Build/_AgentValidation/00000000-000000-shared/mcp-sessions"


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


class Collector:
    def __init__(self, args):
        self.args = args
        self.output = Path(args.output).resolve()
        if not self.output.is_relative_to(REPO / "Build/_AgentValidation"):
            raise ValueError("Output must be beneath Build/_AgentValidation.")
        self.output.mkdir(parents=True, exist_ok=False)
        for name in ("replies", "screenshots", "logs"):
            (self.output / name).mkdir()
        self.counter = 0
        self.endpoint = None
        self.owned = False
        self.rows = []
        self.notes = []
        self.started = dt.datetime.now(dt.timezone.utc).isoformat()

    def save(self, name, value):
        (self.output / name).write_text(json.dumps(value, indent=2), encoding="utf-8")

    def manifest(self):
        matches = [p for p in SESSIONS.glob("*/session.json")
                   if read_json(p).get("name") == self.args.session]
        if len(matches) != 1:
            raise RuntimeError("Exactly one existing named session is required.")
        return read_json(matches[0])

    def manage(self, action):
        cmd = ["pwsh", "-NoProfile", "-File", str(REPO / "Tools/Manage-McpEditorSession.ps1"),
               action, "-Name", self.args.session, "-AsJson"]
        if action == "Start":
            cmd += ["-Configuration", "Release", "-PermissionPolicy", "AllowDestructive",
                    "-StartupTimeoutSeconds", "240",
                    "-SessionEnvironmentFile", str(Path(self.args.environment).resolve())]
            if self.args.no_build:
                cmd.append("-NoBuild")
        with (self.output / "logs" / (action.lower() + ".log")).open("w", encoding="utf-8") as log:
            process = subprocess.Popen(cmd, cwd=REPO, stdout=log, stderr=subprocess.STDOUT)
            deadline = time.monotonic() + (900 if action == "Start" else 90)
            # The editor can inherit launcher handles, so readiness comes from
            # the manager's manifest, not from an inherited pipe reaching EOF.
            while time.monotonic() < deadline:
                manifest = self.manifest()
                if manifest.get("state") == ("Ready" if action == "Start" else "Stopped"):
                    return manifest
                if process.poll() is not None:
                    if process.returncode:
                        raise RuntimeError(f"Session {action} failed; see logs.")
                time.sleep(2)
            raise TimeoutError(f"Session {action} timed out; see logs.")

    def rpc(self, method, params=None):
        self.counter += 1
        request = {"jsonrpc": "2.0", "id": self.counter, "method": method}
        if params is not None:
            request["params"] = params
        name = (params or {}).get("name", method.replace("/", "-"))
        request_path = f"replies/{self.counter:04d}-{name}"
        self.save(request_path + "-request.json", request)
        req = urllib.request.Request(self.endpoint, json.dumps(request).encode(),
                                     {"Content-Type": "application/json"})
        try:
            with urllib.request.urlopen(req, timeout=120) as response:
                reply = json.load(response)
        except Exception as exc:
            self.save(request_path + "-transport-error.json", {"error": str(exc)})
            raise
        self.save(request_path + ".json", reply)
        if reply.get("error"):
            raise RuntimeError(str(reply["error"]))
        result = reply.get("result", {})
        if result.get("isError"):
            raise RuntimeError(str(result.get("content", result))[:1500])
        return result

    def call(self, tool_name, **arguments):
        result = self.rpc("tools/call", {"name": tool_name, "arguments": arguments})
        return result.get("structuredContent", result.get("data", {}))

    def camera(self, x=-20, y=2, z=4):
        self.call("set_editor_camera_view", position_x=x, position_y=y,
                  position_z=z, pitch=0, yaw=0, roll=0, duration=0)

    def wait_for_world(self):
        print("Waiting for the fixture world", flush=True)
        deadline = time.monotonic() + 180
        while True:
            try:
                return self.call("list_worlds")
            except RuntimeError as exc:
                if "requires unavailable capabilities: World" not in str(exc):
                    raise
                if time.monotonic() >= deadline:
                    raise TimeoutError("Fixture world was unavailable after 180 seconds") from exc
                time.sleep(5)

    def sample(self):
        stats = self.call("get_render_profiler_stats")
        vk = stats["vulkan"]
        frame = vk["frame_lifecycle"]
        meter = vk["retired_resources"]["metering"]
        descriptor = vk["descriptors"]
        validation = vk.get("validation", {}).get("cumulative") or {}
        return dict(utc=dt.datetime.now(dt.timezone.utc).isoformat(),
                    authority=frame["authority_id"], outcome=frame["outcome"],
                    presents=meter["currentGenerationPresentsCompleted"],
                    native=meter["liveResourceCount"], sets=meter["trackedDescriptorSetCount"],
                    mesh_variants=descriptor["mesh_descriptor_allocation_variants"],
                    mesh_sets=descriptor["mesh_descriptor_allocated_sets"],
                    pending=meter["pendingRetirementCount"],
                    binding_failures=descriptor["binding_failures"],
                    skipped_draws=descriptor["skipped_draws"],
                    validation=validation, latest_failure=frame.get("present_now_latest_failure"))

    def phase(self, name, seconds=30):
        print(f"Collecting {name} ({seconds}s)", flush=True)
        samples = [self.sample()]
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            time.sleep(min(10, max(0, deadline - time.monotonic())))
            samples.append(self.sample())
        end = samples[-1]
        same = samples[-2]["authority"] == end["authority"]
        issues = []
        if not same or end["presents"] <= samples[-2]["presents"]:
            issues.append("Final sample interval has no confirmed present progress")
        if end["outcome"] != "Completed":
            issues.append("Final sampled frame did not complete")
        if end["pending"] or end["binding_failures"] or end["skipped_draws"]:
            issues.append("Final sample has retirement/binding/skipped-draw findings")
        validation = end["validation"]
        if not all(validation.get(key) for key in
                   ("standardValidationEnabled", "synchronizationValidationEnabled", "debugMessengerActive")):
            issues.append("Full validation configuration not confirmed")
        if validation.get("errorCount", 0) or validation.get("overflowCount", 0):
            issues.append("Cumulative Vulkan errors or overflow require log review")
        row = dict(name=name, seconds=seconds, status="REVIEW" if issues else "TELEMETRY_OK",
                   issues=issues, samples=samples, visual_review="PENDING")
        self.rows.append(row)
        self.save(f"{name}.json", row)
        try:
            capture = self.call("capture_viewport_screenshot", output_dir=str(self.output / "screenshots"))
            row["capture"] = capture
            self.save(f"{name}.json", row)
        except Exception as exc:
            row["issues"].append("Screenshot capture failed: " + str(exc))
            row["status"] = "REVIEW"
        self.call("get_texture_streaming_summary")
        self.call("get_advanced_profile_diagnostics")
        self.report()

    def attempt(self, name, action):
        try:
            action()
        except Exception as exc:
            self.notes.append({"case": name, "status": "INCOMPLETE", "reason": str(exc)})
            print(f"{name}: incomplete; details saved", flush=True)
            self.report()

    def mutations(self):
        nodes = self.call("find_nodes_by_name", name="Sponza").get("nodes", [])
        if not nodes:
            raise RuntimeError("Sponza fixture node unavailable; no arbitrary node selected")
        node = self.call("get_scene_node_info", node_id=nodes[0]["id"])
        self.save("mutation-target.json", node)
        transform = node.get("transform")
        if transform:
            pos = {k.lower(): v for k, v in transform["translation"].items()}
            args = dict(node_id=node["id"], **{"translation_" + k: pos[k] for k in "xyz"})
            try:
                self.call("set_node_transform", **dict(args, translation_y=pos["y"] + 0.25))
                self.phase("transform-changed", 20)
            finally:
                self.call("set_node_transform", **args)
            self.phase("transform-restored", 20)
        try:
            self.call("set_node_active", node_id=node["id"], is_active=False)
            self.phase("node-inactive", 20)
        finally:
            self.call("set_node_active", node_id=node["id"], is_active=node["isActive"])
        self.phase("node-reactivated", 20)
        # Resolve a real model material instead of guessing an asset identifier.
        model_nodes = self.call("find_nodes_by_type", component_type="ModelComponent").get("nodes", [])
        candidates = [node] + [self.call("get_scene_node_info", node_id=n["id"])
                               for n in model_nodes[:8] if "sponza" in n.get("path", "").lower()]
        for candidate in candidates:
            for component in candidate.get("components", []):
                if not component["type"].endswith(".ModelComponent"):
                    continue
                material = self.call("get_material_uniforms", node_id=candidate["id"],
                                     component_id=component["id"])
                for uniform in material.get("uniforms", []):
                    value = uniform.get("value")
                    if uniform["name"].lower() not in ("roughness", "metallic"):
                        continue
                    if isinstance(value, str):
                        value = float(value)
                    if not isinstance(value, (int, float)):
                        continue
                    args = dict(material_id=material["materialId"], uniform_name=uniform["name"])
                    try:
                        self.call("set_material_uniform", **args, value=0.2 if value > 0.5 else 0.8)
                        self.call("get_material_uniforms", material_id=material["materialId"])
                        self.phase("material-changed", 20)
                    finally:
                        self.call("set_material_uniform", **args, value=value)
                    self.phase("material-restored", 20)
                    return
        raise RuntimeError("No bounded scalar material target found; material mutation not covered")

    def restart(self, number):
        self.call("restart_renderer", backend="vulkan", first_frame_timeout_ms=30000)
        self.camera()
        self.phase(f"restart{number}-A")
        self.camera(-15, 3, 2)
        self.phase(f"restart{number}-B")
        self.camera()
        self.phase(f"restart{number}-return-A")

    def world_roundtrip(self):
        before = self.call("list_worlds")
        snapshot = self.call("snapshot_world_state", name="Disposable lifecycle evidence")
        restored = self.call("restore_world_state", snapshot_id=snapshot["snapshotId"])
        self.save("world-roundtrip.json", {"before": before, "snapshot": snapshot,
                                           "restore": restored, "after": self.call("list_worlds")})
        self.camera()
        self.phase("world-restored", 60)
        self.notes.append({"case": "world-roundtrip", "status": "REVIEW",
                           "reason": "Snapshot restoration does not establish distinct-world lifetime or disposal; undo can retain the old world."})

    def report(self):
        summary = dict(started_utc=self.started, session=self.args.session,
                       review_status="SCREENSHOT_AND_LOG_REVIEW_PENDING", phases=self.rows,
                       notes=self.notes, not_covered=["distinct-world lifetime/collection",
                       "simultaneous multiple views", "attached debugger and matched performance pairs",
                       "injected upload cancellation/failure", "in-place native buffer replacement",
                       "regression unit tests"])
        self.save("summary.json", summary)
        lines = ["# Vulkan lifecycle evidence", "", "Screenshot and log review pending. No phase closure is claimed.", "",
                 "| Check | Status | Presents | Native | Sets | Mesh variants | Pending | Errors |",
                 "| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |"]
        for row in self.rows:
            end = row["samples"][-1]
            lines.append(f"| {row['name']} | {row['status']} | {end['presents']} | {end['native']} | {end['sets']} | {end['mesh_variants']} | {end['pending']} | {end['validation'].get('errorCount', 'unknown')} |")
        lines += ["", "## Incomplete checks and collection notes", ""]
        lines += [f"- {note['case']}: {note['reason']}" for note in self.notes]
        lines += ["", "## Not covered", ""] + [f"- {item}" for item in summary["not_covered"]]
        (self.output / "README.md").write_text("\n".join(lines) + "\n", encoding="utf-8")

    def run(self):
        manifest = self.manifest()
        if manifest.get("state") != "Stopped":
            raise RuntimeError("Refusing to attach: named session must already be stopped.")
        self.save("session-before.json", manifest)
        self.owned = True
        try:
            manifest = self.manage("Start")
            binaries = Path(manifest["editorPath"]).parent
            hashes = {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in
                      (binaries / n for n in ("XREngine.Editor.dll", "XREngine.Runtime.Rendering.dll", "XREngine.Runtime.Rendering.Vulkan.dll"))}
            self.save("binary-hashes.json", hashes)
            self.endpoint = manifest["endpoint"]
            tools = self.rpc("tools/list")
            self.save("tool-catalog.json", tools)
            self.wait_for_world()
            self.camera()
            self.phase("warmup-A", 90)
            if self.args.cases == "mutations-world":
                self.attempt("scene-material-mutations", self.mutations)
                self.attempt("world-roundtrip", self.world_roundtrip)
                return
            self.phase("stationary-A", 60)
            for label, xyz in [("camera-B", (-15, 3, 2)), ("return-A", (-20, 2, 4)),
                               ("repeat-B", (-15, 3, 2)), ("unseen-C", (-10, 2, 4)),
                               ("return-C-A", (-20, 2, 4))]:
                self.camera(*xyz)
                self.phase(label)
            self.attempt("scene-material-mutations", self.mutations)
            def reload_shaders():
                self.call("reload_renderer_shaders")
                self.phase("shader-reload", 45)
            self.attempt("shader-reload", reload_shaders)
            for number in (1, 2):
                self.attempt(f"restart{number}", lambda n=number: self.restart(n))
            self.attempt("world-roundtrip", self.world_roundtrip)
        except Exception as exc:
            self.notes.append({"case": "collection", "status": "INCOMPLETE", "reason": str(exc)})
        finally:
            try:
                self.save("session-stopped.json", self.manage("Stop"))
            except Exception as exc:
                self.notes.append({"case": "session-stop", "status": "INCOMPLETE", "reason": str(exc)})
            root = Path(self.manifest()["sessionRoot"])
            if (root / "build.log").is_file():
                shutil.copy2(root / "build.log", self.output / "logs/build.log")
            inventory = []
            for folder in (root / "logs",):
                for path in folder.rglob("*"):
                    if path.is_file():
                        target = self.output / "logs" / path.relative_to(root)
                        target.parent.mkdir(parents=True, exist_ok=True)
                        shutil.copy2(path, target)
                        inventory.append(str(target.relative_to(self.output)))
            self.save("log-inventory.json", {"copied": inventory,
                       "note": "Release may omit textual logs; raw profiler replies contain cumulative validation messages."})
            self.report()
        print(f"Evidence ready: {self.output / 'README.md'}", flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--session", required=True, help="Existing stopped isolated session to reuse")
    parser.add_argument("--environment", required=True, help="Named-session environment JSON")
    parser.add_argument("--output", required=True, help="New directory beneath Build/_AgentValidation")
    parser.add_argument("--no-build", action="store_true", help="Reuse verified existing session binaries")
    parser.add_argument("--cases", choices=("full", "mutations-world"), default="full",
                        help="Collect the full sequence or only scene/material/world cases")
    Collector(parser.parse_args()).run()

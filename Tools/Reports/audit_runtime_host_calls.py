#!/usr/bin/env python3
"""Report lexical host-call matches from an immutable portable-closure revision.

The report preserves historical regular expressions and counts. A match does
not prove that a call is reachable from a browser runtime.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from pathlib import PurePosixPath
from pathlib import Path
from typing import Any


EXPRESSIONS = {
    "blocking_waits": r"\.Wait\(|\.WaitOne\(|\.Result\b|GetAwaiter\(\)\.GetResult\(\)|Thread\.Sleep|\.WaitAll\(|\.WaitAny\(|\.Join\(",
    "thread_creation": r"new Thread\(",
    "task_parallel_pool": r"Task\.Run\(|Parallel\.(For|ForEach|Invoke)|ThreadPool\.",
    "direct_file": r"\b(File|Directory)\.[A-Za-z_][A-Za-z0-9_]*|\bnew[[:space:]]+(FileStream|FileInfo|DirectoryInfo)[[:space:]]*\(",
}

REPO_ROOT = Path(__file__).resolve().parents[2]
MAX_GIT_ARGUMENT_CHARS = 12_000


def git(*args: str) -> bytes:
    """Run Git without a shell and return its byte output."""
    return subprocess.run(
        ["git", *args], cwd=REPO_ROOT, check=True,
        stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    ).stdout


def resolve_revision(revision: str) -> str:
    return git("rev-parse", "--verify", "--end-of-options", f"{revision}^{{commit}}").decode().strip()


def tracked_sources(commit: str) -> tuple[list[str], list[str]]:
    roots_data = git("show", f"{commit}:Build/Portable/PortableProjects.tsv")
    roots = [
        line.strip()
        for line in roots_data.decode("utf-8").splitlines()
        if line.strip() and not line.lstrip().startswith("#")
    ]
    paths = git("ls-tree", "-r", "-z", "--name-only", commit).decode(
        "utf-8", errors="surrogateescape"
    ).split("\0")
    sources: list[str] = []
    for path in paths:
        if not path or not path.endswith(".cs"):
            continue
        parts = PurePosixPath(path).parts
        if not parts or parts[0] not in roots:
            continue
        sources.append(path)
    return roots, sorted(sources)


def matches(commit: str, expression: str, paths: list[str]) -> list[dict[str, Any]]:
    if not paths:
        return []
    found: list[dict[str, Any]] = []
    for batch in path_batches(paths):
        result = subprocess.run(
            ["git", "grep", "--no-color", "-n", "-z", "-E", expression, commit, "--", *batch],
            cwd=REPO_ROOT,
            check=False,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
        )
        if result.returncode not in (0, 1):
            raise RuntimeError(result.stderr.decode("utf-8", errors="replace"))
        found.extend(parse_grep_output(result.stdout, commit))
    return sorted(found, key=lambda item: (item["path"], item["line"], item["text"]))


def path_batches(paths: list[str]) -> list[list[str]]:
    """Keep each Git command below a conservative Windows argument limit."""
    batches: list[list[str]] = []
    batch: list[str] = []
    size = 0
    for path in paths:
        path_size = len(os.fsencode(path)) + 3
        if batch and size + path_size > MAX_GIT_ARGUMENT_CHARS:
            batches.append(batch)
            batch = []
            size = 0
        batch.append(path)
        size += path_size
    if batch:
        batches.append(batch)
    return batches


def parse_grep_output(output: bytes, commit: str) -> list[dict[str, Any]]:
    """Parse `git grep -z -n` records without treating path colons as separators."""
    records: list[dict[str, Any]] = []
    prefix = f"{commit}:".encode("ascii")
    offset = 0
    while offset < len(output):
        path_end = output.find(b"\0", offset)
        line_end = output.find(b"\0", path_end + 1)
        text_end = output.find(b"\n", line_end + 1)
        if min(path_end, line_end, text_end) < 0:
            raise ValueError("Unexpected NUL-delimited git grep output")
        path_field = output[offset:path_end]
        if not path_field.startswith(prefix):
            raise ValueError("Git grep returned a path outside the requested revision")
        path = path_field[len(prefix):].decode("utf-8", errors="surrogateescape")
        line_field = output[path_end + 1:line_end]
        text_field = output[line_end + 1:text_end]
        records.append({
            "path": path,
            "line": int(line_field.decode("ascii")),
            "text": text_field.decode("utf-8", errors="surrogateescape"),
        })
        offset = text_end + 1
    return records


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--revision", default="HEAD", help="Git commit, tag, or ref (default: HEAD)")
    parser.add_argument("--output", help="Write JSON to this path; stdout is the default")
    args = parser.parse_args()

    commit = resolve_revision(args.revision)
    roots, paths = tracked_sources(commit)
    categories: dict[str, Any] = {}
    for name, expression in EXPRESSIONS.items():
        category_paths = paths
        if name == "direct_file":
            # This historical inventory was scoped to these four host-facing roots.
            direct_roots = {
                "XREngine.Runtime.Core",
                "XREngine.Runtime.Rendering",
                "XREngine.Runtime.Host",
                "XREngine.Data",
            }
            category_paths = [path for path in paths if path.split("/", 1)[0] in direct_roots]
        records = matches(commit, expression, category_paths)
        by_root: dict[str, dict[str, int]] = {
            root: {"matched_files": 0, "matching_lines": 0} for root in roots
        }
        files_by_root: dict[str, set[str]] = {root: set() for root in roots}
        for record in records:
            root = record["path"].split("/", 1)[0]
            by_root[root]["matching_lines"] += 1
            files_by_root[root].add(record["path"])
        for root in roots:
            by_root[root]["matched_files"] = len(files_by_root[root])
        categories[name] = {
            "expression": expression,
            "total": {"matched_files": len({r["path"] for r in records}), "matching_lines": len(records)},
            "by_root": by_root,
            "matches": records,
        }

    blocking_matches = categories["blocking_waits"]["matches"]
    asset_getawaiter = [
        record for record in blocking_matches
        if record["path"].startswith("XREngine.Runtime.Core/Assets/")
        and "GetAwaiter().GetResult()" in record["text"]
    ]
    report = {
        "revision": commit,
        "expressions": EXPRESSIONS,
        "roots": roots,
        "source_path_count": len(paths),
        "categories": categories,
        "core_asset_getawaiter_sync_wrapper_subtotal": {
            "matched_files": len({record["path"] for record in asset_getawaiter}),
            "matching_lines": len(asset_getawaiter),
            "matches": asset_getawaiter,
        },
    }
    encoded = json.dumps(report, indent=2, ensure_ascii=False) + "\n"
    if args.output:
        with open(args.output, "w", encoding="utf-8", newline="\n") as stream:
            stream.write(encoded)
    else:
        sys.stdout.write(encoded)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

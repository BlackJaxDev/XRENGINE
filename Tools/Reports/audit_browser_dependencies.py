#!/usr/bin/env python3
"""Inventory source-declared MSBuild dependencies for browser-port planning.

This is a syntactic, conditional union of declarations, not MSBuild evaluation,
NuGet restore, runtime reachability analysis, or a browser compatibility verdict.
"""

import argparse
import hashlib
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


DEFAULT_ROOTS = (
    "XREngine.Runtime.Core/XREngine.Runtime.Core.csproj",
    "XREngine.Runtime.Rendering/XREngine.Runtime.Rendering.csproj",
    "XREngine.Runtime.Bootstrap/XREngine.Runtime.Bootstrap.csproj",
)
COPY_METADATA = ("CopyToOutputDirectory", "CopyToPublishDirectory", "Link", "TargetPath")
EXPRESSION = re.compile(r"\$\(|@\(|%\(|[*?]")


def tag(element):
    return element.tag.rsplit("}", 1)[-1]


def relative(path, repo):
    """Return a stable repository path; never expose machine-local paths."""
    try:
        return path.resolve().relative_to(repo).as_posix()
    except ValueError:
        return None


def conditions(element, parents):
    condition = element.get("Condition")
    return parents + ([condition] if condition else [])


def walk(element, parents=()):
    current = conditions(element, list(parents))
    yield element, current
    for child in element:
        yield from walk(child, current)


def value(element, name):
    attribute = element.get(name)
    if attribute:
        return attribute
    for child in element:
        if tag(child) == name:
            return (child.text or "").strip() or None
    return None


def declaration(element, conditions_list, *attributes):
    result = {key: val for key in attributes if (val := value(element, key)) is not None}
    result["conditions"] = conditions_list
    return result


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def source_revision(repo):
    try:
        result = subprocess.run(
            ["git", "-C", str(repo), "rev-parse", "HEAD"],
            capture_output=True, text=True, check=True, timeout=5,
        )
        return result.stdout.strip() or None
    except (OSError, subprocess.CalledProcessError, subprocess.TimeoutExpired):
        return None


def inventory(repo, roots):
    projects = {}
    build_files = {}
    unknowns = []
    pending = list(roots)
    seen_projects = set()
    seen_imports = set()

    def note(kind, owner, declaration_text):
        unknowns.append({"kind": kind, "owner": owner, "declaration": declaration_text})

    def inspect(path, project=False):
        owner = relative(path, repo)
        if owner is None:
            note("path_outside_repository", "<repository>", str(path.name))
            return None
        if not path.is_file():
            note("missing_project" if project else "missing_import", owner, owner)
            return None
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as error:
            raise ValueError(f"malformed XML in {owner}: {error}") from error
        data = {
            "sha256": sha256(path), "target_frameworks": [], "project_references": [],
            "package_references": [], "imports": [], "targets": [], "output_copy_items": [],
        }
        for element, cond in walk(root):
            name = tag(element)
            if name in ("TargetFramework", "TargetFrameworks"):
                data["target_frameworks"].append({"property": name, "value": (element.text or "").strip(), "conditions": cond})
            elif name == "ProjectReference":
                entry = declaration(element, cond, "Include", "Update", "Remove", "ReferenceOutputAssembly", "OutputItemType")
                include = entry.get("Include")
                if include:
                    resolved = []
                    for part in include.split(";"):
                        part = part.strip()
                        if not part:
                            continue
                        if EXPRESSION.search(part):
                            note("unexpanded_project_reference", owner, part)
                            continue
                        destination = (path.parent / part.replace("\\", "/")).resolve()
                        target = relative(destination, repo)
                        if target is None:
                            note("path_outside_repository", owner, part)
                        else:
                            resolved.append(target)
                            pending.append(destination)
                    entry["resolved_paths"] = resolved
                data["project_references"].append(entry)
            elif name == "PackageReference":
                data["package_references"].append(declaration(element, cond, "Include", "Update", "Remove", "Version", "PrivateAssets", "IncludeAssets", "ExcludeAssets"))
            elif name == "Import":
                entry = declaration(element, cond, "Project", "Sdk")
                source = entry.get("Project")
                if source:
                    if EXPRESSION.search(source):
                        note("unexpanded_import", owner, source)
                    else:
                        destination = (path.parent / source.replace("\\", "/")).resolve()
                        entry["resolved_path"] = relative(destination, repo)
                        if entry["resolved_path"] is None:
                            note("path_outside_repository", owner, source)
                        elif destination not in seen_imports:
                            seen_imports.add(destination)
                            imported = inspect(destination)
                            if imported is not None:
                                build_files[entry["resolved_path"]] = imported
                data["imports"].append(entry)
            elif name == "Target":
                entry = declaration(element, cond, "Name", "BeforeTargets", "AfterTargets", "DependsOnTargets", "Inputs", "Outputs")
                entry["actions"] = [
                    {"task": tag(child), "attributes": dict(sorted(child.attrib.items()))}
                    for child in element if tag(child) not in ("PropertyGroup", "ItemGroup", "OnError")
                ]
                data["targets"].append(entry)
            elif name in ("None", "Content", "NativeCopyLocalItems"):
                metadata = {key: value(element, key) for key in COPY_METADATA}
                if any(metadata.get(key) for key in COPY_METADATA[:2]):
                    data["output_copy_items"].append({"item_type": name, **declaration(element, cond, "Include", "Update", "Remove"),
                                                      "metadata": {key: val for key, val in metadata.items() if val}})
        return data

    for name in ("Directory.Build.props", "Directory.Build.targets"):
        path = repo / name
        result = inspect(path)
        if result is not None:
            build_files[name] = result

    while pending:
        path = pending.pop(0)
        key = relative(path, repo)
        if key is None or key in seen_projects:
            continue
        seen_projects.add(key)
        result = inspect(path, project=True)
        if result is not None:
            projects[key] = result

    return {
        "scope": "source-declared conditional union; not an evaluated or reachable browser dependency graph",
        "source_revision": source_revision(repo),
        "roots": sorted(relative(path, repo) for path in roots),
        "projects": dict(sorted(projects.items())),
        "shared_build_files": dict(sorted(build_files.items())),
        "unknowns": sorted(unknowns, key=lambda item: (item["kind"], item["owner"], item["declaration"])),
        "limitations": [
            "Conditions are retained as text; no MSBuild properties, conditions, imports, SDK defaults, or item transforms are evaluated.",
            "Project references are a union of literal declarations, including mutually exclusive configurations.",
            "Package references are direct declarations only; NuGet transitive dependencies and resolved versions are unknown.",
            "Missing submodules and nonliteral project/import paths are reported in unknowns; dynamic target-generated references and outputs are not resolved.",
            "Only repository-root Directory.Build.props and Directory.Build.targets are inspected automatically; nearer or nested Directory.Build files are not discovered.",
            "Output copy items identify source declarations, not actual native binaries, publish contents, or browser reachability.",
        ],
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo", type=Path, default=Path(__file__).resolve().parents[2], help="repository root")
    parser.add_argument("--root", action="append", help="project path relative to repository (repeatable)")
    parser.add_argument("--output", type=Path, help="write the same JSON to this file as well as stdout")
    args = parser.parse_args()
    repo = args.repo.resolve()
    if not repo.is_dir():
        parser.error("--repo must be an existing directory")
    roots = []
    for item in args.root or DEFAULT_ROOTS:
        path = (repo / item).resolve()
        if relative(path, repo) is None or not path.is_file():
            parser.error(f"root project must exist inside repository: {item}")
        roots.append(path)
    try:
        result = inventory(repo, roots)
    except ValueError as error:
        parser.error(str(error))
    rendered = json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    if args.output:
        args.output.write_text(rendered, encoding="utf-8")
    sys.stdout.write(rendered)


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Inventory declared or evaluated browser dependencies and guard selected source APIs.

Evaluation consumes an existing restore; no mode certifies runtime reachability
or browser compatibility. The source guard is lexical, not a semantic analyzer.
"""

import argparse
import hashlib
import json
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from pathlib import Path


# These identifiers are checked against the evaluated Compile items. The guard
# masks comments and string/character literals before matching; it is deliberately
# conservative about aliases and simple names, but is not a semantic analyzer.
FORBIDDEN = {
    "native interop": r"\b(?:DllImport|LibraryImport|NativeLibrary|Marshal\.GetDelegateForFunctionPointer)\b",
    "desktop UI or graphics": r"\b(?:System\.Windows|System\.Drawing|Microsoft\.Win32|Silk\.NET\.(?:GLFW|SDL|OpenGL|Direct3D|OpenXR|Windowing)|ImGuiNET|UltralightNet|SkiaSharp)\b",
    "native dependency": r"\b(?:FFmpeg|NAudio|OpenAL|DirectStorage|MagicPhysX|JoltPhysics|CUDA|CoACD|RiveSharp)\b",
    "runtime code or assembly loading": r"\b(?:Assembly\.(?:Load|LoadFrom|LoadFile|LoadWithPartialName)|AssemblyLoadContext|Reflection\.Emit|DynamicMethod|Expression\.Compile|Activator\.CreateInstanceFrom)\b",
    "process or desktop services": r"\b(?:Process\.Start|Environment\.GetFolderPath|Registry(?:Key)?\.|System\.Management)\b",
}
REVIEWED_REFLECTION = {
    "XREngine.Browser/BrowserSceneEnvelope.cs": {
        "GetProperty": "JsonElement wire-field access, not System.Type reflection",
    },
    # The selected interpreter retains these managed lookup helpers. Their
    # metadata preservation and serializer behavior need separate trim/AOT proof.
    "XREngine.Data/Core/Events/XRPersistentCall.cs": {
        "Type.GetType": "serialized event parameter identity resolution",
        "GetMethods": "serialized event target overload resolution",
    },
    "XREngine.Data/Core/Reflection/XRLoadableTypeCatalog.cs": {
        "GetTypes": "cached loaded-assembly type inspection",
        "GetExportedTypes": "cached public type inspection",
    },
    "XREngine.Data/Core/Objects/XRBase.cs": {
        "GetMethod": "MemoryPack clone helper lookup",
        "MakeGenericMethod": "closed clone helper specialization in interpreter",
    },
    "XREngine.Data/Serialization/AotRuntimeMetadataStore.cs": {
        "Type.GetType": "persisted type identity resolver in interpreter",
        "GetAssemblies": "loaded-assembly fallback in untrimmed interpreter",
    },
    "XREngine.Extensions/Enum.cs": {
        "GetCustomAttributes": "enum display metadata lookup",
    },
    "XREngine.Extensions/Object.cs": {
        "GetMethod": "managed private method helper; browser composition does not call it",
    },
    "XREngine.Extensions/Reflection/Type.cs": {
        "Activator.CreateInstance": "explicit managed instance helper; not used by browser composition",
        "GetConstructor": "managed type constraint check",
        "GetCustomAttribute": "attribute helper; untrimmed metadata only",
        "GetCustomAttributes": "attribute helper; untrimmed metadata only",
    },
    "XREngine.Runtime.Core/Attributes/RequiresTransformAttribute.cs": {
        "GetCustomAttribute": "component transform requirement attribute",
        "Activator.CreateInstance": "fallback transform construction if explicit registry lacks type",
    },
    "XREngine.Runtime.Core/Scene/Components/XRComponent.cs": {
        "GetConstructor": "legacy component constructor invocation in interpreter",
        "GetUninitializedObject": "legacy component factory; requires browser lifecycle exercise",
    },
    "XREngine.Runtime.Core/Scene/SceneNode.Components.cs": {
        "GetCustomAttributes": "component requirement attributes",
    },
    "XREngine.Runtime.Core/Scene/Transforms/TransformBase.cs": {
        "GetCustomAttribute": "transform display metadata",
    },
    "XREngine.Runtime.Core/World/XRWorldObjectBase.cs": {
        "GetAssemblies": "untrimmed replication metadata scan from static initializer",
        "GetTypes": "loaded-assembly replication candidate inspection",
        "GetProperties": "replicable property discovery",
        "GetProperty": "registered replication property resolution",
        "GetCustomAttribute": "replication attribute inspection",
    },
    "XREngine.Runtime.Rendering/Runtime/RendererModules/RendererBackendModuleIdentity.cs": {
        "GetCustomAttribute": "target framework identity of registered module assembly",
    },
}
REFLECTION = re.compile(r"\b(?:Type\.GetType|GetTypes|GetExportedTypes|GetMethods|GetMethod|GetConstructor|GetCustomAttributes|GetCustomAttribute|GetProperties|GetProperty|GetAssemblies|GetUninitializedObject|Activator\.CreateInstance|MakeGenericType|MakeGenericMethod)\b")


def code_without_literals(source):
    """Preserve line positions while removing C# comments and literal bodies."""
    pattern = re.compile(
        r'//[^\n]*|/\*[\s\S]*?\*/|\$?@?"(?:""|\\.|[^"])*"|\$?"""[\s\S]*?"""|\x27(?:\\.|[^\x27\\])*\x27'
    )
    return pattern.sub(lambda match: re.sub(r"[^\n]", " ", match.group()), source)


def inspect_sources(repo, sources):
    findings = []
    for source in sorted(set(sources)):
        path = Path(source).resolve()
        name = relative(path, repo)
        if name is None or not path.is_file():
            raise ValueError(f"evaluated Compile item is absent or outside repository: {source}")
        code = code_without_literals(path.read_text(encoding="utf-8-sig"))
        for kind, expression in FORBIDDEN.items():
            for match in re.finditer(expression, code):
                findings.append({"file": name, "line": code.count("\n", 0, match.start()) + 1,
                                 "kind": kind, "symbol": match.group(), "decision": "reject"})
        for match in REFLECTION.finditer(code):
            reason = REVIEWED_REFLECTION.get(name, {}).get(match.group())
            findings.append({"file": name, "line": code.count("\n", 0, match.start()) + 1,
                             "kind": "reflection", "symbol": match.group(),
                             "decision": "reviewed" if reason else "review required",
                             "reason": reason})
        for match in re.finditer(r"\[\s*ModuleInitializer\s*\]|\bstatic\s+(?:readonly\s+)?[A-Za-z_][A-Za-z_0-9<>?, ]*\s*(?:\(|=)", code):
            findings.append({"file": name, "line": code.count("\n", 0, match.start()) + 1,
                             "kind": "initializer", "symbol": match.group(), "decision": "inspect"})
    return findings


def evaluated(repo, root, dotnet, configuration):
    """Follow evaluated ProjectReference items and consume each restore assets file."""
    projects = {}
    pending = [root.resolve()]
    while pending:
        project = pending.pop()
        name = relative(project, repo)
        if name is None or not project.is_file():
            raise ValueError(f"evaluated project is absent or outside repository: {project.name}")
        if name in projects:
            continue
        command = [dotnet, "msbuild", str(project), "-nologo",
                   "-p:XREnginePortableRuntime=true",
                   "-p:Configuration=" + configuration,
                   "-getProperty:TargetFramework,RuntimeIdentifier,ProjectAssetsFile,MSBuildProjectFullPath,MSBuildAllProjects",
                   "-getItem:ProjectReference,PackageReference,Compile,Content,None,Reference,NativeCopyLocalItems,ReferenceCopyLocalPaths,RuntimeTargetsCopyLocalItems"]
        result = subprocess.run(command, capture_output=True, text=True, timeout=120)
        if result.returncode:
            raise ValueError(f"MSBuild evaluation failed for {name}: {result.stderr.strip() or result.stdout.strip()}")
        data = json.loads(result.stdout)
        properties = data["Properties"]
        items = data["Items"]
        references = []
        for item in items.get("ProjectReference", []):
            destination = Path(item.get("FullPath") or (project.parent / item["Identity"])).resolve()
            target = relative(destination, repo)
            if target is None:
                raise ValueError(f"project reference outside repository in {name}: {item['Identity']}")
            references.append({"project": target, "metadata": item})
            pending.append(destination)
        assets_file = Path(properties["ProjectAssetsFile"])
        if not assets_file.is_absolute():
            assets_file = (project.parent / assets_file).resolve()
        if not assets_file.is_file():
            raise ValueError(f"restore assets absent for {name}; restore the portable browser graph first")
        assets = json.loads(assets_file.read_text(encoding="utf-8"))
        target_name = next((key for key in assets.get("targets", {})
                            if key.startswith(properties["TargetFramework"] + "/")
                            and (not properties["RuntimeIdentifier"] or key.endswith("/" + properties["RuntimeIdentifier"]))), None)
        if target_name is None:
            target_name = properties["TargetFramework"]
        if target_name not in assets.get("targets", {}):
            raise ValueError(f"restored target absent for {name}: {target_name}")
        packages = []
        for identity, target in sorted(assets["targets"][target_name].items()):
            if target.get("type") != "package":
                continue
            resolved_assets = {key: target[key]
                      for key in ("compile", "runtime", "native", "runtimeTargets", "build", "buildTransitive", "buildMultiTargeting", "contentFiles", "resource", "frameworkAssemblies", "frameworkReferences")
                      if target.get(key)}
            packages.append({"identity": identity, "dependencies": target.get("dependencies", {}),
                             "assets": resolved_assets,
                             "disposition": "portable managed, runtime metadata review pending"
                             if identity.split("/")[0] in ("MemoryPack", "YamlDotNet")
                             else "transitive review required",
                             "owner": "Data serialization" if identity.split("/")[0] in
                             ("MemoryPack", "YamlDotNet") else "portable dependency maintainer"})
        source_paths = [item.get("FullPath") or str((project.parent / item["Identity"]).resolve())
                        for item in items.get("Compile", [])]
        findings = inspect_sources(repo, source_paths)
        projects[name] = {
            "properties": properties, "project_references": references,
            "package_references": items.get("PackageReference", []),
            "copy_items": {key: items.get(key, []) for key in ("Content", "None", "Reference")},
            "compile": sorted(relative(Path(path), repo) for path in source_paths),
            "resolved_packages": packages,
            "resolved_native_copy": {key: items.get(key, []) for key in
                                     ("NativeCopyLocalItems", "ReferenceCopyLocalPaths", "RuntimeTargetsCopyLocalItems")},
            "source_findings": findings,
        }
    return {
        "scope": "evaluated portable project closure and restored NuGet assets; not runtime execution or publish output",
        "source_revision": source_revision(repo), "root": relative(root, repo),
        "configuration": configuration,
        "projects": dict(sorted(projects.items())),
        "excluded": {"XREngine.Audio": "desktop/native audio implementation; no browser project reference"},
        "limitations": [
            "MSBuild evaluation and project.assets.json represent the selected configuration; generated target items and publish output can differ.",
            "Source scanning is lexical and cannot resolve aliases, indirect calls or dynamic service registration.",
            "Initializer findings identify candidates; method reachability and execution order require runtime inspection.",
            "Trimmed and AOT publish modes are deliberately unsupported pending separate metadata and serialization qualification.",
        ],
    }


def guard(repo, sources_file):
    lines = sources_file.read_text(encoding="utf-8-sig").splitlines()
    if not lines:
        raise ValueError("portable Compile source list is empty")
    findings = inspect_sources(repo, lines)
    rejected = [item for item in findings if item["decision"] in ("reject", "review required")]
    for item in rejected:
        print(f"{item['file']}:{item['line']}: portable {item['kind']}: {item['symbol']}", file=sys.stderr)
    return bool(rejected)


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
    parser.add_argument("--mode", choices=("declared", "evaluated", "guard"), default="declared")
    parser.add_argument("--dotnet", default="dotnet", help="dotnet executable for evaluated mode")
    parser.add_argument("--configuration", default="Release", choices=("Debug", "Release"),
                        help="restored build configuration for evaluated mode")
    parser.add_argument("--sources-file", type=Path, help="evaluated Compile item paths, one per line, for guard mode")
    args = parser.parse_args()
    repo = args.repo.resolve()
    if not repo.is_dir():
        parser.error("--repo must be an existing directory")
    if args.mode == "guard":
        if not args.sources_file or not args.sources_file.is_file():
            parser.error("guard mode requires --sources-file")
        try:
            rejected = guard(repo, args.sources_file)
        except (ValueError, OSError, UnicodeError) as error:
            parser.error(str(error))
        return 1 if rejected else 0
    roots = []
    default_roots = ("XREngine.Browser/XREngine.Browser.csproj",) if args.mode == "evaluated" else DEFAULT_ROOTS
    for item in args.root or default_roots:
        path = (repo / item).resolve()
        if relative(path, repo) is None or not path.is_file():
            parser.error(f"root project must exist inside repository: {item}")
        roots.append(path)
    try:
        if args.mode == "evaluated":
            if len(roots) != 1:
                parser.error("evaluated mode requires exactly one root project")
            result = evaluated(repo, roots[0], args.dotnet, args.configuration)
        else:
            result = inventory(repo, roots)
    except (ValueError, OSError, subprocess.TimeoutExpired, json.JSONDecodeError) as error:
        parser.error(str(error))
    rendered = json.dumps(result, indent=2, sort_keys=True, ensure_ascii=False) + "\n"
    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(rendered, encoding="utf-8")
    sys.stdout.write(rendered)


if __name__ == "__main__":
    sys.exit(main())

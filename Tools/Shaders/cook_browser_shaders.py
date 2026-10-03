#!/usr/bin/env python3
"""Package explicit WGSL and its binding contract into content-addressed browser assets.

This is a deterministic packager, not a GLSL/Slang compiler or WGSL validator.
Browser shader compilation remains authoritative for WGSL syntax and semantics.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
from pathlib import Path, PurePosixPath
import re
import sys
import tempfile

COMPILER_IDENTITY = "xrengine-wgsl-packager/1"
MAX_SOURCE_BYTES = 1024 * 1024
MAX_JSON_BYTES = 64 * 1024
MAX_ARTIFACTS = 16
LAYOUT = {
    "vertexStride": 20, "positionOffset": 0, "uvOffset": 12,
    "transformBytes": 64, "materialBytes": 16,
    "bindings": [
        {"group": 0, "binding": 0, "kind": "uniform", "visibility": "vertex", "bytes": 64, "dynamic": True},
        {"group": 1, "binding": 0, "kind": "uniform", "visibility": "fragment", "bytes": 16, "dynamic": False},
        {"group": 1, "binding": 1, "kind": "texture-2d-float", "visibility": "fragment"},
        {"group": 1, "binding": 2, "kind": "filtering-sampler", "visibility": "fragment"},
    ],
}
PIPELINE = {
    "topology": "triangle-list", "cullMode": "none", "depthFormat": "depth24plus",
    "depthWrite": True, "depthCompare": "less", "blend": False, "sampleCount": 1,
}
MINIMUM_LIMITS = {
    "maxVertexAttributes": 2, "maxBindGroups": 2, "maxBindingsPerBindGroup": 3,
    "maxUniformBufferBindingSize": 64, "maxDynamicUniformBuffersPerPipelineLayout": 1,
}
RECIPE_KEYS = {
    "schemaVersion", "name", "source", "sourceLanguage", "target", "entryPoints",
    "defines", "includes", "specialization", "requiredFeatures", "requiredLimits",
    "matrixLayout", "semanticSchemaIdentity", "layout", "pipeline",
}


def canonical(value: object) -> bytes:
    return (json.dumps(value, ensure_ascii=True, allow_nan=False, sort_keys=True,
                       separators=(",", ":")) + "\n").encode("utf-8")


def digest(content: bytes) -> str:
    return hashlib.sha256(content).hexdigest()


def unique_object(pairs: list[tuple[str, object]]) -> dict:
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError(f"duplicate JSON property '{key}'")
        result[key] = value
    return result


def read_bounded(path: Path, limit: int) -> bytes:
    with path.open("rb") as stream:
        data = stream.read(limit + 1)
    if len(data) > limit:
        raise ValueError(f"{path.name}: exceeds {limit}-byte limit")
    return data


def reject_constant(value: str) -> None:
    raise ValueError(f"nonfinite JSON value '{value}' is not supported")


def require(condition: bool, message: str) -> None:
    if not condition:
        raise ValueError(message)


def make_artifact(recipe_path: Path, source_root: Path) -> tuple[str, bytes, bytes]:
    recipe = json.loads(read_bounded(recipe_path, MAX_JSON_BYTES).decode("utf-8"),
                        object_pairs_hook=unique_object, parse_constant=reject_constant)
    require(type(recipe) is dict and set(recipe) == RECIPE_KEYS,
            "recipe properties must match the supported shader recipe schema")
    require(type(recipe["schemaVersion"]) is int and recipe["schemaVersion"] == 1,
            "unsupported recipe schemaVersion")
    name = recipe["name"]
    require(type(name) is str and re.fullmatch(r"[a-z][a-z0-9-]{0,63}", name) is not None,
            "name must be a lowercase shader identifier")
    require(recipe["sourceLanguage"] == "WGSL" and recipe["target"] == "WebGPUWgsl",
            f"{name}: only explicit WGSL targeting WebGPUWgsl is supported; use an approved offline compiler for other languages")
    require(recipe["entryPoints"] == {"vertex": "vertexMain", "fragment": "fragmentMain"},
            f"{name}: this material profile requires vertexMain and fragmentMain; other stages/entry points are unsupported")
    require(recipe["defines"] == [] and recipe["includes"] == [] and recipe["specialization"] == {},
            f"{name}: defines, includes and specialization are unsupported; package self-contained WGSL")
    require(recipe["matrixLayout"] == "column-major" and
            recipe["semanticSchemaIdentity"] == "xrengine.browser.mesh.v1",
            f"{name}: incompatible matrix or semantic schema")
    require(canonical(recipe["layout"]) == canonical(LAYOUT) and
            canonical(recipe["pipeline"]) == canonical(PIPELINE),
            f"{name}: unsupported binding, vertex, material or pipeline layout")
    features = recipe["requiredFeatures"]
    require(type(features) is list and len(features) <= 32 and
            all(type(feature) is str and re.fullmatch(r"[a-z][a-z0-9-]{0,63}", feature) for feature in features),
            f"{name}: invalid requiredFeatures")
    require(features == [], f"{name}: this material profile does not support optional WebGPU features")
    limits = recipe["requiredLimits"]
    require(type(limits) is dict and set(limits) == set(MINIMUM_LIMITS),
            f"{name}: requiredLimits must describe the supported layout limits")
    for key, minimum in MINIMUM_LIMITS.items():
        require(type(limits[key]) is int and minimum <= limits[key] <= 0x7fffffff,
                f"{name}: invalid minimum device limit '{key}' (at least {minimum})")

    source_name = recipe["source"]
    require(type(source_name) is str and 0 < len(source_name) <= 240 and "\\" not in source_name,
            f"{name}: source must be a relative POSIX path")
    require(re.fullmatch(r"(?:[A-Za-z0-9_-]+/)*[A-Za-z0-9_.-]+\.wgsl", source_name) is not None,
            f"{name}: unsupported source path characters")
    relative = PurePosixPath(source_name)
    require(not relative.is_absolute() and source_name == relative.as_posix() and
            all(part not in ("", ".", "..") for part in relative.parts) and relative.suffix == ".wgsl",
            f"{name}: source must be a normalized relative .wgsl path")
    source_path = (source_root / source_name).resolve(strict=True)
    require(source_path.is_relative_to(source_root), f"{name}: source escapes the source root")
    source = read_bounded(source_path, MAX_SOURCE_BYTES)
    decoded = source.decode("utf-8")
    require(bool(decoded.strip()) and "\x00" not in decoded and not decoded.startswith("\ufeff"),
            f"{name}: WGSL must be nonempty UTF-8 without BOM or NUL")
    # Canonical line endings keep source locations intact and identities independent of Git checkout settings.
    source = decoded.replace("\r\n", "\n").replace("\r", "\n").encode("utf-8")
    source_hash = digest(source)
    descriptor = {
        key: recipe[key] for key in (
            "schemaVersion", "name", "sourceLanguage", "target", "entryPoints", "defines",
            "specialization", "requiredLimits", "matrixLayout", "semanticSchemaIdentity", "layout", "pipeline"
        )
    }
    descriptor["requiredFeatures"] = sorted(features)
    descriptor["compilerIdentity"] = COMPILER_IDENTITY
    descriptor["source"] = {
        "path": source_name, "sha256": source_hash, "byteLength": len(source), "url": source_hash + ".wgsl",
    }
    descriptor["dependencies"] = [{"path": source_name, "sha256": source_hash}]
    encoded = canonical(descriptor)
    require(len(encoded) <= MAX_JSON_BYTES, f"{name}: descriptor exceeds the JSON byte limit")
    return name, encoded, source


def write_atomic(path: Path, data: bytes, immutable: bool = False) -> None:
    if path.exists():
        existing = read_bounded(path, max(len(data), MAX_SOURCE_BYTES))
        if existing == data:
            return
        require(not immutable, f"{path.name}: existing immutable asset differs; refusing overwrite")
    temporary = None
    try:
        with tempfile.NamedTemporaryFile(dir=path.parent, prefix=".shader-", delete=False) as stream:
            temporary = Path(stream.name)
            stream.write(data)
            stream.flush()
        os.replace(temporary, path)
    finally:
        if temporary is not None:
            temporary.unlink(missing_ok=True)


def main() -> int:
    repository = Path(__file__).resolve().parents[2]
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--recipe", type=Path, action="append",
                        help="Recipe JSON, repeat for multiple shader artifacts.")
    parser.add_argument("--source-root", type=Path, default=repository / "XREngine.Runtime.Rendering.WebGPU/Assets")
    parser.add_argument("--output", type=Path, default=repository / "XREngine.Runtime.Rendering.WebGPU/Assets/shaders")
    args = parser.parse_args()
    recipes = args.recipe or [repository / "XREngine.Runtime.Rendering.WebGPU/Shaders/browser-unlit.recipe.json"]
    try:
        require(0 < len(recipes) <= MAX_ARTIFACTS, "package requires 1–16 recipes")
        source_root = args.source_root.resolve(strict=True)
        prepared = []
        names = set()
        for recipe_path in recipes:
            try:
                name, descriptor, source = make_artifact(recipe_path, source_root)
            except (OSError, ValueError, TypeError, RecursionError) as error:
                raise ValueError(f"{recipe_path.name}: {error}") from error
            require(name not in names, f"duplicate shader artifact name '{name}'")
            names.add(name)
            prepared.append((name, descriptor, source))
        # Finish every recipe before publishing files; the manifest commits the complete package last.
        output = args.output.resolve()
        output.mkdir(parents=True, exist_ok=True)
        artifacts = []
        for name, descriptor, source in sorted(prepared):
            identity = digest(descriptor)
            descriptor_name = identity + ".shader.json"
            write_atomic(output / (digest(source) + ".wgsl"), source, immutable=True)
            write_atomic(output / descriptor_name, descriptor, immutable=True)
            artifacts.append({"name": name, "descriptor": descriptor_name, "sha256": identity})
        manifest = canonical({"schemaVersion": 1, "backend": "WebGPU", "packetVersion": 2, "artifacts": artifacts})
        require(len(manifest) <= MAX_JSON_BYTES, "manifest exceeds the JSON byte limit")
        write_atomic(output / "manifest.json", manifest)
        print(f"Packaged {len(artifacts)} WGSL artifact(s); compiler identity {COMPILER_IDENTITY}.")
        return 0
    except (OSError, ValueError, TypeError, RecursionError) as error:
        print(f"Shader cook failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

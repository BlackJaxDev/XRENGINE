"""Dump shader constant blocks and read-only resources at a frame event.

Run inside RenderDoc's Python shell or replay environment. RenderDoc supplies
the global `controller` and `rd` objects. Optional globals:

    EID = 1457
    SHADER_STAGE = rd.ShaderStage.Pixel
    DUMP_CONSTANTS = True
    DUMP_READ_ONLY_RESOURCES = True
    SAVE_TEXTURES = False
    OUT_DIR = "McpCaptures/rdc"
"""

import os

if "controller" not in globals() or "rd" not in globals():
    raise RuntimeError("Run this script inside RenderDoc so `controller` and `rd` are available.")

SAVE_TEXTURES = bool(globals().get("SAVE_TEXTURES", False))
DUMP_CONSTANTS = bool(globals().get("DUMP_CONSTANTS", True))
DUMP_READ_ONLY_RESOURCES = bool(globals().get("DUMP_READ_ONLY_RESOURCES", True))
OUT_DIR = str(globals().get("OUT_DIR", os.path.join("McpCaptures", "rdc")))


def _selected_event_id():
    if "EID" in globals():
        return EID
    if hasattr(controller, "GetFrameEvent"):
        return controller.GetFrameEvent()
    raise RuntimeError("Set EID before running this script.")


def _shader_stage(value):
    if isinstance(value, str):
        return getattr(rd.ShaderStage, value)
    return value


def _stage_name(stage):
    value = str(stage)
    return value.rsplit(".", 1)[-1]


def _resource_id_is_valid(resource_id):
    return resource_id != rd.ResourceId.Null()


def _format_name(value):
    if value is None:
        return ""
    if hasattr(value, "Name"):
        return value.Name()
    return str(value)


def _texture_map():
    return {str(texture.resourceId): texture for texture in controller.GetTextures()}


def _resource_name_map():
    return {str(resource.resourceId): resource.name for resource in controller.GetResources()}


def _safe_resource_id(resource_id):
    return str(resource_id).replace("ResourceId::", "").replace(":", "_").replace("/", "_")


def _trim_values(values, count):
    try:
        return list(values[:count])
    except Exception:
        return []


def _render_variable(variable):
    if len(variable.members) > 0:
        return {
            member.name if member.name else str(index): _render_variable(member)
            for index, member in enumerate(variable.members)
        }

    count = max(1, variable.rows * variable.columns)
    value = getattr(variable, "value", None)
    rendered = {
        "rows": variable.rows,
        "columns": variable.columns,
    }

    if value is None:
        return rendered

    for field_name in ("f32v", "s32v", "u32v", "u64v"):
        field = getattr(value, field_name, None)
        if field is None:
            continue
        field_values = _trim_values(field, count)
        if field_values:
            rendered[field_name] = field_values

    return rendered


def _dump_constant_blocks(pipe, stage):
    reflection = pipe.GetShaderReflection(stage)
    if reflection is None:
        return []

    entry_point = pipe.GetShaderEntryPoint(stage)
    shader_pipe = pipe.GetGraphicsPipelineObject()
    blocks = []

    for index, block in enumerate(reflection.constantBlocks):
        row = {
            "index": index,
            "name": block.name,
            "byteSize": block.byteSize,
            "variables": {},
        }

        try:
            bound = pipe.GetConstantBlock(stage, index, 0)
            descriptor = bound.descriptor
            row["resource"] = str(descriptor.resource)
            row["byteOffset"] = descriptor.byteOffset
            row["boundByteSize"] = descriptor.byteSize

            if _resource_id_is_valid(descriptor.resource):
                variables = controller.GetCBufferVariableContents(
                    shader_pipe,
                    reflection.resourceId,
                    stage,
                    entry_point,
                    index,
                    descriptor.resource,
                    descriptor.byteOffset,
                    descriptor.byteSize,
                )
                row["variables"] = {variable.name: _render_variable(variable) for variable in variables}
        except Exception as exc:
            row["error"] = str(exc)

        blocks.append(row)

    return blocks


def _save_texture(resource_id, event_id, stage_name, slot):
    os.makedirs(OUT_DIR, exist_ok=True)
    save = rd.TextureSave()
    save.resourceId = resource_id
    save.destType = rd.FileType.PNG
    save.mip = 0
    save.slice.sliceIndex = 0

    filename = f"eid{event_id}_{stage_name}_slot{slot}_{_safe_resource_id(resource_id)}.png"
    path = os.path.join(OUT_DIR, filename)
    controller.SaveTexture(save, path)
    return path


def _dump_read_only_resources(pipe, stage, event_id, stage_name, textures, resource_names):
    rows = []
    for slot, used in enumerate(pipe.GetReadOnlyResources(stage)):
        descriptor = used.descriptor
        if not _resource_id_is_valid(descriptor.resource):
            continue

        texture = textures.get(str(descriptor.resource))
        row = {
            "slot": slot,
            "resource": str(descriptor.resource),
            "view": str(descriptor.view),
            "name": resource_names.get(str(descriptor.resource), ""),
            "textureWidth": texture.width if texture is not None else 0,
            "textureHeight": texture.height if texture is not None else 0,
            "textureFormat": _format_name(texture.format) if texture is not None else "",
            "viewFormat": _format_name(getattr(descriptor, "format", None)),
            "firstMip": getattr(descriptor, "firstMip", ""),
            "numMips": getattr(descriptor, "numMips", ""),
            "firstSlice": getattr(descriptor, "firstSlice", ""),
            "swizzle": str(getattr(descriptor, "swizzle", "")),
        }

        if SAVE_TEXTURES and texture is not None:
            row["saved"] = _save_texture(descriptor.resource, event_id, stage_name, slot)

        rows.append(row)

    return rows


event_id = _selected_event_id()
stage = _shader_stage(globals().get("SHADER_STAGE", rd.ShaderStage.Pixel))
stage_name = _stage_name(stage)

controller.SetFrameEvent(event_id, True)
pipeline = controller.GetPipelineState()
textures = _texture_map()
resource_names = _resource_name_map()

result = {
    "eventId": event_id,
    "stage": stage_name,
}

if DUMP_CONSTANTS:
    result["constantBlocks"] = _dump_constant_blocks(pipeline, stage)

if DUMP_READ_ONLY_RESOURCES:
    result["readOnlyResources"] = _dump_read_only_resources(
        pipeline,
        stage,
        event_id,
        stage_name,
        textures,
        resource_names,
    )

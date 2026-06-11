"""Dump viewport, scissor, rasterizer, depth, target, and sampled-resource state.

Run inside RenderDoc's Python shell or replay environment. RenderDoc supplies
the global `controller` and `rd` objects. Optional globals:

    EID = 1457
    EIDS = [1457, 1473]
    SHADER_STAGE = rd.ShaderStage.Pixel
    INCLUDE_READ_ONLY_RESOURCES = True
"""

if "controller" not in globals() or "rd" not in globals():
    raise RuntimeError("Run this script inside RenderDoc so `controller` and `rd` are available.")

INCLUDE_READ_ONLY_RESOURCES = bool(globals().get("INCLUDE_READ_ONLY_RESOURCES", True))


def _selected_event_ids():
    if "EIDS" in globals():
        return list(EIDS)
    if "EID" in globals():
        return [EID]
    if hasattr(controller, "GetFrameEvent"):
        return [controller.GetFrameEvent()]
    raise RuntimeError("Set EID or EIDS before running this script.")


def _shader_stage(value):
    if isinstance(value, str):
        return getattr(rd.ShaderStage, value)
    return value


def _enum(value):
    return str(value)


def _resource_id_is_valid(resource_id):
    return resource_id != rd.ResourceId.Null()


def _texture_map():
    return {str(texture.resourceId): texture for texture in controller.GetTextures()}


def _resource_name_map():
    return {str(resource.resourceId): resource.name for resource in controller.GetResources()}


def _format_name(value):
    if value is None:
        return ""
    if hasattr(value, "Name"):
        return value.Name()
    return str(value)


def _output_targets(pipe, textures, resource_names):
    targets = []
    for index, target in enumerate(pipe.GetOutputTargets()):
        if not _resource_id_is_valid(target.resource):
            continue
        texture = textures.get(str(target.resource))
        targets.append(
            {
                "index": index,
                "resource": str(target.resource),
                "name": resource_names.get(str(target.resource), ""),
                "width": texture.width if texture is not None else 0,
                "height": texture.height if texture is not None else 0,
                "format": _format_name(texture.format) if texture is not None else "",
            }
        )

    if hasattr(pipe, "GetDepthTarget"):
        depth = pipe.GetDepthTarget()
        if depth is not None and hasattr(depth, "resource") and _resource_id_is_valid(depth.resource):
            texture = textures.get(str(depth.resource))
            targets.append(
                {
                    "index": "depth",
                    "resource": str(depth.resource),
                    "name": resource_names.get(str(depth.resource), ""),
                    "width": texture.width if texture is not None else 0,
                    "height": texture.height if texture is not None else 0,
                    "format": _format_name(texture.format) if texture is not None else "",
                }
            )

    return targets


def _viewports(vk):
    rows = []
    for index, viewport_scissor in enumerate(vk.viewportScissor.viewportScissors):
        viewport = viewport_scissor.vp
        scissor = viewport_scissor.scissor
        rows.append(
            {
                "index": index,
                "viewport": {
                    "x": viewport.x,
                    "y": viewport.y,
                    "width": viewport.width,
                    "height": viewport.height,
                    "minDepth": viewport.minDepth,
                    "maxDepth": viewport.maxDepth,
                },
                "scissor": {
                    "x": scissor.x,
                    "y": scissor.y,
                    "width": scissor.width,
                    "height": scissor.height,
                },
            }
        )
    return rows


def _read_only_resources(pipe, stage, textures, resource_names):
    rows = []
    for slot, used in enumerate(pipe.GetReadOnlyResources(stage)):
        descriptor = used.descriptor
        if not _resource_id_is_valid(descriptor.resource):
            continue

        texture = textures.get(str(descriptor.resource))
        rows.append(
            {
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
        )
    return rows


stage = _shader_stage(globals().get("SHADER_STAGE", rd.ShaderStage.Pixel))
textures = _texture_map()
resource_names = _resource_name_map()
out = []

for event_id in _selected_event_ids():
    controller.SetFrameEvent(event_id, True)
    pipe = controller.GetPipelineState()
    vk = controller.GetVulkanPipelineState()
    rasterizer = vk.rasterizer
    depth_stencil = vk.depthStencil

    entry = {
        "eventId": event_id,
        "targets": _output_targets(pipe, textures, resource_names),
        "viewports": _viewports(vk),
        "rasterizer": {
            "cullMode": _enum(rasterizer.cullMode),
            "frontCCW": rasterizer.frontCCW,
            "discard": rasterizer.rasterizerDiscardEnable,
            "depthClamp": rasterizer.depthClampEnable,
            "fillMode": _enum(rasterizer.fillMode),
        },
        "depthStencil": {
            "depthTest": depth_stencil.depthTestEnable,
            "depthWrite": depth_stencil.depthWriteEnable,
            "depthFunc": _enum(depth_stencil.depthFunction),
        },
    }

    if INCLUDE_READ_ONLY_RESOURCES:
        entry["readOnlyResources"] = _read_only_resources(pipe, stage, textures, resource_names)

    out.append(entry)

result = out

"""Audit viewport/scissor extents against active render-target size.

Run inside RenderDoc's Python shell or replay environment. RenderDoc supplies
the global `controller` and `rd` objects. Optional globals:

    INCLUDE_OK = True
    MAX_NAME = 80
"""

if "controller" not in globals() or "rd" not in globals():
    raise RuntimeError("Run this script inside RenderDoc so `controller` and `rd` are available.")

INCLUDE_OK = bool(globals().get("INCLUDE_OK", False))
MAX_NAME = int(globals().get("MAX_NAME", 80))


def _resource_id_is_valid(resource_id):
    return resource_id != rd.ResourceId.Null()


def _texture_map():
    return {str(texture.resourceId): texture for texture in controller.GetTextures()}


def _resource_name_map():
    return {str(resource.resourceId): resource.name for resource in controller.GetResources()}


def _active_target(pipe):
    for target in pipe.GetOutputTargets():
        if _resource_id_is_valid(target.resource):
            return "color", target.resource

    if hasattr(pipe, "GetDepthTarget"):
        depth = pipe.GetDepthTarget()
        if depth is not None and hasattr(depth, "resource") and _resource_id_is_valid(depth.resource):
            return "depth", depth.resource

    return "none", rd.ResourceId.Null()


def _range_status(start, end, extent):
    if extent <= 0:
        return "unknown"
    if start >= extent or end <= 0:
        return "offscreen"
    if start < 0 or end > extent:
        return "partial"
    return "ok"


def _combined_status(x_status, y_status):
    if x_status == "offscreen" or y_status == "offscreen":
        return "OFFSCREEN"
    if x_status == "partial" or y_status == "partial":
        return "PARTIAL"
    if x_status == "unknown" or y_status == "unknown":
        return "UNKNOWN"
    return "ok"


def _viewport_status(viewport, width, height):
    left = min(viewport.x, viewport.x + viewport.width)
    right = max(viewport.x, viewport.x + viewport.width)
    top = min(viewport.y, viewport.y + viewport.height)
    bottom = max(viewport.y, viewport.y + viewport.height)
    return _combined_status(_range_status(left, right, width), _range_status(top, bottom, height))


def _scissor_status(scissor, width, height):
    return _combined_status(
        _range_status(scissor.x, scissor.x + scissor.width, width),
        _range_status(scissor.y, scissor.y + scissor.height, height),
    )


def _action_name(action):
    try:
        return action.GetName(controller.GetStructuredFile())[:MAX_NAME]
    except Exception:
        return str(action.eventId)


textures = _texture_map()
resource_names = _resource_name_map()
rows = []
total_draws = 0
draws_with_viewports = 0


def walk(actions):
    global total_draws, draws_with_viewports

    for action in actions:
        if action.flags & rd.ActionFlags.Drawcall:
            total_draws += 1
            event_id = action.eventId
            controller.SetFrameEvent(event_id, True)

            vk = controller.GetVulkanPipelineState()
            viewport_scissors = vk.viewportScissor.viewportScissors
            if len(viewport_scissors) == 0:
                continue
            draws_with_viewports += 1

            pipe = controller.GetPipelineState()
            target_kind, target_id = _active_target(pipe)
            target_texture = textures.get(str(target_id))
            target_width = target_texture.width if target_texture is not None else 0
            target_height = target_texture.height if target_texture is not None else 0

            viewport_rows = []
            bad = False
            for index, viewport_scissor in enumerate(viewport_scissors):
                viewport = viewport_scissor.vp
                scissor = viewport_scissor.scissor
                viewport_state = _viewport_status(viewport, target_width, target_height)
                scissor_state = _scissor_status(scissor, target_width, target_height)
                if viewport_state != "ok" or scissor_state != "ok":
                    bad = True
                viewport_rows.append(
                    {
                        "index": index,
                        "viewport": {
                            "x": viewport.x,
                            "y": viewport.y,
                            "width": viewport.width,
                            "height": viewport.height,
                            "minDepth": viewport.minDepth,
                            "maxDepth": viewport.maxDepth,
                            "status": viewport_state,
                        },
                        "scissor": {
                            "x": scissor.x,
                            "y": scissor.y,
                            "width": scissor.width,
                            "height": scissor.height,
                            "status": scissor_state,
                        },
                    }
                )

            if bad or INCLUDE_OK:
                rows.append(
                    {
                        "eventId": event_id,
                        "name": _action_name(action),
                        "targetKind": target_kind,
                        "target": str(target_id),
                        "targetName": resource_names.get(str(target_id), ""),
                        "targetSize": f"{target_width}x{target_height}",
                        "viewports": viewport_rows,
                        "status": "bad" if bad else "ok",
                    }
                )

        walk(action.children)


walk(controller.GetRootActions())

bad_rows = [row for row in rows if row["status"] != "ok"]
result = {
    "bad": bad_rows,
    "rows": rows if INCLUDE_OK else bad_rows,
    "summary": {
        "totalDraws": total_draws,
        "drawsWithViewports": draws_with_viewports,
        "reportedRows": len(rows),
        "badRows": len(bad_rows),
        "includeOk": INCLUDE_OK,
    },
}

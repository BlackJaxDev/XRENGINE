# RenderDoc Tools

Small helpers for XRENGINE RenderDoc investigations. Keep one-off probes under
`Tools/TempInspect`; move scripts here once they are parameterized and broadly
useful.

## Trigger a Live Capture

`trigger_capture.py` connects to a RenderDoc target-control ident, triggers one
capture, and copies the `.rdc` file locally.

```powershell
$env:RENDERDOC_PYTHON_PATH = "C:\Program Files\RenderDoc\plugins\python"
python Tools/RenderDoc/trigger_capture.py --ident 38920 --output McpCaptures/rdc/frame.rdc
```

If `renderdoc.py` is already importable, `RENDERDOC_PYTHON_PATH` is optional.
The output directory is created automatically.

## Replay Scripts

Scripts under `Tools/RenderDoc/replay/` are intended to run inside RenderDoc's
Python shell or replay environment, where RenderDoc provides `controller` and
`rd`.

From the repository root, a typical RenderDoc Python shell call looks like:

```python
EID = 1457
exec(open(r"Tools\RenderDoc\replay\dump_pipeline_state.py").read())
result
```

Available replay helpers:

- `audit_viewports.py`: walks every draw and reports viewport/scissor extents
  that are outside the active color or depth target.
- `dump_pipeline_state.py`: dumps targets, viewports, scissors, rasterizer,
  depth/stencil, and sampled resources for `EID` or `EIDS`.
- `dump_shader_resources.py`: dumps shader constant blocks and read-only
  resources for an event. Set `SAVE_TEXTURES = True` to export bound textures
  as PNGs under `OUT_DIR`.

Common globals:

```python
EID = 1457
EIDS = [1457, 1473]
SHADER_STAGE = rd.ShaderStage.Pixel
OUT_DIR = r"McpCaptures\rdc"
```

# RenderDoc Tools

Small helpers for XRENGINE RenderDoc investigations. Keep one-off probes under
`Build/_AgentValidation/<run>/scratch`; move scripts here once they are
parameterized and broadly useful.

## Install RenderDoc And rdc-cli

Use the combined installer directly or select it from the `Deps` section of
`ExecTool.bat`:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File Tools/Dependencies/Install-RenderDoc.ps1
```

The installer:

- installs RenderDoc through the `BaldurKarlsson.RenderDoc` winget package when
  it is missing;
- installs the pinned, MIT-licensed `rdc-cli` 0.5.6 in an isolated `uv` tool
  environment;
- adds both command directories to the per-user `PATH`;
- bootstraps the RenderDoc replay Python module when needed; and
- finishes by running `rdc doctor`.

Open a new shell after the first installation so it inherits the updated
`PATH`. The installer invokes `rdc` by absolute path, so its own validation also
works from a shell that started before installation.

## Inspect A Capture With rdc-cli

Keep captures and exports under the active investigation run root:

```powershell
rdc doctor
rdc open Build/_AgentValidation/<run>/renderdoc/frame.rdc
rdc passes
rdc draws --limit 40
rdc rt <EID> -o Build/_AgentValidation/<run>/renderdoc/final-output.png
rdc close
```

## Trigger a Live Capture

`trigger_capture.py` connects to a RenderDoc target-control ident, triggers one
capture, and copies the `.rdc` file locally.

```powershell
$env:RENDERDOC_PYTHON_PATH = "$env:LOCALAPPDATA\rdc\renderdoc"
python Tools/RenderDoc/trigger_capture.py --ident 38920 --output Build/_AgentValidation/<run>/renderdoc/frame.rdc
```

`Install-RenderDoc.ps1` persists `RENDERDOC_PYTHON_PATH` when it finds the
bootstrapped module, so the explicit assignment is normally unnecessary. The
output directory is created automatically.

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
OUT_DIR = r"Build\_AgentValidation\<run>\renderdoc"
```

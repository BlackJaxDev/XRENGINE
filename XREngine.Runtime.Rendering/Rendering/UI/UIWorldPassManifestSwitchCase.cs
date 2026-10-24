using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering.UI;

internal readonly record struct UIWorldPassManifestSwitchCase(
    int Key, ViewportRenderCommandContainer Commands, int CommandCount);

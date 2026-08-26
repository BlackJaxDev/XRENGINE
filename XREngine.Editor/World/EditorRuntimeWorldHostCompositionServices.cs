using XREngine.Runtime.Bootstrap;

namespace XREngine.Editor;

/// <summary>Attaches editor scene policy before a host loads its target scenes.</summary>
internal sealed class EditorRuntimeWorldHostCompositionServices : IRuntimeWorldHostCompositionServices
{
    public void Compose(RuntimeWorldHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        EditorWorldIntegrationRegistry.GetOrAttach(host.CoreWorld).BindRenderer(host.RenderWorld);
    }
}

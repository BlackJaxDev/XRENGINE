namespace XREngine.Data.Plugins;

/// <summary>
/// An engine extension built outside the engine's assemblies. A plugin assembly names its implementation with
/// <see cref="XREnginePluginAttribute"/>; the host creates it with its public parameterless constructor.
/// </summary>
public interface IXREnginePlugin
{
    /// <summary>Registers the plugin's services. Disposing the result removes them.</summary>
    IDisposable Install(XREnginePluginContext context);
}

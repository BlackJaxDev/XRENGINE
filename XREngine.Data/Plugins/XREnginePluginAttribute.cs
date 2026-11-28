using System.Diagnostics.CodeAnalysis;

namespace XREngine.Data.Plugins;

/// <summary>Names an <see cref="IXREnginePlugin"/> implementation that the host installs from this assembly.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true, Inherited = false)]
public sealed class XREnginePluginAttribute(
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] Type pluginType) : Attribute
{
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    public Type PluginType { get; } = pluginType ?? throw new ArgumentNullException(nameof(pluginType));
}

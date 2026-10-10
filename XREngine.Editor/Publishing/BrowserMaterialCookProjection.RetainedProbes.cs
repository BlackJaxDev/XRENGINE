using System.Reflection;
using MemoryPack;
using XREngine.Components.Capture.Lights;
using XREngine.Data.Core;
using XREngine.Rendering;
using YamlDotNet.Serialization;

namespace XREngine.Editor.Publishing;

internal sealed partial class BrowserMaterialCookProjection
{
    private readonly Dictionary<LightProbeComponent, PublishedRetainedLightProbeComponent> _retainedProbeCopies = new(ReferenceEqualityComparer.Instance);

    private PublishedRetainedLightProbeComponent ProjectRetainedProbe(LightProbeComponent source)
    {
        if (_retainedProbeCopies.TryGetValue(source, out PublishedRetainedLightProbeComponent? existing)) return existing;
        if (source.GetType() != typeof(LightProbeComponent))
            throw new NotSupportedException($"BrowserCook.RetainedProbeTypeUnsupported: '{source.Name}' has custom component state that requires its own target projection.");
        RetainedLightProbeIblProfile profile = RetainedLightProbeIblProfile.Capture(source);
        profile = profile with { Irradiance = ProjectRetainedProbeImage(profile.SourceIrradiance), Prefilter = ProjectRetainedProbeImage(profile.SourcePrefilter) };
        using IDisposable wrappers = GenericRenderObject.EnterApiWrapperCreationSuppressionScope();
        using IDisposable cache = XRObjectBase.SuppressObjectCacheRegistration();
        PublishedRetainedLightProbeComponent copy = new();
        try
        {
            copy.InfluenceSphereInnerRadius = 0;
            copy.InfluenceSphereOuterRadius = source.InfluenceSphereOuterRadius;
            copy.InfluenceBoxOuterExtents = source.InfluenceBoxOuterExtents;
            PropertyInfo[] properties = typeof(LightProbeComponent).GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int referencePass = 0; referencePass < 2; referencePass++)
                foreach (PropertyInfo property in properties)
                {
                    if (!property.CanRead || !property.CanWrite || property.GetIndexParameters().Length != 0 ||
                        property.GetCustomAttribute<YamlIgnoreAttribute>() is not null ||
                        property.GetCustomAttribute<RuntimeOnlyAttribute>() is not null ||
                    property.PropertyType.GetCustomAttribute<RuntimeOnlyAttribute>(inherit: true) is not null ||
                        property.GetCustomAttribute<MemoryPackIgnoreAttribute>() is not null) continue;
                    bool reference = !property.PropertyType.IsValueType && property.PropertyType != typeof(string);
                    if (reference != (referencePass == 1)) continue;
                    property.SetValue(copy, property.GetValue(source));
                }
            copy.AdoptPersistentID(source.ID);
            copy.PublishedRetainedIbl = profile;
            _retainedProbeCopies.Add(source, copy);
            return copy;
        }
        catch
        {
            copy.Destroy(now: true);
            throw;
        }
    }

    private void ReleaseRetainedProbeCopies()
    {
        foreach (PublishedRetainedLightProbeComponent copy in _retainedProbeCopies.Values)
        {
            copy.PublishedRetainedIbl = null;
            copy.Destroy(now: true);
        }
        _retainedProbeCopies.Clear();
    }
}

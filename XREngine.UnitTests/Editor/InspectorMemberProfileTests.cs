using NUnit.Framework;
using XREngine.Components.Lights;
using XREngine.Editor;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks the member profiles that give native inspector sections the layout of the custom ImGui editors.</summary>
[TestFixture]
public sealed class InspectorMemberProfileTests
{
    [Test]
    public void DirectionalLightUsesTheImGuiLightSectionsAsGroups()
    {
        Type type = typeof(DirectionalLightComponent);
        Assert.That(InspectorMemberProfiles.GroupFor(type, "Color"), Is.EqualTo("Light"));
        Assert.That(InspectorMemberProfiles.GroupFor(type, "CastsShadows"), Is.EqualTo("Shadow Map"));
        Assert.That(InspectorMemberProfiles.GroupFor(type, "ShadowDepthBiasTexels"), Is.EqualTo("Bias"));
        Assert.That(InspectorMemberProfiles.GroupFor(type, "Scale"), Is.EqualTo("Directional Shadow Projection"));
        Assert.That(InspectorMemberProfiles.LabelFor(type, "ShadowDepthBiasTexels"), Is.EqualTo("Depth Bias"));
        Assert.That(InspectorMemberProfiles.GroupFor(type, "DiffuseIntensity"), Is.Null);
    }

    [Test]
    public void ProfileMembersExistOnTheirTypes()
    {
        // A renamed property would silently drop out of its section, so every profile name must resolve.
        foreach (string name in new[] { "Color", "DiffuseIntensity", "Type", "CastsShadows", "ShadowMapEncoding",
                     "EnableContactShadows", "ShadowDebugMode", "Scale", "CascadeCount", "CascadePercentages", "DebugCascadeColors" })
            Assert.That(typeof(DirectionalLightComponent).GetProperty(name), Is.Not.Null, name);
    }
}

using NUnit.Framework;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Editor;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks the picker list of the native inspector object field.</summary>
[TestFixture]
public sealed class InspectorObjectCandidateSourceTests
{
    [Test]
    public void ObjectFieldsCoverComponentNodeTransformAndAssetReferences()
    {
        Assert.That(InspectorObjectCandidateSource.IsObjectReferenceType(typeof(XRComponent)), Is.True);
        Assert.That(InspectorObjectCandidateSource.IsObjectReferenceType(typeof(SceneNode)), Is.True);
        Assert.That(InspectorObjectCandidateSource.IsObjectReferenceType(typeof(Transform)), Is.True);
        Assert.That(InspectorObjectCandidateSource.IsObjectReferenceType(typeof(XRAsset)), Is.True);
        Assert.That(InspectorObjectCandidateSource.IsObjectReferenceType(typeof(string)), Is.False);
        Assert.That(InspectorObjectCandidateSource.IsObjectReferenceType(typeof(System.Numerics.Vector3)), Is.False);
    }

    [Test]
    public void NullableReferenceListsNoneFirstAndMapsOptionIds()
    {
        InspectorObjectCandidateSource source = new();
        SceneNode node = new("Detached");
        try
        {
            source.Reset(typeof(SceneNode), [node], nullable: true);
            Assert.That(source.Count, Is.EqualTo(1), "A node in no world has only the None entry.");
            Assert.That(source.GetOption(0).Id, Is.EqualTo(InspectorObjectCandidateSource.NoneId));
            Assert.That(source.GetOption(0).Label, Is.EqualTo("None"));
            Assert.That(source.TryGetCandidate(InspectorObjectCandidateSource.NoneId, out var none) && none.Value is null, Is.True);
            Assert.That(source.IndexOfId(99), Is.EqualTo(-1));

            source.Reset(typeof(SceneNode), [node], nullable: false);
            Assert.That(source.Count, Is.Zero);
        }
        finally { node.Destroy(true); }
    }
}

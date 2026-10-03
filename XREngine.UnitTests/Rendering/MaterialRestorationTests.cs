using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.UnitTests.Rendering;

/// <summary>Checks material shader ownership across suppressed snapshot setters.</summary>
[TestFixture]
[NonParallelizable]
public sealed class MaterialRestorationTests
{
    [Test]
    public void RestoredShaderList_RebuildsStageCachesAndTracksOnlyOwnedList()
    {
        using XRShader oldShader = new(EShaderType.Vertex);
        using XRShader fragment = new(EShaderType.Fragment);
        using XRShader compute = new(EShaderType.Compute);
        using XRMaterial material = new(oldShader);
        EventList<XRShader> previous = material.Shaders;
        EventList<XRShader> restored = [fragment];
        try
        {
            using (XRBase.SuppressPropertyNotifications())
                material.Shaders = restored;
            var lifecycle = (IPostCookedBinaryDeserialize)material;
            lifecycle.OnPostCookedBinaryDeserialize();
            lifecycle.OnPostCookedBinaryDeserialize();

            Assert.That(material.FragmentShaders, Is.EqualTo(new[] { fragment }));
            Assert.That(material.VertexShaders, Is.Empty);
            long revision = material.ShaderStateRevision;
            previous.Clear();
            Assert.That(material.ShaderStateRevision, Is.EqualTo(revision));

            restored.Add(compute);
            Assert.That(material.ComputeShaders, Is.EqualTo(new[] { compute }));
            Assert.That(material.ShaderStateRevision, Is.EqualTo(revision + 1));
            restored.Remove(fragment);
            Assert.That(material.HasFragmentShader, Is.False);

            material.Destroy(now: true);
            revision = material.ShaderStateRevision;
            restored.Clear();
            Assert.That(material.ShaderStateRevision, Is.EqualTo(revision));
        }
        finally
        {
            previous.Destroy(now: true);
            restored.Destroy(now: true);
        }
    }
}

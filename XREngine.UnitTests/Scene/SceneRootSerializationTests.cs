using System.Reflection;
using System.Text;
using MemoryPack;
using NUnit.Framework;
using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Scene;

namespace XREngine.UnitTests.Scene;

[TestFixture, NonParallelizable]
public sealed class SceneRootSerializationTests
{
    [TestCase("yaml", false)]
    [TestCase("yaml", true)]
    [TestCase("cooked", false)]
    [TestCase("cooked", true)]
    [TestCase("envelope", false)]
    [TestCase("envelope", true)]
    [TestCase("memorypack", false)]
    public void WritesTheExistingSerializedBytes(string format, bool withRoots)
    {
        using IDisposable suppression = XRObjectBase.SuppressObjectCacheRegistration();
        XRScene scene = CreateScene(withRoots);
        try
        {
            byte[] payload = Serialize(scene, format);
            Assert.That(payload, Is.EqualTo(LegacySceneRootFixtures.Get(FixtureName(format, withRoots))));
        }
        finally { Retire(scene); }
    }

    [TestCase("yaml", false)]
    [TestCase("yaml", true)]
    [TestCase("cooked", false)]
    [TestCase("cooked", true)]
    [TestCase("envelope", false)]
    [TestCase("envelope", true)]
    [TestCase("memorypack", false)]
    public void ReadsExistingPayloadAndRestoresExactRootOccurrences(string format, bool withRoots)
    {
        using IDisposable suppression = XRObjectBase.SuppressObjectCacheRegistration();
        XRScene scene = Deserialize(LegacySceneRootFixtures.Get(FixtureName(format, withRoots)), format);
        try
        {
            Assert.That(scene.Name, Is.EqualTo("Legacy root collection"));
            Guid capturedId = Guid.Parse("6e31a66f-c9c4-46d1-a604-53104bf86c20");
            if (format == "memorypack")
            {
                // The legacy direct formatter also generates a new scene ID on read.
                Assert.That(scene.ID, Is.Not.EqualTo(Guid.Empty));
                Assert.That(scene.ID, Is.Not.EqualTo(capturedId));
            }
            else
                Assert.That(scene.ID, Is.EqualTo(capturedId));
            Assert.That(scene.SceneRoots.Count, Is.EqualTo(withRoots ? 3 : 0));
            if (!withRoots)
                return;
            SceneNode first = scene.SceneRoots[0];
            SceneNode second = scene.SceneRoots[1];
            Assert.That(first.Name, Is.EqualTo("First root"));
            Assert.That(second.Name, Is.EqualTo("Second root"));
            Assert.That(second.IsActiveSelf, Is.False);
            Assert.That(scene.SceneRoots[2], Is.SameAs(first));
            SceneRootMembership[] firstOwners = first.CaptureSceneRootMemberships();
            Assert.That(firstOwners, Has.Length.EqualTo(1));
            Assert.That(firstOwners[0].Collection, Is.SameAs(scene.SceneRoots));
            Assert.That(firstOwners[0].Count, Is.EqualTo(2));
            Assert.That(second.CaptureSceneRootMemberships().Single().Count, Is.EqualTo(1));
        }
        finally { Retire(scene); }
    }

    [Test]
    public void DirectMemoryPackKeepsTheExistingNonemptySceneLimitation()
    {
        using IDisposable suppression = XRObjectBase.SuppressObjectCacheRegistration();
        XRScene scene = CreateScene(withRoots: true);
        try
        {
            // The legacy capture also failed because TransformBase has no direct formatter.
            MemoryPackSerializationException? error = Assert.Throws<MemoryPackSerializationException>(
                () => MemoryPackSerializer.Serialize(scene));
            Assert.That(error!.Message, Does.Contain("TransformBase"));
        }
        finally { Retire(scene); }
    }

    private static string FixtureName(string format, bool withRoots) => $"{(withRoots ? "roots" : "empty")}.{format}";

    private static byte[] Serialize(XRScene scene, string format) => format switch
    {
        "yaml" => Encoding.UTF8.GetBytes(AssetManager.Serializer.Serialize(scene)),
        "cooked" => CookedBinarySerializer.Serialize(scene),
        "envelope" => (byte[])Adapter.GetMethod("Serialize", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [scene])!,
        "memorypack" => MemoryPackSerializer.Serialize(scene),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static XRScene Deserialize(byte[] payload, string format) => format switch
    {
        "yaml" => AssetManager.Deserializer.Deserialize<XRScene>(Encoding.UTF8.GetString(payload)),
        "cooked" => (XRScene)CookedBinarySerializer.Deserialize(typeof(XRScene), payload)!,
        "envelope" => (XRScene)Adapter.GetMethod("Deserialize", BindingFlags.Public | BindingFlags.Static)!
            .CreateDelegate<SceneRootEnvelopeReader>()(payload, typeof(XRScene))!,
        "memorypack" => MemoryPackSerializer.Deserialize<XRScene>(payload)!,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static Type Adapter => typeof(XRAsset).Assembly.GetType("XREngine.Core.Files.XRAssetMemoryPackAdapter", throwOnError: true)!;

    private static XRScene CreateScene(bool withRoots)
    {
        XRScene scene = new("Legacy root collection") { IsVisible = false };
        scene.AdoptPersistentID(Guid.Parse("6e31a66f-c9c4-46d1-a604-53104bf86c20"));
        if (!withRoots)
            return scene;
        SceneNode first = new("First root");
        first.AdoptPersistentID(Guid.Parse("6e31a66f-c9c4-46d1-a604-53104bf86c21"));
        first.Transform.AdoptPersistentID(Guid.Parse("6e31a66f-c9c4-46d1-a604-53104bf86c22"));
        SceneNode second = new("Second root") { IsActiveSelf = false };
        second.AdoptPersistentID(Guid.Parse("6e31a66f-c9c4-46d1-a604-53104bf86c23"));
        second.Transform.AdoptPersistentID(Guid.Parse("6e31a66f-c9c4-46d1-a604-53104bf86c24"));
        scene.SceneRoots.AddRange([first, second, first]);
        return scene;
    }

    private static void Retire(XRScene scene)
    {
        SceneNode[] roots = scene.SceneRoots.Distinct(ReferenceEqualityComparer.Instance).Cast<SceneNode>().ToArray();
        scene.Destroy(true);
        foreach (SceneNode node in roots)
        {
            node.Destroy(true);
            node.Transform.Destroy(true);
        }
    }
}

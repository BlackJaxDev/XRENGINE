using NUnit.Framework;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Editor.Publishing;
using XREngine.Scene;

namespace XREngine.UnitTests.Audio;

[TestFixture, NonParallelizable]
public sealed class BrowserAudioWorldAuditTests
{
    [Test]
    public void HydratedWorldRecordsStaticResidencyFailureInCapabilityReport()
    {
        using DataSource data = new(new byte[WebAudioPcmProfile.MaximumBufferBytes / 2]);
        using DataSource extraData = new(new byte[1]);
        AudioData audio = new() { Data = data, Type = AudioData.EPCMType.Byte,
            Frequency = 48000, ChannelCount = 1 };
        AudioData extra = new() { Data = extraData, Type = AudioData.EPCMType.Byte,
            Frequency = 48000, ChannelCount = 1 };
        XRWorld authored = new("audio-world", new XRScene("audio-scene",
            StaticNode("first", audio), StaticNode("second", audio), StaticNode("extra", extra)));
        byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(authored));
        byte[] envelope = CookedAssetEnvelope.Serialize(new CookedAssetBlob(
            CookedAssetTypeReference.Encode(typeof(XRWorld)), CookedAssetFormat.BinaryV2, payload));
        XRWorld cooked = CookedAssetReader.LoadAsset<XRWorld>(envelope)!;
        string reportDirectory = Path.Combine(Path.GetTempPath(), "xrengine-audio-audit-" + Guid.NewGuid().ToString("N"));
        try
        {
            using BrowserCapabilityReport report = new("/game/audio-world.asset", reportDirectory, CancellationToken.None);
            BrowserWorldCapabilityAudit.Inspect(cooked, null, CancellationToken.None,
                inheritedScenePasses: new HashSet<int>(), report: report);
            Assert.That(report.Diagnostics.Any(static diagnostic =>
                diagnostic.Code == "BrowserCook.AudioStaticResidencyExceeded"), Is.True);
            Assert.Throws<NotSupportedException>(() => report.ThrowIfBlocked("audio-report"));
        }
        finally
        {
            HashSet<DataSource> cookedData = new(ReferenceEqualityComparer.Instance);
            foreach (XRScene scene in cooked.Scenes)
                foreach (SceneNode node in scene.RootNodes)
                    if (node.GetComponent<AudioSourceComponent>()?.StaticBuffer?.Data is { } source)
                        cookedData.Add(source);
            foreach (DataSource source in cookedData)
                source.Dispose();
            cooked.Destroy();
            authored.Destroy();
            if (Directory.Exists(reportDirectory))
                Directory.Delete(reportDirectory, recursive: true);
        }
    }

    private static SceneNode StaticNode(string name, AudioData audio)
    {
        SceneNode node = new(name);
        AudioSourceComponent source = node.AddComponent<AudioSourceComponent>()!;
        source.RelativeToListener = true;
        source.StaticBuffer = audio;
        return node;
    }
}

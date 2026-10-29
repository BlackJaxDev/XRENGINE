using System.Buffers.Binary;
using System.Reflection;
using NUnit.Framework;
using XREngine.Audio;
using XREngine.Components;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Editor.Publishing;
using XREngine.Scene;

namespace XREngine.UnitTests.Audio;

[TestFixture, NonParallelizable]
public sealed class BrowserAudioCookingTests
{
    [TestCase(AudioData.EPCMType.Byte, 1)]
    [TestCase(AudioData.EPCMType.Byte, 2)]
    [TestCase(AudioData.EPCMType.Short, 1)]
    [TestCase(AudioData.EPCMType.Short, 2)]
    [TestCase(AudioData.EPCMType.Float, 1)]
    [TestCase(AudioData.EPCMType.Float, 2)]
    public void CookedAuthoredAudioReachesTransportWithoutChangingSamples(AudioData.EPCMType type, int channels)
    {
        byte[] expected = Samples(type);
        using DataSource data = new(expected);
        AudioData audio = new() { Name = "authored-pcm", Data = data, Type = type, Frequency = 48000, ChannelCount = channels };
        SceneNode node = new("Audio");
        node.AddComponent<AudioSourceComponent>()!.StaticBuffer = audio;
        XRScene scene = new("AudioScene", node);
        int validated = 0;
        CookedBinarySerializationCallbacks callbacks = new()
        {
            OnSerializingValue = value =>
            {
                if (value is AudioData pcm)
                {
                    BrowserAudioCookProfile.Validate(pcm);
                    validated++;
                }
                return value;
            },
        };
        byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() =>
            CookedBinarySerializer.Serialize(scene, callbacks));
        byte[] envelope = CookedAssetEnvelope.Serialize(new CookedAssetBlob(
            CookedAssetTypeReference.Encode(typeof(XRScene)), CookedAssetFormat.BinaryV2, payload));
        XRScene cookedScene = CookedAssetReader.LoadAsset<XRScene>(envelope)!;
        SceneNode clone = cookedScene.RootNodes.Single();
        AudioData cooked = clone.GetComponent<AudioSourceComponent>()!.StaticBuffer!;
        try
        {
            Assert.That(validated, Is.GreaterThan(0));
            Assert.That(cooked, Is.Not.SameAs(audio));
            Assert.That(cooked.Data, Is.Not.SameAs(data));
            Assert.That(cooked.Name, Is.EqualTo(audio.Name));
            Assert.That(cooked.ChannelCount, Is.EqualTo(channels));
            Assert.That(cooked.Type, Is.EqualTo(type));
            BrowserAudioCookProfile.Validate(cooked);
            using PcmRecordingTransport transport = new();
            using ListenerContext listener = CreateListener(transport);
            AudioBuffer buffer = listener.TakeBuffer();
            buffer.SetData(cooked);
            Assert.That(transport.Bytes, Is.EqualTo(expected));
            Assert.That(transport.Frequency, Is.EqualTo(48000));
            Assert.That(transport.Channels, Is.EqualTo(channels));
            Assert.That(transport.Format, Is.EqualTo((SampleFormat)type));
        }
        finally
        {
            cooked.Data?.Dispose();
            clone.Destroy();
            node.Destroy();
            cookedScene.Destroy();
            scene.Destroy();
        }
    }

    [Test]
    public void StandalonePcmAssetLoadsAfterItsSourceStorageIsDisposed()
    {
        byte[] expected = Samples(AudioData.EPCMType.Short);
        using DataSource data = new(expected);
        AudioData audio = new() { Name = "saved-audio", Data = data, Type = AudioData.EPCMType.Short,
            Frequency = 44100, ChannelCount = 2, OriginalPath = "unavailable-original.wav" };
        BrowserAudioCookProfile.Validate(audio);
        byte[] payload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(() => CookedBinarySerializer.Serialize(audio));
        byte[] envelope = CookedAssetEnvelope.Serialize(new CookedAssetBlob(
            CookedAssetTypeReference.Encode(typeof(AudioData)), CookedAssetFormat.BinaryV2, payload));
        data.Dispose();
        AudioData cooked = CookedAssetReader.LoadAsset<AudioData>(envelope)!;
        try
        {
            BrowserAudioCookProfile.Validate(cooked);
            Assert.That(cooked.Data!.GetBytes(), Is.EqualTo(expected));
            Assert.That(cooked.Frequency, Is.EqualTo(44100));
            Assert.That(cooked.ChannelCount, Is.EqualTo(2));
        }
        finally
        {
            cooked.Data?.Dispose();
            cooked.Destroy();
            audio.Destroy();
        }
    }

    [TestCase(0, 1, AudioData.EPCMType.Byte, 4)]
    [TestCase(7999, 1, AudioData.EPCMType.Byte, 4)]
    [TestCase(192001, 1, AudioData.EPCMType.Byte, 4)]
    [TestCase(48000, 0, AudioData.EPCMType.Byte, 4)]
    [TestCase(48000, 6, AudioData.EPCMType.Byte, 4)]
    [TestCase(48000, 1, (AudioData.EPCMType)99, 4)]
    [TestCase(48000, 1, AudioData.EPCMType.Short, 3)]
    [TestCase(48000, 2, AudioData.EPCMType.Float, 4)]
    [TestCase(48000, 1, AudioData.EPCMType.Byte, 0)]
    public void CookRejectsUnsupportedPcm(int frequency, int channels, AudioData.EPCMType type, int length)
    {
        using DataSource data = new(new byte[length]);
        AudioData audio = new() { Name = "bad-audio", Data = data, Type = type, Frequency = frequency, ChannelCount = channels };
        NotSupportedException? failure = Assert.Throws<NotSupportedException>(() => BrowserAudioCookProfile.Validate(audio));
        Assert.That(failure!.Message, Does.StartWith("BrowserCook.AudioPcmUnsupported:"));
        Assert.That(failure.Message, Does.Contain("bad-audio"));
    }

    [TestCase(float.NaN)]
    [TestCase(float.PositiveInfinity)]
    [TestCase(float.NegativeInfinity)]
    public void CookRejectsNonfiniteSamples(float sample)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, sample);
        using DataSource data = new(bytes);
        AudioData audio = new() { Data = data, Type = AudioData.EPCMType.Float, Frequency = 48000, ChannelCount = 1 };
        Assert.Throws<NotSupportedException>(() => BrowserAudioCookProfile.Validate(audio));
    }

    [Test]
    public void CookRejectsMissingPayload() => Assert.Throws<NotSupportedException>(() => BrowserAudioCookProfile.Validate(new AudioData()));

    [Test]
    public void ProfileBoundsPayloadBeforeReadingSamples()
    {
        Assert.That(WebAudioPcmProfile.ValidateLayout(WebAudioPcmProfile.MaximumBufferBytes, 8000, 2, SampleFormat.Float),
            Is.EqualTo(WebAudioPcmProfile.MaximumBufferBytes / 8));
        Assert.That(WebAudioPcmProfile.ValidateLayout(8, 192000, 2, SampleFormat.Float), Is.EqualTo(1));
        Assert.Throws<InvalidDataException>(() => WebAudioPcmProfile.ValidateLayout(WebAudioPcmProfile.MaximumBufferBytes + 1, 48000, 1, SampleFormat.Byte));
    }

    [Test]
    public void UploadPreservesChannelCountAndRejectedDataDoesNotReplaceMetadata()
    {
        using PcmRecordingTransport transport = new();
        using ListenerContext listener = CreateListener(transport);
        AudioBuffer buffer = listener.TakeBuffer();
        using DataSource good = new(new byte[] { 0, 128, 255 });
        using DataSource bad = new(new byte[] { 0, 128, 255 });
        buffer.SetData(new AudioData { Data = good, Type = AudioData.EPCMType.Byte, Frequency = 48000, ChannelCount = 1 });
        Assert.Throws<NotSupportedException>(() => buffer.SetData(
            new AudioData { Data = bad, Type = AudioData.EPCMType.Byte, Frequency = 24000, ChannelCount = 3 }));
        Assert.That(buffer.Data, Is.SameAs(good));
        Assert.That(buffer.Frequency, Is.EqualTo(48000));
        Assert.That(transport.Bytes, Is.EqualTo(new byte[] { 0, 128, 255 }));
    }

    private static ListenerContext CreateListener(IAudioTransport transport)
        => (ListenerContext)Activator.CreateInstance(typeof(ListenerContext), BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, args: [transport, null], culture: null)!;

    private static byte[] Samples(AudioData.EPCMType type)
    {
        if (type == AudioData.EPCMType.Byte)
            return [0, 128, 255, 64];
        if (type == AudioData.EPCMType.Short)
            return [0, 128, 0, 0, 255, 127, 0, 64];
        byte[] bytes = new byte[16];
        float[] samples = [-1, 0, 1, 0.25f];
        for (int index = 0; index < samples.Length; index++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4), samples[index]);
        return bytes;
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;
using NUnit.Framework;
using Shouldly;
using XREngine.Networking;

namespace XREngine.UnitTests.Core;

[TestFixture]
public sealed class NetworkingContractsTests
{
    [Test]
    public void StateChangePayloadSerializer_RoundTripsMemoryPackPayload()
    {
        var payload = new PlayerHeartbeat
        {
            ServerPlayerIndex = 7,
            ClientId = "client-a",
            TimestampUtc = 123.5,
            InstanceId = Guid.NewGuid()
        };

        string serialized = InvokeSerializerSerialize(payload);
        bool success = InvokeSerializerTryDeserialize(serialized, out PlayerHeartbeat? deserialized);

        success.ShouldBeTrue();
        deserialized.ShouldNotBeNull();
        deserialized!.ServerPlayerIndex.ShouldBe(payload.ServerPlayerIndex);
        deserialized.ClientId.ShouldBe(payload.ClientId);
        deserialized.TimestampUtc.ShouldBe(payload.TimestampUtc);
        deserialized.InstanceId.ShouldBe(payload.InstanceId);
    }

    [Test]
    public void HumanoidPoseCodec_WriteBaselineAvatar_ProducesExpectedPacketLength()
    {
        var buffer = new List<byte>();
        QuantizedHumanoidPose pose = HumanoidPoseCodec.Quantize(new HumanoidPoseSample(
            Vector3.Zero,
            0f,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero,
            Vector3.Zero));

        int written = HumanoidPoseCodec.WriteBaselineAvatar(buffer, entityId: 3, pose, lod: 1, baselineSequence: 9);

        written.ShouldBe(HumanoidPoseCodec.BaselineAvatarBytes);
        buffer.Count.ShouldBe(HumanoidPoseCodec.BaselineAvatarBytes);
        HumanoidPoseCodec.TryReadBaselineAvatar(buffer.ToArray(), out var header, out _, out int bytesConsumed).ShouldBeTrue();
        header.EntityId.ShouldBe((ushort)3);
        header.Sequence.ShouldBe((ushort)9);
        bytesConsumed.ShouldBe(HumanoidPoseCodec.BaselineAvatarBytes);
    }

    private static string InvokeSerializerSerialize<T>(T payload)
    {
        var serializerType = typeof(PlayerJoinRequest).Assembly.GetType("XREngine.Networking.StateChangePayloadSerializer")!;
        var method = serializerType.GetMethod("Serialize")!.MakeGenericMethod(typeof(T));
        return (string)method.Invoke(null, [payload])!;
    }

    private static bool InvokeSerializerTryDeserialize<T>(string data, out T? payload)
    {
        object?[] args = [data, null];
        var serializerType = typeof(PlayerJoinRequest).Assembly.GetType("XREngine.Networking.StateChangePayloadSerializer")!;
        var method = serializerType.GetMethod("TryDeserialize")!.MakeGenericMethod(typeof(T));
        bool result = (bool)method.Invoke(null, args)!;
        payload = (T?)args[1];
        return result;
    }
}
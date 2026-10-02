using System.Numerics;
using XREngine.Networking;

namespace XREngine.RenderBench;

internal static partial class RuntimeDataLayoutScenario
{
    private static void MeasureNetworking(RenderBenchOptions options, RuntimeDataLayoutReport report)
    {
        foreach (int avatars in Populations)
        {
            FixedQuantizedHumanoidPose pose = HumanoidPoseCodec.QuantizeFixed(new HumanoidPoseSample(
                new Vector3(0, 1.7f, 0), 0, new Vector3(0, 0.9f, 0), new Vector3(0, 1.7f, 0),
                new Vector3(-0.4f, 1.3f, 0), new Vector3(0.4f, 1.3f, 0), new Vector3(-0.2f, 0, 0), new Vector3(0.2f, 0, 0)));
            byte[][] packets = new byte[avatars][];
            int[] lengths = new int[avatars];
            for (int i = 0; i < avatars; i++)
                packets[i] = new byte[BaseNetworkingManager.MaxHighRatePayloadBytes];
            byte[] scratch = new byte[BaseNetworkingManager.HighRateSlotBytes];
            var ring = new RealtimePacketSendRing(avatars, BaseNetworkingManager.HighRateSlotBytes);
            var slabs = new PersistentReceiveSlabPool(1, BaseNetworkingManager.HighRateSlotBytes);
            Guid session = new("52454e44-4552-4245-4e43-480000000001");

            for (int channel = 0; channel < 3; channel++)
            {
                int selectedChannel = channel;
                Action send = () =>
                {
                    for (int i = 0; i < avatars; i++)
                    {
                        int written;
                        bool success;
                        if (selectedChannel == 0)
                        {
                            HumanoidPoseSpanPacketWriter writer = new(scratch);
                            writer.BeginFrame(HumanoidPosePacketKind.Baseline, 1);
                            success = writer.TryAddBaselineAvatar((ushort)(i + 1), pose);
                            HumanoidPosePacketHeader header = new() { SessionId = session, AvatarCount = 1, Kind = HumanoidPosePacketKind.Baseline,
                                BaselineSequence = 1, FrameSequence = 1, Channel = NetworkReplicationChannel.HumanoidPose };
                            success &= HumanoidPosePacket.TryWrite(packets[i], header, "benchmark"u8, [], writer.Payload, out written);
                        }
                        else if (selectedChannel == 1)
                        {
                            ClockSyncSample clock = new(session, i + 1, 1, 2, 3, 1);
                            success = ClockSyncPacket.TryWrite(packets[i], clock, "benchmark"u8, out written);
                        }
                        else
                        {
                            NetworkDeltaPacketHeader delta = new() { SessionId = session, ServerTickId = 2, BaselineTickId = 1, DeltaSequence = 1 };
                            success = NetworkDeltaPacket.TryWrite(packets[i], delta, [], scratch.AsSpan(0, 32), out written);
                        }
                        lengths[i] = written;
                        if (!success || !ring.TryEnqueue(packets[i].AsSpan(0, written)) || !ring.TryDequeue(out _))
                            throw new InvalidDataException("Network send fixture failed to encode or enqueue.");
                    }
                };
                send();
                Action receive = () =>
                {
                    for (int i = 0; i < avatars; i++)
                    {
                        if (!slabs.TryRent(out PersistentReceiveSlab slab))
                            throw new InvalidOperationException("Receive fixture exhausted its slab.");
                        try
                        {
                            packets[i].AsSpan(0, lengths[i]).CopyTo(slab.WritableSpan);
                            slab.Length = lengths[i];
                            bool success;
                            if (selectedChannel == 0)
                            {
                                success = HumanoidPosePacket.TryRead(slab.Span, out HumanoidPosePacketView view);
                                HumanoidPosePacketCursor cursor = new(view.Payload);
                                success &= cursor.TryReadNextBaseline(out _, out _) && !cursor.HasRemaining;
                            }
                            else if (selectedChannel == 1)
                                success = ClockSyncPacket.TryRead(slab.Span, out _, out _);
                            else
                                success = NetworkDeltaPacket.TryRead(slab.Span, out _);
                            if (!success)
                                throw new InvalidDataException("Network receive fixture failed to decode.");
                        }
                        finally { slabs.Return(slab); }
                    }
                };
                Action relay = () =>
                {
                    for (int i = 0; i < avatars; i++)
                    {
                        if (!ring.TryEnqueue(packets[i].AsSpan(0, lengths[i])) || !ring.TryDequeue(out ReadOnlyMemory<byte> packet))
                            throw new InvalidOperationException("Relay fixture queue failed.");
                        packet.Span.CopyTo(scratch);
                    }
                };
                for (int repeat = 0; repeat < options.ScenarioRepeats; repeat++)
                {
                    var sent = Measure(send, options.WarmupFrames, options.CaptureFrames);
                    var received = Measure(receive, options.WarmupFrames, options.CaptureFrames);
                    var relayed = Measure(relay, options.WarmupFrames, options.CaptureFrames);
                    int packetBytes = 0;
                    for (int i = 0; i < avatars; i++) packetBytes += lengths[i];
                    report.Networking.Add(new(channel == 0 ? "pose-baseline" : channel == 1 ? "clock-sync" : "replication-delta",
                        avatars, repeat, options.CaptureFrames, packetBytes, sent.Bytes, received.Bytes, relayed.Bytes,
                        sent.Milliseconds, received.Milliseconds, relayed.Milliseconds));
                }
            }
        }
    }
}

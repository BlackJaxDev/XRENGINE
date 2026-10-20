using XREngine.Networking;

namespace XREngine;

public abstract partial class BaseNetworkingManager
{
    private const int MaximumReliableRealtimePackets = 32;

    /// <summary>Counts queued frames across both lanes. The caller holds the peer lock.</summary>
    private static int QueuedPacketCount_NoLock(UdpPeerState peer)
        => peer.SendQueue.Count + peer.PendingTransforms.Count + (peer.HighRateRing?.Count ?? 0);

    private void EnqueueForPeer(UdpPeerState peer, QueuedUdpPacket packet, bool retainReliableReservation = false, bool requeue = false)
    {
        if (!UseBoundedRealtimeQueues)
        {
            peer.SendQueue.Enqueue(packet);
            return;
        }

        bool overflow;
        lock (peer.Sync)
        {
            bool coalesce = !packet.ResendOnFailedAck && packet.OwnerId != Guid.Empty
                && packet.Bytes.Length >= HeaderLen
                && ((packet.Bytes[3] >> 1) & 7) == (byte)EBroadcastType.Transform;
            bool replaced = coalesce && peer.PendingTransforms.ContainsKey(packet.OwnerId);
            // A producer can queue a newer full snapshot while the send pump tries protection.
            if (requeue && replaced)
                return;

            int previousBytes = replaced ? peer.PendingTransforms[packet.OwnerId].Bytes.Length : 0;
            overflow = (!replaced && QueuedPacketCount_NoLock(peer) >= RealtimeWebSocketProtocol.MaximumQueuedDatagrams)
                || peer.PendingBytes - previousBytes + packet.Bytes.Length > RealtimeWebSocketProtocol.MaximumQueuedBytes
                || packet.ResendOnFailedAck && !retainReliableReservation && peer.ReliablePacketCount >= MaximumReliableRealtimePackets;
            if (!overflow)
            {
                if (coalesce)
                    peer.PendingTransforms[packet.OwnerId] = packet;
                else
                    peer.SendQueue.Enqueue(packet);
                peer.PendingBytes += packet.Bytes.Length - previousBytes;
                if (packet.ResendOnFailedAck && !retainReliableReservation)
                    peer.ReliablePacketCount++;
            }
            else if (packet.ResendOnFailedAck && retainReliableReservation)
            {
                peer.ReliablePacketCount--;
            }
        }

        if (overflow)
            OnRealtimeQueueOverflow();
    }

    private bool TryTakeQueuedPacket(UdpPeerState peer, out QueuedUdpPacket packet)
    {
        if (!UseBoundedRealtimeQueues)
            return peer.SendQueue.TryDequeue(out packet);

        lock (peer.Sync)
        {
            if (peer.SendQueue.TryDequeue(out packet))
            {
                peer.PendingBytes -= packet.Bytes.Length;
                return true;
            }

            Guid selected = Guid.Empty;
            foreach (KeyValuePair<Guid, QueuedUdpPacket> pending in peer.PendingTransforms)
            {
                selected = pending.Key;
                packet = pending.Value;
                break;
            }
            if (selected == Guid.Empty)
                return false;

            peer.PendingTransforms.Remove(selected);
            peer.PendingBytes -= packet.Bytes.Length;
            return true;
        }
    }

    private void ReleaseReliablePacket(UdpPeerState peer, bool reliable)
    {
        if (!UseBoundedRealtimeQueues || !reliable)
            return;

        lock (peer.Sync)
            peer.ReliablePacketCount--;
    }
}

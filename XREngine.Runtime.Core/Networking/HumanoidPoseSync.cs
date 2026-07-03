using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using BitsKit.IO;

namespace XREngine.Networking
{
    public enum HumanoidTrackerSlot : int
    {
        Hip = 0,
        Head = 1,
        LeftHand = 2,
        RightHand = 3,
        LeftFoot = 4,
        RightFoot = 5
    }

    [Flags]
    public enum HumanoidPoseFlags : ushort
    {
        None = 0,
        Hip = 1 << 0,
        Head = 1 << 1,
        LeftHand = 1 << 2,
        RightHand = 1 << 3,
        LeftFoot = 1 << 4,
        RightFoot = 1 << 5,
        LodMask = 0b11 << 6,
        Baseline = 1 << 8,
        RootChanged = 1 << 9,
        ForceResend = 1 << 10,
    }

    public readonly record struct HumanoidQuantizationSettings(float TileSize = 32f, float TrackerRange = 2f)
    {
        public static HumanoidQuantizationSettings Default { get; } = new HumanoidQuantizationSettings();
    }

    public readonly record struct HumanoidPoseDeltaSettings(
        int SmallDeltaBits = 7,
        short PositionSmallThreshold = 64,
        short YawSmallThreshold = 512,
        short Deadzone = 1)
    {
        public static HumanoidPoseDeltaSettings Default { get; } = new HumanoidPoseDeltaSettings();
    }

    public readonly record struct HumanoidPoseSample(
        Vector3 RootPosition,
        float RootYawRadians,
        Vector3 Hip,
        Vector3 Head,
        Vector3 LeftHand,
        Vector3 RightHand,
        Vector3 LeftFoot,
        Vector3 RightFoot)
    {
        public Vector3 this[HumanoidTrackerSlot slot] => slot switch
        {
            HumanoidTrackerSlot.Hip => Hip,
            HumanoidTrackerSlot.Head => Head,
            HumanoidTrackerSlot.LeftHand => LeftHand,
            HumanoidTrackerSlot.RightHand => RightHand,
            HumanoidTrackerSlot.LeftFoot => LeftFoot,
            HumanoidTrackerSlot.RightFoot => RightFoot,
            _ => Vector3.Zero
        };
    }

    public readonly record struct QuantizedRootPose(short SectorX, short SectorZ, short LocalX, short LocalY, short LocalZ, ushort Yaw);
    public readonly record struct QuantizedTrackerPose(short X, short Y, short Z);
    public readonly record struct QuantizedHumanoidPose(QuantizedRootPose Root, QuantizedTrackerPose[] Trackers)
    {
        public QuantizedTrackerPose this[HumanoidTrackerSlot slot]
        {
            get => Trackers[(int)slot];
            set => Trackers[(int)slot] = value;
        }
    }

    public readonly record struct FixedQuantizedHumanoidPose(
        QuantizedRootPose Root,
        QuantizedTrackerPose Hip,
        QuantizedTrackerPose Head,
        QuantizedTrackerPose LeftHand,
        QuantizedTrackerPose RightHand,
        QuantizedTrackerPose LeftFoot,
        QuantizedTrackerPose RightFoot)
    {
        public QuantizedTrackerPose this[HumanoidTrackerSlot slot] => slot switch
        {
            HumanoidTrackerSlot.Hip => Hip,
            HumanoidTrackerSlot.Head => Head,
            HumanoidTrackerSlot.LeftHand => LeftHand,
            HumanoidTrackerSlot.RightHand => RightHand,
            HumanoidTrackerSlot.LeftFoot => LeftFoot,
            HumanoidTrackerSlot.RightFoot => RightFoot,
            _ => default
        };
    }

    public readonly record struct HumanoidPoseAvatarHeader(ushort EntityId, HumanoidPoseFlags Flags, ushort Sequence);
    public static class HumanoidPoseCodec
    {
        public const int TrackerCount = 6;
        public const byte AllTrackersMask = 0b0011_1111;
        public const int BaselineAvatarBytes = 56;
        private const int MaxDeltaBytes = 128;

        public static byte GetLod(HumanoidPoseFlags flags)
            => (byte)(((ushort)flags >> 6) & 0b11);

        public static byte GetTrackerMask(HumanoidPoseFlags flags)
            => (byte)((ushort)flags & AllTrackersMask);

        public static HumanoidPoseFlags BuildFlags(byte lod, byte trackerMask, bool isBaseline, bool rootChanged, bool forceResend)
        {
            HumanoidPoseFlags flags = (HumanoidPoseFlags)(trackerMask & AllTrackersMask);
            flags |= (HumanoidPoseFlags)((lod & 0b11) << 6);
            if (isBaseline)
                flags |= HumanoidPoseFlags.Baseline;
            if (rootChanged)
                flags |= HumanoidPoseFlags.RootChanged;
            if (forceResend)
                flags |= HumanoidPoseFlags.ForceResend;
            return flags;
        }

        public static QuantizedHumanoidPose Quantize(HumanoidPoseSample sample, HumanoidQuantizationSettings? settings = null)
        {
            HumanoidQuantizationSettings config = settings ?? HumanoidQuantizationSettings.Default;
            float halfTile = config.TileSize * 0.5f;

            short sectorX = (short)MathF.Floor(sample.RootPosition.X / config.TileSize);
            short sectorZ = (short)MathF.Floor(sample.RootPosition.Z / config.TileSize);
            float sectorCenterX = (sectorX + 0.5f) * config.TileSize;
            float sectorCenterZ = (sectorZ + 0.5f) * config.TileSize;

            short localX = QuantizeSigned(sample.RootPosition.X - sectorCenterX, halfTile);
            short localY = QuantizeSigned(sample.RootPosition.Y, halfTile);
            short localZ = QuantizeSigned(sample.RootPosition.Z - sectorCenterZ, halfTile);
            ushort yaw = QuantizeYaw(sample.RootYawRadians);

            QuantizedRootPose root = new(sectorX, sectorZ, localX, localY, localZ, yaw);
            QuantizedTrackerPose[] trackers = new QuantizedTrackerPose[TrackerCount];
            for (int i = 0; i < TrackerCount; i++)
            {
                Vector3 local = sample[(HumanoidTrackerSlot)i];
                trackers[i] = QuantizeTracker(local, config.TrackerRange);
            }

            return new QuantizedHumanoidPose(root, trackers);
        }

        public static FixedQuantizedHumanoidPose QuantizeFixed(HumanoidPoseSample sample, HumanoidQuantizationSettings? settings = null)
        {
            HumanoidQuantizationSettings config = settings ?? HumanoidQuantizationSettings.Default;
            float halfTile = config.TileSize * 0.5f;

            short sectorX = (short)MathF.Floor(sample.RootPosition.X / config.TileSize);
            short sectorZ = (short)MathF.Floor(sample.RootPosition.Z / config.TileSize);
            float sectorCenterX = (sectorX + 0.5f) * config.TileSize;
            float sectorCenterZ = (sectorZ + 0.5f) * config.TileSize;

            QuantizedRootPose root = new(
                sectorX,
                sectorZ,
                QuantizeSigned(sample.RootPosition.X - sectorCenterX, halfTile),
                QuantizeSigned(sample.RootPosition.Y, halfTile),
                QuantizeSigned(sample.RootPosition.Z - sectorCenterZ, halfTile),
                QuantizeYaw(sample.RootYawRadians));

            return new FixedQuantizedHumanoidPose(
                root,
                QuantizeTracker(sample.Hip, config.TrackerRange),
                QuantizeTracker(sample.Head, config.TrackerRange),
                QuantizeTracker(sample.LeftHand, config.TrackerRange),
                QuantizeTracker(sample.RightHand, config.TrackerRange),
                QuantizeTracker(sample.LeftFoot, config.TrackerRange),
                QuantizeTracker(sample.RightFoot, config.TrackerRange));
        }

        public static HumanoidPoseSample Dequantize(QuantizedHumanoidPose quantized, HumanoidQuantizationSettings? settings = null)
        {
            HumanoidQuantizationSettings config = settings ?? HumanoidQuantizationSettings.Default;
            float halfTile = config.TileSize * 0.5f;

            Vector3 rootPosition = new(
                DequantizeSector(quantized.Root.SectorX, quantized.Root.LocalX, config.TileSize, halfTile),
                DequantizeSigned(quantized.Root.LocalY, halfTile),
                DequantizeSector(quantized.Root.SectorZ, quantized.Root.LocalZ, config.TileSize, halfTile));
            float yaw = DequantizeYaw(quantized.Root.Yaw);

            Vector3[] trackers = new Vector3[TrackerCount];
            for (int i = 0; i < TrackerCount; i++)
                trackers[i] = DequantizeTracker(quantized.Trackers[i], config.TrackerRange);

            return new HumanoidPoseSample(
                rootPosition,
                yaw,
                trackers[0],
                trackers[1],
                trackers[2],
                trackers[3],
                trackers[4],
                trackers[5]);
        }

        public static HumanoidPoseSample Dequantize(FixedQuantizedHumanoidPose quantized, HumanoidQuantizationSettings? settings = null)
        {
            HumanoidQuantizationSettings config = settings ?? HumanoidQuantizationSettings.Default;
            float halfTile = config.TileSize * 0.5f;

            Vector3 rootPosition = new(
                DequantizeSector(quantized.Root.SectorX, quantized.Root.LocalX, config.TileSize, halfTile),
                DequantizeSigned(quantized.Root.LocalY, halfTile),
                DequantizeSector(quantized.Root.SectorZ, quantized.Root.LocalZ, config.TileSize, halfTile));

            return new HumanoidPoseSample(
                rootPosition,
                DequantizeYaw(quantized.Root.Yaw),
                DequantizeTracker(quantized.Hip, config.TrackerRange),
                DequantizeTracker(quantized.Head, config.TrackerRange),
                DequantizeTracker(quantized.LeftHand, config.TrackerRange),
                DequantizeTracker(quantized.RightHand, config.TrackerRange),
                DequantizeTracker(quantized.LeftFoot, config.TrackerRange),
                DequantizeTracker(quantized.RightFoot, config.TrackerRange));
        }

        public static bool TryWriteBaselineAvatar(
            Span<byte> destination,
            ushort entityId,
            in FixedQuantizedHumanoidPose pose,
            byte lod,
            out int bytesWritten,
            byte trackerMask = AllTrackersMask,
            ushort baselineSequence = 0)
        {
            bytesWritten = 0;
            if (destination.Length < BaselineAvatarBytes)
                return false;

            Span<byte> span = destination.Slice(0, BaselineAvatarBytes);
            BinaryPrimitives.WriteUInt16LittleEndian(span, entityId);
            HumanoidPoseFlags flags = BuildFlags(lod, trackerMask, isBaseline: true, rootChanged: true, forceResend: false);
            BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)flags);
            const ushort payloadLength = BaselineAvatarBytes - 6;
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], payloadLength);
            WriteRoot(span[6..], pose.Root);

            int trackerOffset = 18;
            for (int i = 0; i < TrackerCount; i++)
            {
                WriteTracker(span[trackerOffset..], pose[(HumanoidTrackerSlot)i]);
                trackerOffset += 6;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(span[^2..], baselineSequence);
            bytesWritten = BaselineAvatarBytes;
            return true;
        }

        public static bool TryReadBaselineAvatarFixed(
            ReadOnlySpan<byte> data,
            out HumanoidPoseAvatarHeader header,
            out FixedQuantizedHumanoidPose pose,
            out int bytesConsumed)
        {
            header = default;
            pose = default;
            bytesConsumed = 0;
            if (data.Length < BaselineAvatarBytes)
                return false;

            ushort entity = BinaryPrimitives.ReadUInt16LittleEndian(data);
            HumanoidPoseFlags flags = (HumanoidPoseFlags)BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
            ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            if (data.Length < 6 + payloadLength || payloadLength < 50)
                return false;

            QuantizedRootPose root = ReadRoot(data[6..]);
            int offset = 18;
            QuantizedTrackerPose hip = ReadTracker(data[offset..]); offset += 6;
            QuantizedTrackerPose head = ReadTracker(data[offset..]); offset += 6;
            QuantizedTrackerPose leftHand = ReadTracker(data[offset..]); offset += 6;
            QuantizedTrackerPose rightHand = ReadTracker(data[offset..]); offset += 6;
            QuantizedTrackerPose leftFoot = ReadTracker(data[offset..]); offset += 6;
            QuantizedTrackerPose rightFoot = ReadTracker(data[offset..]);
            ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6 + payloadLength - 2));

            header = new HumanoidPoseAvatarHeader(entity, flags, sequence);
            pose = new FixedQuantizedHumanoidPose(root, hip, head, leftHand, rightHand, leftFoot, rightFoot);
            bytesConsumed = 6 + payloadLength;
            return true;
        }

        public static int WriteBaselineAvatar(
            List<byte> buffer,
            ushort entityId,
            QuantizedHumanoidPose pose,
            byte lod,
            byte trackerMask = AllTrackersMask,
            ushort baselineSequence = 0)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            ValidateTrackers(pose);
            int offset = buffer.Count;
            EnsureCapacity(buffer, BaselineAvatarBytes);

            Span<byte> span = stackalloc byte[BaselineAvatarBytes];
            BinaryPrimitives.WriteUInt16LittleEndian(span, entityId);
            HumanoidPoseFlags flags = BuildFlags(lod, trackerMask, isBaseline: true, rootChanged: true, forceResend: false);
            BinaryPrimitives.WriteUInt16LittleEndian(span[2..], (ushort)flags);
            const ushort payloadLength = BaselineAvatarBytes - 6;
            BinaryPrimitives.WriteUInt16LittleEndian(span[4..], payloadLength);
            WriteRoot(span[6..], pose.Root);

            int trackerOffset = 18;
            for (int i = 0; i < TrackerCount; i++)
            {
                WriteTracker(span[trackerOffset..], pose.Trackers[i]);
                trackerOffset += 6;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(span[^2..], baselineSequence);
            buffer.AddRange(span.ToArray());
            return buffer.Count - offset;
        }

        public static int WriteDeltaAvatar(
            List<byte> buffer,
            ushort entityId,
            QuantizedHumanoidPose current,
            QuantizedHumanoidPose baseline,
            byte lod,
            HumanoidPoseDeltaSettings? deltaSettings = null,
            byte trackerMask = AllTrackersMask,
            bool forceFullResend = false)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            HumanoidPoseDeltaSettings settings = deltaSettings ?? HumanoidPoseDeltaSettings.Default;
            ValidateTrackers(current);
            ValidateTrackers(baseline);

            Span<short> trackerDeltaX = stackalloc short[TrackerCount];
            Span<short> trackerDeltaY = stackalloc short[TrackerCount];
            Span<short> trackerDeltaZ = stackalloc short[TrackerCount];
            Span<byte> trackerAxisMask = stackalloc byte[TrackerCount];

            byte changedTrackerMask = 0;
            for (int i = 0; i < TrackerCount; i++)
            {
                if (((trackerMask >> i) & 1) == 0)
                    continue;

                short dx = (short)(current.Trackers[i].X - baseline.Trackers[i].X);
                short dy = (short)(current.Trackers[i].Y - baseline.Trackers[i].Y);
                short dz = (short)(current.Trackers[i].Z - baseline.Trackers[i].Z);

                byte axisMask = 0;
                if (Math.Abs(dx) > settings.Deadzone)
                {
                    axisMask |= 1;
                    trackerDeltaX[i] = dx;
                }

                if (Math.Abs(dy) > settings.Deadzone)
                {
                    axisMask |= 1 << 1;
                    trackerDeltaY[i] = dy;
                }

                if (Math.Abs(dz) > settings.Deadzone)
                {
                    axisMask |= 1 << 2;
                    trackerDeltaZ[i] = dz;
                }

                trackerAxisMask[i] = axisMask;
                if (axisMask != 0)
                    changedTrackerMask |= (byte)(1 << i);
            }

            bool rootXChanged = Math.Abs(current.Root.LocalX - baseline.Root.LocalX) > settings.Deadzone;
            bool rootYChanged = Math.Abs(current.Root.LocalY - baseline.Root.LocalY) > settings.Deadzone;
            bool rootZChanged = Math.Abs(current.Root.LocalZ - baseline.Root.LocalZ) > settings.Deadzone;
            int yawDelta = DeltaYaw(current.Root.Yaw, baseline.Root.Yaw);
            bool yawChanged = Math.Abs(yawDelta) > settings.Deadzone;
            bool rootChanged = rootXChanged || rootYChanged || rootZChanged || yawChanged;

            HumanoidPoseFlags flags = BuildFlags(lod, changedTrackerMask, isBaseline: false, rootChanged, forceFullResend);

            Span<byte> header = stackalloc byte[6];
            BinaryPrimitives.WriteUInt16LittleEndian(header, entityId);
            BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)flags);

            byte[] bitBuffer = ArrayPool<byte>.Shared.Rent(MaxDeltaBytes);
            Array.Clear(bitBuffer, 0, MaxDeltaBytes);
            int bytesUsed;

            try
            {
                BitWriter writer = new(bitBuffer);

                if (rootChanged)
                {
                    byte rootMask = 0;
                    if (rootXChanged) rootMask |= 1;
                    if (rootYChanged) rootMask |= 1 << 1;
                    if (rootZChanged) rootMask |= 1 << 2;
                    if (yawChanged) rootMask |= 1 << 3;

                    writer.WriteUInt8LSB(rootMask, 4);
                    if (rootXChanged) WriteDeltaValue(writer, current.Root.LocalX - baseline.Root.LocalX, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend);
                    if (rootYChanged) WriteDeltaValue(writer, current.Root.LocalY - baseline.Root.LocalY, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend);
                    if (rootZChanged) WriteDeltaValue(writer, current.Root.LocalZ - baseline.Root.LocalZ, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend);
                    if (yawChanged) WriteDeltaValue(writer, yawDelta, settings.YawSmallThreshold, settings.SmallDeltaBits, forceFullResend, 16);
                }

                for (int i = 0; i < TrackerCount; i++)
                {
                    if (((changedTrackerMask >> i) & 1) == 0)
                        continue;

                    byte axisMask = trackerAxisMask[i];
                    writer.WriteUInt8LSB(axisMask, 3);
                    if ((axisMask & 1) != 0)
                        WriteDeltaValue(writer, trackerDeltaX[i], settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend);
                    if ((axisMask & (1 << 1)) != 0)
                        WriteDeltaValue(writer, trackerDeltaY[i], settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend);
                    if ((axisMask & (1 << 2)) != 0)
                        WriteDeltaValue(writer, trackerDeltaZ[i], settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend);
                }

                bytesUsed = (writer.Position + 7) >> 3;
                if (bytesUsed > MaxDeltaBytes || bytesUsed > ushort.MaxValue)
                    throw new InvalidOperationException("Humanoid delta exceeded reserved buffer size.");
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(bitBuffer);
                throw;
            }

            EnsureCapacity(buffer, header.Length + bytesUsed);
            BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)bytesUsed);
            buffer.AddRange(header.ToArray());
            for (int i = 0; i < bytesUsed; i++)
                buffer.Add(bitBuffer[i]);

            ArrayPool<byte>.Shared.Return(bitBuffer);

            return header.Length + bytesUsed;
        }

        public static bool TryWriteDeltaAvatar(
            Span<byte> destination,
            ushort entityId,
            in FixedQuantizedHumanoidPose current,
            in FixedQuantizedHumanoidPose baseline,
            byte lod,
            out int bytesWritten,
            HumanoidPoseDeltaSettings? deltaSettings = null,
            byte trackerMask = AllTrackersMask,
            bool forceFullResend = false)
        {
            bytesWritten = 0;
            if (destination.Length < 6)
                return false;

            HumanoidPoseDeltaSettings settings = deltaSettings ?? HumanoidPoseDeltaSettings.Default;
            Span<short> trackerDeltaX = stackalloc short[TrackerCount];
            Span<short> trackerDeltaY = stackalloc short[TrackerCount];
            Span<short> trackerDeltaZ = stackalloc short[TrackerCount];
            Span<byte> trackerAxisMask = stackalloc byte[TrackerCount];

            byte changedTrackerMask = 0;
            for (int i = 0; i < TrackerCount; i++)
            {
                if (((trackerMask >> i) & 1) == 0)
                    continue;

                QuantizedTrackerPose source = current[(HumanoidTrackerSlot)i];
                QuantizedTrackerPose baseTracker = baseline[(HumanoidTrackerSlot)i];
                short dx = (short)(source.X - baseTracker.X);
                short dy = (short)(source.Y - baseTracker.Y);
                short dz = (short)(source.Z - baseTracker.Z);

                byte axisMask = 0;
                if (Math.Abs(dx) > settings.Deadzone)
                {
                    axisMask |= 1;
                    trackerDeltaX[i] = dx;
                }

                if (Math.Abs(dy) > settings.Deadzone)
                {
                    axisMask |= 1 << 1;
                    trackerDeltaY[i] = dy;
                }

                if (Math.Abs(dz) > settings.Deadzone)
                {
                    axisMask |= 1 << 2;
                    trackerDeltaZ[i] = dz;
                }

                trackerAxisMask[i] = axisMask;
                if (axisMask != 0)
                    changedTrackerMask |= (byte)(1 << i);
            }

            bool rootXChanged = Math.Abs(current.Root.LocalX - baseline.Root.LocalX) > settings.Deadzone;
            bool rootYChanged = Math.Abs(current.Root.LocalY - baseline.Root.LocalY) > settings.Deadzone;
            bool rootZChanged = Math.Abs(current.Root.LocalZ - baseline.Root.LocalZ) > settings.Deadzone;
            int yawDelta = DeltaYaw(current.Root.Yaw, baseline.Root.Yaw);
            bool yawChanged = Math.Abs(yawDelta) > settings.Deadzone;
            bool rootChanged = rootXChanged || rootYChanged || rootZChanged || yawChanged;

            destination.Clear();
            SpanBitWriter writer = new(destination[6..]);

            if (rootChanged)
            {
                byte rootMask = 0;
                if (rootXChanged) rootMask |= 1;
                if (rootYChanged) rootMask |= 1 << 1;
                if (rootZChanged) rootMask |= 1 << 2;
                if (yawChanged) rootMask |= 1 << 3;

                if (!writer.TryWriteUnsigned(rootMask, 4))
                    return false;
                if (rootXChanged && !WriteDeltaValue(ref writer, current.Root.LocalX - baseline.Root.LocalX, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend))
                    return false;
                if (rootYChanged && !WriteDeltaValue(ref writer, current.Root.LocalY - baseline.Root.LocalY, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend))
                    return false;
                if (rootZChanged && !WriteDeltaValue(ref writer, current.Root.LocalZ - baseline.Root.LocalZ, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend))
                    return false;
                if (yawChanged && !WriteDeltaValue(ref writer, yawDelta, settings.YawSmallThreshold, settings.SmallDeltaBits, forceFullResend, 16))
                    return false;
            }

            for (int i = 0; i < TrackerCount; i++)
            {
                if (((changedTrackerMask >> i) & 1) == 0)
                    continue;

                byte axisMask = trackerAxisMask[i];
                if (!writer.TryWriteUnsigned(axisMask, 3))
                    return false;
                if ((axisMask & 1) != 0 && !WriteDeltaValue(ref writer, trackerDeltaX[i], settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend))
                    return false;
                if ((axisMask & (1 << 1)) != 0 && !WriteDeltaValue(ref writer, trackerDeltaY[i], settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend))
                    return false;
                if ((axisMask & (1 << 2)) != 0 && !WriteDeltaValue(ref writer, trackerDeltaZ[i], settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFullResend))
                    return false;
            }

            int payloadLength = (writer.Position + 7) >> 3;
            if (payloadLength > ushort.MaxValue)
                return false;

            HumanoidPoseFlags flags = BuildFlags(lod, changedTrackerMask, isBaseline: false, rootChanged, forceFullResend);
            BinaryPrimitives.WriteUInt16LittleEndian(destination, entityId);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)flags);
            BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], (ushort)payloadLength);
            bytesWritten = 6 + payloadLength;
            return true;
        }

        public static bool TryReadBaselineAvatar(
            ReadOnlySpan<byte> data,
            out HumanoidPoseAvatarHeader header,
            out QuantizedHumanoidPose pose,
            out int bytesConsumed,
            HumanoidQuantizationSettings? settings = null)
        {
            header = default;
            pose = default;
            bytesConsumed = 0;
            if (data.Length < 6)
                return false;

            ushort entity = BinaryPrimitives.ReadUInt16LittleEndian(data);
            HumanoidPoseFlags flags = (HumanoidPoseFlags)BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
            ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            if (data.Length < 6 + payloadLength || payloadLength < 50)
                return false;

            QuantizedRootPose root = ReadRoot(data[6..]);

            QuantizedTrackerPose[] trackers = new QuantizedTrackerPose[TrackerCount];
            int offset = 18;
            for (int i = 0; i < TrackerCount; i++)
            {
                trackers[i] = ReadTracker(data[offset..]);
                offset += 6;
            }

            ushort sequence = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(6 + payloadLength - 2));
            header = new HumanoidPoseAvatarHeader(entity, flags, sequence);
            pose = new QuantizedHumanoidPose(root, trackers);
            bytesConsumed = 6 + payloadLength;
            _ = settings;
            return true;
        }

        public static bool TryReadDeltaAvatar(
            ReadOnlySpan<byte> data,
            QuantizedHumanoidPose baseline,
            out HumanoidPoseAvatarHeader header,
            out QuantizedHumanoidPose pose,
            out int bytesConsumed,
            HumanoidPoseDeltaSettings? deltaSettings = null)
        {
            header = default;
            pose = default;
            bytesConsumed = 0;

            if (data.Length < 6)
                return false;

            HumanoidPoseDeltaSettings settings = deltaSettings ?? HumanoidPoseDeltaSettings.Default;
            ValidateTrackers(baseline);
            ushort entity = BinaryPrimitives.ReadUInt16LittleEndian(data);
            HumanoidPoseFlags flags = (HumanoidPoseFlags)BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
            ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            if (data.Length < 6 + payloadLength)
                return false;

            header = new HumanoidPoseAvatarHeader(entity, flags, 0);

            BitReader reader = new(data.Slice(6, payloadLength).ToArray());
            QuantizedRootPose root = baseline.Root;
            if (flags.HasFlag(HumanoidPoseFlags.RootChanged))
            {
                uint rootMask;
                try
                {
                    rootMask = reader.ReadUInt8LSB(4);
                }
                catch
                {
                    return false;
                }

                short lx = root.LocalX;
                short ly = root.LocalY;
                short lz = root.LocalZ;
                ushort yaw = root.Yaw;
                short dx = 0, dy = 0, dz = 0;

                if ((rootMask & 1) != 0 && !TryReadDeltaValue(reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFull: flags.HasFlag(HumanoidPoseFlags.ForceResend), out dx))
                    return false;
                if ((rootMask & (1 << 1)) != 0 && !TryReadDeltaValue(reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFull: flags.HasFlag(HumanoidPoseFlags.ForceResend), out dy))
                    return false;
                if ((rootMask & (1 << 2)) != 0 && !TryReadDeltaValue(reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFull: flags.HasFlag(HumanoidPoseFlags.ForceResend), out dz))
                    return false;
                if ((rootMask & (1 << 3)) != 0 && !TryReadYawDelta(reader, root.Yaw, settings.YawSmallThreshold, settings.SmallDeltaBits, flags.HasFlag(HumanoidPoseFlags.ForceResend), out yaw))
                    return false;

                if ((rootMask & 1) != 0) lx = (short)(root.LocalX + dx);
                if ((rootMask & (1 << 1)) != 0) ly = (short)(root.LocalY + dy);
                if ((rootMask & (1 << 2)) != 0) lz = (short)(root.LocalZ + dz);

                root = new QuantizedRootPose(baseline.Root.SectorX, baseline.Root.SectorZ, lx, ly, lz, yaw);
            }

            QuantizedTrackerPose[] trackers = new QuantizedTrackerPose[TrackerCount];
            for (int i = 0; i < TrackerCount; i++)
                trackers[i] = baseline.Trackers[i];

            byte trackerMask = GetTrackerMask(flags);
            for (int i = 0; i < TrackerCount; i++)
            {
                if (((trackerMask >> i) & 1) == 0)
                    continue;

                uint axisMask;
                try
                {
                    axisMask = reader.ReadUInt8LSB(3);
                }
                catch
                {
                    return false;
                }

                short dx = 0;
                short dy = 0;
                short dz = 0;

                if ((axisMask & 1) != 0 && !TryReadDeltaValue(reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, flags.HasFlag(HumanoidPoseFlags.ForceResend), out dx))
                    return false;
                if ((axisMask & (1 << 1)) != 0 && !TryReadDeltaValue(reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, flags.HasFlag(HumanoidPoseFlags.ForceResend), out dy))
                    return false;
                if ((axisMask & (1 << 2)) != 0 && !TryReadDeltaValue(reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, flags.HasFlag(HumanoidPoseFlags.ForceResend), out dz))
                    return false;

                QuantizedTrackerPose source = baseline.Trackers[i];
                trackers[i] = new QuantizedTrackerPose(
                    (short)(source.X + dx),
                    (short)(source.Y + dy),
                    (short)(source.Z + dz));
            }

            pose = new QuantizedHumanoidPose(root, trackers);
            bytesConsumed = 6 + payloadLength;
            return true;
        }

        public static bool TryReadDeltaAvatar(
            ReadOnlySpan<byte> data,
            in FixedQuantizedHumanoidPose baseline,
            out HumanoidPoseAvatarHeader header,
            out FixedQuantizedHumanoidPose pose,
            out int bytesConsumed,
            HumanoidPoseDeltaSettings? deltaSettings = null)
        {
            header = default;
            pose = default;
            bytesConsumed = 0;

            if (data.Length < 6)
                return false;

            HumanoidPoseDeltaSettings settings = deltaSettings ?? HumanoidPoseDeltaSettings.Default;
            ushort entity = BinaryPrimitives.ReadUInt16LittleEndian(data);
            HumanoidPoseFlags flags = (HumanoidPoseFlags)BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
            ushort payloadLength = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            if (data.Length < 6 + payloadLength)
                return false;

            header = new HumanoidPoseAvatarHeader(entity, flags, 0);
            SpanBitReader reader = new(data.Slice(6, payloadLength));
            QuantizedRootPose root = baseline.Root;
            bool forceFull = (flags & HumanoidPoseFlags.ForceResend) != 0;

            if ((flags & HumanoidPoseFlags.RootChanged) != 0)
            {
                if (!reader.TryReadUnsigned(4, out uint rootMask))
                    return false;

                short lx = root.LocalX;
                short ly = root.LocalY;
                short lz = root.LocalZ;
                ushort yaw = root.Yaw;
                short dx = 0, dy = 0, dz = 0;

                if ((rootMask & 1) != 0 && !TryReadDeltaValue(ref reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFull, out dx))
                    return false;
                if ((rootMask & (1 << 1)) != 0 && !TryReadDeltaValue(ref reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFull, out dy))
                    return false;
                if ((rootMask & (1 << 2)) != 0 && !TryReadDeltaValue(ref reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFull, out dz))
                    return false;
                if ((rootMask & (1 << 3)) != 0 && !TryReadYawDelta(ref reader, root.Yaw, settings.YawSmallThreshold, settings.SmallDeltaBits, forceFull, out yaw))
                    return false;

                if ((rootMask & 1) != 0) lx = (short)(root.LocalX + dx);
                if ((rootMask & (1 << 1)) != 0) ly = (short)(root.LocalY + dy);
                if ((rootMask & (1 << 2)) != 0) lz = (short)(root.LocalZ + dz);
                root = new QuantizedRootPose(baseline.Root.SectorX, baseline.Root.SectorZ, lx, ly, lz, yaw);
            }

            Span<QuantizedTrackerPose> trackers = stackalloc QuantizedTrackerPose[TrackerCount];
            trackers[0] = baseline.Hip;
            trackers[1] = baseline.Head;
            trackers[2] = baseline.LeftHand;
            trackers[3] = baseline.RightHand;
            trackers[4] = baseline.LeftFoot;
            trackers[5] = baseline.RightFoot;

            byte changedTrackerMask = GetTrackerMask(flags);
            for (int i = 0; i < TrackerCount; i++)
            {
                if (((changedTrackerMask >> i) & 1) == 0)
                    continue;

                if (!reader.TryReadUnsigned(3, out uint axisMask))
                    return false;

                short dx = 0;
                short dy = 0;
                short dz = 0;

                if ((axisMask & 1) != 0 && !TryReadDeltaValue(ref reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFull, out dx))
                    return false;
                if ((axisMask & (1 << 1)) != 0 && !TryReadDeltaValue(ref reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFull, out dy))
                    return false;
                if ((axisMask & (1 << 2)) != 0 && !TryReadDeltaValue(ref reader, settings.PositionSmallThreshold, settings.SmallDeltaBits, forceFull, out dz))
                    return false;

                QuantizedTrackerPose source = trackers[i];
                trackers[i] = new QuantizedTrackerPose(
                    (short)(source.X + dx),
                    (short)(source.Y + dy),
                    (short)(source.Z + dz));
            }

            pose = new FixedQuantizedHumanoidPose(root, trackers[0], trackers[1], trackers[2], trackers[3], trackers[4], trackers[5]);
            bytesConsumed = 6 + payloadLength;
            return true;
        }

        public static int GetDeltaSizeBytes(
            QuantizedHumanoidPose current,
            QuantizedHumanoidPose baseline,
            HumanoidPoseDeltaSettings? deltaSettings = null,
            byte trackerMask = AllTrackersMask,
            bool forceFullResend = false)
        {
            List<byte> scratch = new();
            WriteDeltaAvatar(scratch, 0, current, baseline, 0, deltaSettings, trackerMask, forceFullResend);
            return scratch.Count;
        }

        public static int GetDeltaSizeBytes(
            in FixedQuantizedHumanoidPose current,
            in FixedQuantizedHumanoidPose baseline,
            HumanoidPoseDeltaSettings? deltaSettings = null,
            byte trackerMask = AllTrackersMask,
            bool forceFullResend = false)
        {
            Span<byte> scratch = stackalloc byte[MaxDeltaBytes + 6];
            return TryWriteDeltaAvatar(scratch, 0, current, baseline, 0, out int bytesWritten, deltaSettings, trackerMask, forceFullResend)
                ? bytesWritten
                : -1;
        }

        private static void ValidateTrackers(QuantizedHumanoidPose pose)
        {
            if (pose.Trackers is null || pose.Trackers.Length != TrackerCount)
                throw new ArgumentException($"Quantized pose must include exactly {TrackerCount} tracker positions.", nameof(pose));
        }

        private static void WriteRoot(Span<byte> span, QuantizedRootPose root)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span, root.SectorX);
            BinaryPrimitives.WriteInt16LittleEndian(span[2..], root.SectorZ);
            BinaryPrimitives.WriteInt16LittleEndian(span[4..], root.LocalX);
            BinaryPrimitives.WriteInt16LittleEndian(span[6..], root.LocalY);
            BinaryPrimitives.WriteInt16LittleEndian(span[8..], root.LocalZ);
            BinaryPrimitives.WriteUInt16LittleEndian(span[10..], root.Yaw);
        }

        private static void WriteTracker(Span<byte> span, QuantizedTrackerPose tracker)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span, tracker.X);
            BinaryPrimitives.WriteInt16LittleEndian(span[2..], tracker.Y);
            BinaryPrimitives.WriteInt16LittleEndian(span[4..], tracker.Z);
        }

        private static QuantizedRootPose ReadRoot(ReadOnlySpan<byte> data)
        {
            short sectorX = BinaryPrimitives.ReadInt16LittleEndian(data);
            short sectorZ = BinaryPrimitives.ReadInt16LittleEndian(data[2..]);
            short localX = BinaryPrimitives.ReadInt16LittleEndian(data[4..]);
            short localY = BinaryPrimitives.ReadInt16LittleEndian(data[6..]);
            short localZ = BinaryPrimitives.ReadInt16LittleEndian(data[8..]);
            ushort yaw = BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
            return new QuantizedRootPose(sectorX, sectorZ, localX, localY, localZ, yaw);
        }

        private static QuantizedTrackerPose ReadTracker(ReadOnlySpan<byte> data)
        {
            short x = BinaryPrimitives.ReadInt16LittleEndian(data);
            short y = BinaryPrimitives.ReadInt16LittleEndian(data[2..]);
            short z = BinaryPrimitives.ReadInt16LittleEndian(data[4..]);
            return new QuantizedTrackerPose(x, y, z);
        }

        private static short QuantizeSigned(float value, float halfRange)
        {
            float clamped = Math.Clamp(value, -halfRange, halfRange);
            return (short)MathF.Round(clamped / halfRange * short.MaxValue);
        }

        private static Vector3 DequantizeTracker(QuantizedTrackerPose tracker, float range)
            => new(
                DequantizeSigned(tracker.X, range),
                DequantizeSigned(tracker.Y, range),
                DequantizeSigned(tracker.Z, range));

        private static QuantizedTrackerPose QuantizeTracker(Vector3 local, float range)
            => new(
                QuantizeSigned(local.X, range),
                QuantizeSigned(local.Y, range),
                QuantizeSigned(local.Z, range));

        private static float DequantizeSector(short sector, short local, float tileSize, float halfTile)
            => (sector + 0.5f) * tileSize + DequantizeSigned(local, halfTile);

        private static float DequantizeSigned(short value, float halfRange)
            => value / (float)short.MaxValue * halfRange;

        private static ushort QuantizeYaw(float yawRadians)
        {
            float normalized = NormalizeAngle(yawRadians);
            return (ushort)Math.Clamp(MathF.Round(normalized / (MathF.PI * 2f) * ushort.MaxValue), ushort.MinValue, ushort.MaxValue);
        }

        private static float DequantizeYaw(ushort yaw)
            => yaw / (float)ushort.MaxValue * MathF.PI * 2f;

        private static float NormalizeAngle(float radians)
        {
            float wrapped = radians % (MathF.PI * 2f);
            if (wrapped < 0)
                wrapped += MathF.PI * 2f;
            return wrapped;
        }

        private static int DeltaYaw(ushort current, ushort baseline)
        {
            int diff = current - baseline;
            if (diff > short.MaxValue)
                diff -= ushort.MaxValue + 1;
            else if (diff < short.MinValue)
                diff += ushort.MaxValue + 1;
            return diff;
        }

        private static ushort ApplyYawDelta(ushort baseline, int delta)
        {
            int value = baseline + delta;
            value %= ushort.MaxValue + 1;
            if (value < 0)
                value += ushort.MaxValue + 1;
            return (ushort)value;
        }

        private static void WriteDeltaValue(BitWriter writer, int delta, short smallThreshold, int smallBits, bool forceFull, int fullBits = 16)
        {
            bool useSmall = !forceFull && Math.Abs(delta) <= smallThreshold;
            writer.WriteUInt8LSB(useSmall ? (byte)1 : (byte)0, 1);
            writer.WriteInt16LSB((short)delta, useSmall ? smallBits : fullBits);
        }

        private static bool WriteDeltaValue(ref SpanBitWriter writer, int delta, short smallThreshold, int smallBits, bool forceFull, int fullBits = 16)
        {
            bool useSmall = !forceFull && Math.Abs(delta) <= smallThreshold;
            return writer.TryWriteUnsigned(useSmall ? 1u : 0u, 1)
                && writer.TryWriteSigned(delta, useSmall ? smallBits : fullBits);
        }

        private static bool TryReadDeltaValue(BitReader reader, short smallThreshold, int smallBits, bool forceFull, out short result)
        {
            result = 0;
            uint smallFlag;
            int delta;
            try
            {
                smallFlag = reader.ReadUInt8LSB(1);
                int bits = (smallFlag == 1 && !forceFull) ? smallBits : 16;
                delta = reader.ReadInt16LSB(bits);
            }
            catch
            {
                return false;
            }

            result = (short)delta;
            return Math.Abs(delta) <= short.MaxValue;
        }

        private static bool TryReadDeltaValue(ref SpanBitReader reader, short smallThreshold, int smallBits, bool forceFull, out short result)
        {
            result = 0;
            if (!reader.TryReadUnsigned(1, out uint smallFlag))
                return false;

            int bits = (smallFlag == 1 && !forceFull) ? smallBits : 16;
            if (!reader.TryReadSigned(bits, out int delta))
                return false;

            result = (short)delta;
            return Math.Abs(delta) <= short.MaxValue;
        }

        private static bool TryReadYawDelta(BitReader reader, ushort baseline, short smallThreshold, int smallBits, bool forceFull, out ushort yaw)
        {
            yaw = baseline;
            uint smallFlag;
            int delta;
            try
            {
                smallFlag = reader.ReadUInt8LSB(1);
                int bits = (smallFlag == 1 && !forceFull) ? smallBits : 16;
                delta = reader.ReadInt16LSB(bits);
            }
            catch
            {
                return false;
            }

            if (Math.Abs(delta) > smallThreshold && smallFlag == 1 && !forceFull)
                return false;

            yaw = ApplyYawDelta(baseline, delta);
            return true;
        }

        private static bool TryReadYawDelta(ref SpanBitReader reader, ushort baseline, short smallThreshold, int smallBits, bool forceFull, out ushort yaw)
        {
            yaw = baseline;
            if (!reader.TryReadUnsigned(1, out uint smallFlag))
                return false;

            int bits = (smallFlag == 1 && !forceFull) ? smallBits : 16;
            if (!reader.TryReadSigned(bits, out int delta))
                return false;

            if (Math.Abs(delta) > smallThreshold && smallFlag == 1 && !forceFull)
                return false;

            yaw = ApplyYawDelta(baseline, delta);
            return true;
        }

        private static void EnsureCapacity(List<byte> buffer, int additional)
        {
            if (buffer.Capacity < buffer.Count + additional)
                buffer.Capacity = buffer.Count + additional;
        }

        private ref struct SpanBitWriter
        {
            private Span<byte> _buffer;

            public SpanBitWriter(Span<byte> buffer)
            {
                _buffer = buffer;
                Position = 0;
            }

            public int Position { get; private set; }

            public bool TryWriteUnsigned(uint value, int bitCount)
            {
                if ((uint)bitCount > 31u)
                    return false;

                for (int i = 0; i < bitCount; i++)
                {
                    int byteIndex = Position >> 3;
                    if ((uint)byteIndex >= (uint)_buffer.Length)
                        return false;

                    if (((value >> i) & 1u) != 0u)
                        _buffer[byteIndex] |= (byte)(1 << (Position & 7));

                    Position++;
                }

                return true;
            }

            public bool TryWriteSigned(int value, int bitCount)
            {
                if ((uint)bitCount > 31u || bitCount <= 0)
                    return false;

                uint mask = (1u << bitCount) - 1u;
                return TryWriteUnsigned((uint)value & mask, bitCount);
            }
        }

        private ref struct SpanBitReader
        {
            private ReadOnlySpan<byte> _buffer;
            private readonly int _bitLength;

            public SpanBitReader(ReadOnlySpan<byte> buffer)
            {
                _buffer = buffer;
                _bitLength = buffer.Length * 8;
                Position = 0;
            }

            public int Position { get; private set; }

            public bool TryReadUnsigned(int bitCount, out uint value)
            {
                value = 0u;
                if ((uint)bitCount > 31u || Position + bitCount > _bitLength)
                    return false;

                for (int i = 0; i < bitCount; i++)
                {
                    int byteIndex = Position >> 3;
                    uint bit = (uint)((_buffer[byteIndex] >> (Position & 7)) & 1);
                    value |= bit << i;
                    Position++;
                }

                return true;
            }

            public bool TryReadSigned(int bitCount, out int value)
            {
                value = 0;
                if (bitCount <= 0 || !TryReadUnsigned(bitCount, out uint raw))
                    return false;

                int signed = (int)raw;
                int signBit = 1 << (bitCount - 1);
                if ((signed & signBit) != 0)
                    signed |= -1 << bitCount;

                value = signed;
                return true;
            }
        }
    }

    public sealed class HumanoidPosePacketBuilder
    {
        private readonly HumanoidQuantizationSettings _quantSettings;
        private readonly HumanoidPoseDeltaSettings _deltaSettings;
        private readonly List<byte> _buffer = new();
        private bool _building;

        public HumanoidPosePacketBuilder(
            HumanoidQuantizationSettings? quantSettings = null,
            HumanoidPoseDeltaSettings? deltaSettings = null)
        {
            _quantSettings = quantSettings ?? HumanoidQuantizationSettings.Default;
            _deltaSettings = deltaSettings ?? HumanoidPoseDeltaSettings.Default;
        }

        public HumanoidPosePacketKind Kind { get; private set; }
        public ushort BaselineSequence { get; private set; }
        public int AvatarCount { get; private set; }

        public void BeginFrame(HumanoidPosePacketKind kind, ushort baselineSequence)
        {
            _buffer.Clear();
            AvatarCount = 0;
            Kind = kind;
            BaselineSequence = baselineSequence;
            _building = true;
        }

        public void AddBaselineAvatar(ushort entityId, HumanoidPoseSample sample, byte lod = 0, byte trackerMask = HumanoidPoseCodec.AllTrackersMask)
            => AddBaselineAvatar(entityId, HumanoidPoseCodec.Quantize(sample, _quantSettings), lod, trackerMask);

        public void AddBaselineAvatar(ushort entityId, QuantizedHumanoidPose pose, byte lod = 0, byte trackerMask = HumanoidPoseCodec.AllTrackersMask)
        {
            EnsureBuilding(HumanoidPosePacketKind.Baseline);
            HumanoidPoseCodec.WriteBaselineAvatar(_buffer, entityId, pose, lod, trackerMask, BaselineSequence);
            AvatarCount++;
        }

        public void AddDeltaAvatar(
            ushort entityId,
            QuantizedHumanoidPose current,
            QuantizedHumanoidPose baseline,
            byte lod = 0,
            byte trackerMask = HumanoidPoseCodec.AllTrackersMask,
            bool forceFullResend = false)
        {
            EnsureBuilding(HumanoidPosePacketKind.Delta);
            HumanoidPoseCodec.WriteDeltaAvatar(_buffer, entityId, current, baseline, lod, _deltaSettings, trackerMask, forceFullResend);
            AvatarCount++;
        }

        public HumanoidPoseFrame BuildFrame()
        {
            if (!_building)
                throw new InvalidOperationException("BeginFrame must be called before building a humanoid pose frame.");

            HumanoidPoseFrame frame = new()
            {
                Kind = Kind,
                BaselineSequence = BaselineSequence,
                AvatarCount = AvatarCount,
                Payload = _buffer.ToArray()
            };

            _building = false;
            return frame;
        }

        private void EnsureBuilding(HumanoidPosePacketKind kind)
        {
            if (!_building || Kind != kind)
                throw new InvalidOperationException($"BeginFrame must be called with kind '{kind}' before adding avatars.");
        }
    }

    public ref struct HumanoidPoseSpanPacketWriter
    {
        private Span<byte> _destination;
        private readonly HumanoidQuantizationSettings _quantSettings;
        private readonly HumanoidPoseDeltaSettings _deltaSettings;
        private int _bytesWritten;
        private bool _building;

        public HumanoidPoseSpanPacketWriter(
            Span<byte> destination,
            HumanoidQuantizationSettings? quantSettings = null,
            HumanoidPoseDeltaSettings? deltaSettings = null)
        {
            _destination = destination;
            _quantSettings = quantSettings ?? HumanoidQuantizationSettings.Default;
            _deltaSettings = deltaSettings ?? HumanoidPoseDeltaSettings.Default;
            _bytesWritten = 0;
            _building = false;
            Kind = default;
            BaselineSequence = 0;
            AvatarCount = 0;
        }

        public HumanoidPosePacketKind Kind { get; private set; }
        public ushort BaselineSequence { get; private set; }
        public int AvatarCount { get; private set; }
        public int BytesWritten => _bytesWritten;
        public ReadOnlySpan<byte> Payload => _destination.Slice(0, _bytesWritten);

        public void BeginFrame(HumanoidPosePacketKind kind, ushort baselineSequence)
        {
            _bytesWritten = 0;
            AvatarCount = 0;
            Kind = kind;
            BaselineSequence = baselineSequence;
            _building = true;
        }

        public bool TryAddBaselineAvatar(ushort entityId, HumanoidPoseSample sample, byte lod = 0, byte trackerMask = HumanoidPoseCodec.AllTrackersMask)
            => TryAddBaselineAvatar(entityId, HumanoidPoseCodec.QuantizeFixed(sample, _quantSettings), lod, trackerMask);

        public bool TryAddBaselineAvatar(ushort entityId, in FixedQuantizedHumanoidPose pose, byte lod = 0, byte trackerMask = HumanoidPoseCodec.AllTrackersMask)
        {
            EnsureBuilding(HumanoidPosePacketKind.Baseline);
            if (!HumanoidPoseCodec.TryWriteBaselineAvatar(_destination[_bytesWritten..], entityId, pose, lod, out int written, trackerMask, BaselineSequence))
                return false;

            _bytesWritten += written;
            AvatarCount++;
            return true;
        }

        public bool TryAddDeltaAvatar(
            ushort entityId,
            in FixedQuantizedHumanoidPose current,
            in FixedQuantizedHumanoidPose baseline,
            byte lod = 0,
            byte trackerMask = HumanoidPoseCodec.AllTrackersMask,
            bool forceFullResend = false)
        {
            EnsureBuilding(HumanoidPosePacketKind.Delta);
            if (!HumanoidPoseCodec.TryWriteDeltaAvatar(
                _destination[_bytesWritten..],
                entityId,
                current,
                baseline,
                lod,
                out int written,
                _deltaSettings,
                trackerMask,
                forceFullResend))
            {
                return false;
            }

            _bytesWritten += written;
            AvatarCount++;
            return true;
        }

        private readonly void EnsureBuilding(HumanoidPosePacketKind kind)
        {
            if (!_building || Kind != kind)
                throw new InvalidOperationException($"BeginFrame must be called with kind '{kind}' before adding avatars.");
        }
    }

    public ref struct HumanoidPosePacketCursor
    {
        private ReadOnlySpan<byte> _payload;
        private int _offset;

        public HumanoidPosePacketCursor(ReadOnlySpan<byte> payload)
        {
            _payload = payload;
            _offset = 0;
        }

        public int BytesConsumed => _offset;
        public bool HasRemaining => _offset < _payload.Length;

        public bool TryReadNextBaseline(out HumanoidPoseAvatarHeader header, out FixedQuantizedHumanoidPose pose)
        {
            if (!HumanoidPoseCodec.TryReadBaselineAvatarFixed(_payload[_offset..], out header, out pose, out int consumed))
                return false;

            _offset += consumed;
            return true;
        }

        public bool TryReadNextDelta(
            in FixedQuantizedHumanoidPose baseline,
            out HumanoidPoseAvatarHeader header,
            out FixedQuantizedHumanoidPose pose,
            HumanoidPoseDeltaSettings? deltaSettings = null)
        {
            if (!HumanoidPoseCodec.TryReadDeltaAvatar(_payload[_offset..], baseline, out header, out pose, out int consumed, deltaSettings))
                return false;

            _offset += consumed;
            return true;
        }
    }
}

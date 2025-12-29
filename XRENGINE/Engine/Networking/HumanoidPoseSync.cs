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

        private static void EnsureCapacity(List<byte> buffer, int additional)
        {
            if (buffer.Capacity < buffer.Count + additional)
                buffer.Capacity = buffer.Count + additional;
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

}

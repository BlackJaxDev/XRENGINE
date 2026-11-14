using System.Numerics;
using System.Threading;
using XREngine.Data.Geometry;

namespace XREngine.Scene;

public partial class VisualScene3D
{
    /// <summary>
    /// Capacity of the shadow caster change ring. One record holds the world box that a
    /// shadow caster covered before and after one change. An animated avatar records about
    /// 100 skinned sub-meshes per pose, so 4096 records hold about 40 such movers per plan
    /// frame. When writers lap the reader, the next drain reports an overflow, and the
    /// lights then treat every cascade as changed for that frame.
    /// </summary>
    private const int ShadowCasterChangeCapacity = 4096;

    private readonly Vector3[] _shadowCasterChangeMin = new Vector3[ShadowCasterChangeCapacity];
    private readonly Vector3[] _shadowCasterChangeMax = new Vector3[ShadowCasterChangeCapacity];
    // Sequence number + 1 of the record that a slot holds; 0 for an empty slot. A writer
    // stores it after the box, so a reader that sees the expected stamp sees the box.
    private readonly long[] _shadowCasterChangeStamps = new long[ShadowCasterChangeCapacity];
    private long _shadowCasterChangeWriteIndex;

    /// <summary>
    /// Records that a shadow caster changed inside the union of <paramref name="before"/>
    /// and <paramref name="after"/>: it moved, its deformation produced a new pose, or its
    /// cast-shadow flag changed. Any thread can call it; it takes no lock and allocates
    /// nothing. The directional cascades that the box meets re-render in the next plan
    /// frame.
    /// </summary>
    public void RecordShadowCasterChange(in Box? before, in Box? after)
    {
        if (!before.HasValue && !after.HasValue)
            return;

        Vector3 min;
        Vector3 max;
        if (before.HasValue && after.HasValue)
        {
            min = Vector3.Min(before.Value.WorldMinimum, after.Value.WorldMinimum);
            max = Vector3.Max(before.Value.WorldMaximum, after.Value.WorldMaximum);
        }
        else
        {
            Box box = before ?? after!.Value;
            min = box.WorldMinimum;
            max = box.WorldMaximum;
        }

        long sequence = Interlocked.Increment(ref _shadowCasterChangeWriteIndex) - 1;
        int slot = (int)(sequence % ShadowCasterChangeCapacity);
        _shadowCasterChangeMin[slot] = min;
        _shadowCasterChangeMax[slot] = max;
        Volatile.Write(ref _shadowCasterChangeStamps[slot], sequence + 1);
    }

    /// <summary>
    /// Copies the shadow caster changes recorded since <paramref name="readIndex"/> into
    /// <paramref name="destination"/> and advances the read index. The caller owns the read
    /// index, so each consumer reads every record once. A record whose writer has not
    /// finished stays for the next drain, so no record is lost. Returns the number of
    /// copied records. <paramref name="overflowed"/> is true when records were lost to a
    /// lapped ring or did not fit in <paramref name="destination"/>; the caller then treats
    /// every cascade as changed.
    /// </summary>
    internal int DrainShadowCasterChanges(Span<AABB> destination, ref long readIndex, out bool overflowed)
    {
        overflowed = false;
        long write = Volatile.Read(ref _shadowCasterChangeWriteIndex);
        if (write - readIndex > ShadowCasterChangeCapacity)
        {
            overflowed = true;
            readIndex = write - ShadowCasterChangeCapacity;
        }

        int count = 0;
        while (readIndex < write)
        {
            int slot = (int)(readIndex % ShadowCasterChangeCapacity);
            long stamp = Volatile.Read(ref _shadowCasterChangeStamps[slot]);
            if (stamp < readIndex + 1)
                break; // The writer of this record has not finished; read it next time.
            if (stamp > readIndex + 1)
            {
                // A writer lapped this slot before it was read.
                overflowed = true;
                readIndex++;
                continue;
            }

            if (count >= destination.Length)
            {
                overflowed = true;
                readIndex = write;
                break;
            }

            destination[count++] = new AABB(_shadowCasterChangeMin[slot], _shadowCasterChangeMax[slot]);
            // A writer that lapped the slot during the copy can tear the box.
            if (Volatile.Read(ref _shadowCasterChangeStamps[slot]) != readIndex + 1)
                overflowed = true;
            readIndex++;
        }

        return count;
    }
}

namespace XREngine.Cutscenes;

/// <summary>Finds the key segment of a sorted key time array. It keeps no state and does not allocate.</summary>
internal static class CutsceneCurveKeys
{
    /// <summary>
    /// Finds the segment that holds a time.
    /// </summary>
    /// <param name="times">The sorted key times.</param>
    /// <param name="keyCount">The number of usable keys. It must not be more than the length of <paramref name="times"/>.</param>
    /// <param name="time">The time to find.</param>
    /// <param name="interpolate">True to blend inside a segment. False to hold the earlier key.</param>
    /// <param name="index">The index of the earlier key of the segment.</param>
    /// <param name="alpha">The blend amount from the earlier key to the key after it. It is zero outside the key range.</param>
    public static void Locate(CutsceneTime[] times, int keyCount, CutsceneTime time, bool interpolate, out int index, out float alpha)
    {
        alpha = 0f;
        long flicks = time.Flicks;
        if (flicks <= times[0].Flicks)
        {
            index = 0;
            return;
        }

        int last = keyCount - 1;
        if (flicks >= times[last].Flicks)
        {
            index = last;
            return;
        }

        // Binary search for the last key with a time less than or equal to the sample time.
        int low = 0;
        int high = last;
        while (high - low > 1)
        {
            int mid = low + ((high - low) >> 1);
            if (times[mid].Flicks <= flicks)
                low = mid;
            else
                high = mid;
        }

        index = low;
        if (!interpolate)
            return;

        long start = times[low].Flicks;
        long span = times[low + 1].Flicks - start;
        if (span > 0)
            alpha = (float)((double)(flicks - start) / span);
    }
}

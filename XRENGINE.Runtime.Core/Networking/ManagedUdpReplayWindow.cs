namespace XREngine.Networking;

/// <summary>Fixed 1,024-packet anti-replay window. Call <see cref="CanAccept"/> before MAC success and <see cref="Commit"/> only after structural validation.</summary>
public sealed class ManagedUdpReplayWindow
{
    private readonly ulong[] _seen = new ulong[16];
    private ulong _highest;

    public bool CanAccept(ulong counter)
    {
        if (counter == 0)
            return false;
        if (_highest == 0 || counter > _highest)
            return true;
        ulong distance = _highest - counter;
        return distance < 1024 && (_seen[distance / 64] & (1UL << (int)(distance % 64))) == 0;
    }

    public bool Commit(ulong counter)
    {
        if (!CanAccept(counter))
            return false;

        if (_highest == 0)
        {
            _highest = counter;
            _seen[0] = 1;
            return true;
        }

        if (counter > _highest)
        {
            ulong shift = counter - _highest;
            if (shift >= 1024)
                Array.Clear(_seen);
            else
                ShiftLeft((int)shift);
            _highest = counter;
            _seen[0] |= 1;
            return true;
        }

        ulong distance = _highest - counter;
        _seen[distance / 64] |= 1UL << (int)(distance % 64);
        return true;
    }

    private void ShiftLeft(int count)
    {
        int words = count / 64;
        int bits = count % 64;
        for (int index = _seen.Length - 1; index >= 0; --index)
        {
            ulong value = index - words >= 0 ? _seen[index - words] << bits : 0UL;
            if (bits != 0 && index - words - 1 >= 0)
                value |= _seen[index - words - 1] >> (64 - bits);
            _seen[index] = value;
        }
    }
}

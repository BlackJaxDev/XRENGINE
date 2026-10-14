using System.Diagnostics;

namespace XREngine.Scene.Transforms;

public sealed partial class TransformHierarchyStore
{
    /// <summary>
    /// The outermost measured site that holds the pass gate, plus 1. The value 0 means that the gate is free,
    /// or that an unmeasured site holds it. Only the thread that holds the gate writes this field.
    /// Other threads read it only to find which site blocked them.
    /// </summary>
    private int _passGateHolder;

    /// <summary>
    /// Enters the pass gate and records the entry for <paramref name="site"/>.
    /// The caller must call this method inside a try block, with <paramref name="taken"/> set to false,
    /// and call <see cref="ExitPassGate"/> in the matching finally block.
    /// </summary>
    /// <remarks>
    /// The method first tries to enter with no wait, which is the same fast path as a lock statement and
    /// also accepts a reentrant entry. Only when that try fails, it reads two timestamps around a blocking enter.
    /// Thus an uncontended entry reads no timestamp.
    /// </remarks>
    /// <returns>True when this entry became the recorded holder and must clear it on exit.</returns>
    private bool EnterPassGate(ETransformHierarchyPassGateSite site, ref bool taken)
    {
        Monitor.TryEnter(_passGate, ref taken);
        if (!taken)
        {
            // The holder read is not synchronized with the holder. It is only for attribution.
            int holder = Volatile.Read(ref _passGateHolder);
            long waitStarted = Stopwatch.GetTimestamp();
            Monitor.Enter(_passGate, ref taken);
            long waitEnded = Stopwatch.GetTimestamp();
            TransformHierarchyPassGateTelemetry.RecordWait(site, holder, waitEnded, waitEnded - waitStarted);
        }

        TransformHierarchyPassGateTelemetry.CountEntry(site);

        // A reentrant entry keeps the outer holder.
        if (_passGateHolder != 0)
            return false;

        Volatile.Write(ref _passGateHolder, (int)site + 1);
        return true;
    }

    /// <summary>
    /// Releases the pass gate when <paramref name="taken"/> is true, and clears the recorded holder first
    /// when this entry set it.
    /// </summary>
    private void ExitPassGate(bool taken, bool markedHolder)
    {
        if (!taken)
            return;

        if (markedHolder)
            Volatile.Write(ref _passGateHolder, 0);
        Monitor.Exit(_passGate);
    }
}

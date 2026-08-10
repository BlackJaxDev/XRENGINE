using System.Collections;
using System.Threading;

namespace XREngine.Rendering;

/// <summary>
/// Copy-on-write collection of typed binding publishers. Mutation is expected
/// outside render consumption; readers receive one immutable array snapshot.
/// </summary>
public sealed class RenderBindingPublisherCollection :
    IReadOnlyList<IRenderBindingPublisher>
{
    private readonly object _mutationLock = new();
    private IRenderBindingPublisher[] _snapshot = [];

    /// <inheritdoc/>
    public int Count => Volatile.Read(ref _snapshot).Length;

    /// <inheritdoc/>
    public IRenderBindingPublisher this[int index]
        => Volatile.Read(ref _snapshot)[index];

    /// <summary>
    /// Adds a publisher if the same instance is not already registered.
    /// </summary>
    public void Add(IRenderBindingPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        lock (_mutationLock)
        {
            IRenderBindingPublisher[] current = _snapshot;
            for (int index = 0; index < current.Length; index++)
            {
                if (ReferenceEquals(current[index], publisher))
                    return;
            }

            IRenderBindingPublisher[] replacement =
                new IRenderBindingPublisher[current.Length + 1];
            current.CopyTo(replacement, 0);
            replacement[^1] = publisher;
            Volatile.Write(ref _snapshot, replacement);
        }
    }

    /// <summary>
    /// Removes a previously registered publisher.
    /// </summary>
    public bool Remove(IRenderBindingPublisher publisher)
    {
        ArgumentNullException.ThrowIfNull(publisher);

        lock (_mutationLock)
        {
            IRenderBindingPublisher[] current = _snapshot;
            int matchIndex = -1;
            for (int index = 0; index < current.Length; index++)
            {
                if (!ReferenceEquals(current[index], publisher))
                    continue;

                matchIndex = index;
                break;
            }

            if (matchIndex < 0)
                return false;

            if (current.Length == 1)
            {
                Volatile.Write(ref _snapshot, []);
                return true;
            }

            IRenderBindingPublisher[] replacement =
                new IRenderBindingPublisher[current.Length - 1];
            if (matchIndex > 0)
                Array.Copy(current, 0, replacement, 0, matchIndex);
            if (matchIndex < replacement.Length)
            {
                Array.Copy(
                    current,
                    matchIndex + 1,
                    replacement,
                    matchIndex,
                    replacement.Length - matchIndex);
            }

            Volatile.Write(ref _snapshot, replacement);
            return true;
        }
    }

    /// <summary>
    /// Removes every publisher.
    /// </summary>
    public void Clear()
    {
        lock (_mutationLock)
            Volatile.Write(ref _snapshot, []);
    }

    internal IRenderBindingPublisher[] CaptureSnapshot()
        => Volatile.Read(ref _snapshot);

    /// <summary>
    /// Captures the sole publisher whose producer scope must survive deferred
    /// backend consumption. Collections with more than one such publisher are
    /// rejected because a render request intentionally carries one bounded,
    /// allocation-free publication handle.
    /// </summary>
    internal DeferredRenderBindingPublication CaptureDeferredPublication()
    {
        IRenderBindingPublisher[] publishers = Volatile.Read(ref _snapshot);
        IDeferredRenderBindingPublisher? deferredPublisher = null;
        for (int index = 0; index < publishers.Length; index++)
        {
            if (publishers[index] is not IDeferredRenderBindingPublisher candidate)
                continue;

            if (deferredPublisher is not null)
            {
                throw new InvalidOperationException(
                    "A render binding publisher collection may contain only one deferred-scope publisher.");
            }

            deferredPublisher = candidate;
        }

        return deferredPublisher is null
            ? default
            : new DeferredRenderBindingPublication(
                deferredPublisher,
                deferredPublisher.CaptureDeferredPublication());
    }

    /// <inheritdoc/>
    public IEnumerator<IRenderBindingPublisher> GetEnumerator()
        => ((IEnumerable<IRenderBindingPublisher>)
            Volatile.Read(ref _snapshot)).GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator()
        => GetEnumerator();
}

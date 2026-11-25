namespace System.Collections.Generic;

/// <summary>Keeps structural endpoints reserved through list callbacks without holding a lock.</summary>
internal readonly struct EventListAuthorityLease<T> : IDisposable
{
    private readonly EventListMutationAuthority<T> _authority;
    private readonly bool _owner;
    private readonly T _item;
    private readonly IReadOnlyList<T>? _items;

    internal EventListAuthorityLease(EventListMutationAuthority<T> authority, EventList<T> list, bool removal = false)
    {
        _authority = authority;
        _owner = true;
        _item = default!;
        _items = null;
        lock (authority.MutationGate)
            if (removal) authority.EnterRemoval(list);
            else authority.EnterMutation(list);
    }

    internal EventListAuthorityLease(EventListMutationAuthority<T> authority, T item, bool adding)
    {
        _authority = authority;
        _owner = false;
        _item = item;
        _items = null;
        lock (authority.MutationGate)
            authority.ReserveItem(item, adding);
    }

    internal EventListAuthorityLease(EventListMutationAuthority<T> authority, IReadOnlyList<T> items, bool adding)
    {
        _authority = authority;
        _owner = false;
        _item = default!;
        _items = items;
        lock (authority.MutationGate)
        {
            int reserved = 0;
            try
            {
                for (; reserved < items.Count; reserved++)
                    authority.ReserveItem(items[reserved], adding);
            }
            catch
            {
                while (reserved > 0)
                    authority.ReleaseItem(items[--reserved]);
                throw;
            }
        }
    }

    public void Dispose()
    {
        lock (_authority.MutationGate)
        {
            if (_owner)
                _authority.ExitMutation();
            else if (_items is not null)
                for (int index = _items.Count - 1; index >= 0; index--)
                    _authority.ReleaseItem(_items[index]);
            else
                _authority.ReleaseItem(_item);
        }
    }
}

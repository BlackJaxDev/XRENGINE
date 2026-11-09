namespace XREngine.Editor.Mcp;

/// <summary>Bounds live texture restore receipts for one runtime world.</summary>
internal sealed class EditorMcpLiveTextureReceiptStore
{
    private const int MaxReceipts = 32;
    private readonly Dictionary<Guid, EditorMcpLiveTextureReceipt> _receipts = [];
    private readonly List<Guid> _order = [];

    public Guid Add(EditorMcpLiveTextureReceipt receipt)
    {
        Guid id;
        do { id = Guid.NewGuid(); } while (_receipts.ContainsKey(id));
        _receipts.Add(id, receipt);
        _order.Add(id);
        while (_receipts.Count > MaxReceipts)
        {
            _receipts.Remove(_order[0]);
            _order.RemoveAt(0);
        }
        return id;
    }

    public bool TryGet(Guid id, out EditorMcpLiveTextureReceipt? receipt)
        => _receipts.TryGetValue(id, out receipt);

    public void Remove(Guid id)
    {
        _receipts.Remove(id);
        _order.Remove(id);
    }
}

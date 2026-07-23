namespace XREngine.Rendering;

/// <summary>
/// Metadata returned by allocation-free raw query reads.
/// </summary>
public readonly record struct RenderQueryReadResult(
    ERenderQueryReadStatus Status,
    RenderQueryTicket Ticket,
    RenderQueryResultLayout Layout,
    int ValuesWritten,
    string? Reason = null)
{
    public bool IsReady => Status == ERenderQueryReadStatus.Ready;
}

namespace XREngine.Rendering;

/// <summary>
/// Identifies one explicit texture residency request that the imported texture streaming manager accepted.
/// The request holds the texture at its requested mip until the caller cancels the ticket.
/// </summary>
/// <param name="Id">The identifier of the request. Zero is not a valid ticket.</param>
public readonly record struct TextureResidencyTicket(long Id)
{
    /// <summary>The ticket that identifies no request.</summary>
    public static TextureResidencyTicket None => default;

    /// <summary>True if the ticket identifies a request.</summary>
    public bool IsValid => Id != 0;
}

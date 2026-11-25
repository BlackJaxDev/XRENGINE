namespace XREngine.Editor;

/// <summary>Tells which saved record supplied the primary editor window layout.</summary>
internal enum ENativeDockLayoutKeyMatch
{
    /// <summary>No primary or index-zero record exists. The default layout applies.</summary>
    None,
    /// <summary>The canonical <c>primary</c> record supplied the layout.</summary>
    Canonical,
    /// <summary>The legacy record for the current window title supplied the layout.</summary>
    CurrentTitle,
    /// <summary>The legacy record for the default editor title supplied the layout.</summary>
    DefaultTitle,
    /// <summary>
    /// Only index-zero records for other titles exist. The default layout applies, and the
    /// old records stay unchanged until the user selects a layout explicitly.
    /// </summary>
    Unmatched,
}

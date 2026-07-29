namespace XREngine.Rendering;

/// <summary>
/// Backend seam for binding the immutable global table selection.
/// </summary>
public interface IAdvancedGlobalResourceTableBindingBackend
{
    RuntimeGraphicsApiKind Backend { get; }

    void BindGlobalResourceTables(in AdvancedGlobalResourceTableSet tables);
}

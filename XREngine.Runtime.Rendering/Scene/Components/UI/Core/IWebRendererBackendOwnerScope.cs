namespace XREngine.Rendering.UI;

/// <summary>Runs native web work under its exact window graphics owner.</summary>
public interface IWebRendererBackendOwnerScope
{
    void ExecuteOnOwner(XRWindow window, Action action);
    void AssertCurrentOwner();
}

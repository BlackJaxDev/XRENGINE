using XREngine.Rendering;
using XREngine.Rendering.UI;

namespace XREngine.UnitTests.Rendering;

internal sealed class UIWebFakeScopedBackend : UIWebFakeBackend, IWebRendererBackendOwnerScope
{
    public void ExecuteOnOwner(XRWindow window, Action action) => action();
    public void AssertCurrentOwner() { }
}

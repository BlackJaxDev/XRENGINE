using System.ComponentModel;

namespace XREngine.Rendering.UI
{
    public delegate void DelScrolling(bool up);
    public class UIComboBoxComponent : UIInteractableComponent
    {
        [Category("Events")]
        public event DelScrolling? Scrolled;

        protected void OnScrolled(bool up)
            => Scrolled?.Invoke(up);

        public UIComboBoxComponent()
        {

        }
    }
}

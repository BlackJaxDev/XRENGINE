using XREngine.Data.Core;

namespace XREngine.Rendering.UI
{
    public class UISizingValue : XRBase
    {
        private float _value = 0.0f;
        private ESizingMode _mode = ESizingMode.Fixed;

        public ESizingMode Mode
        {
            get => _mode;
            set => SetField(ref _mode, value);
        }
        public float Value
        {
            get => _value;
            set => SetField(ref _value, value);
        }
    }
}

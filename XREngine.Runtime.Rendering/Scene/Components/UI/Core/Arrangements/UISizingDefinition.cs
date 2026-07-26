using XREngine.Data.Core;

namespace XREngine.Rendering.UI
{
    public class UISizingDefinition : XRBase
    {
        private UISizingValue _value = new();
        private UISizingValue? _min = null;
        private UISizingValue? _max = null;

        internal float CalculatedValue { get; set; }
        internal List<UITransform> AttachedControls { get; } = [];

        public bool NeedsAutoSizing =>
            _value != null && _value.Mode == ESizingMode.Auto ||
            _min != null && _min.Mode == ESizingMode.Auto ||
            _max != null && _max.Mode == ESizingMode.Auto;

        public UISizingValue Value
        {
            get => _value;
            set => SetField(ref _value, value);
        }
        public UISizingValue? Min
        {
            get => _min;
            set => SetField(ref _min, value);
        }
        public UISizingValue? Max
        {
            get => _max;
            set => SetField(ref _max, value);
        }
    }
}

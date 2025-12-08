using XREngine.Scene;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    /// <summary>
    /// Transformer for editing LayerMask struct properties.
    /// Exposes the mask value as an editable int, and re-sets the struct on the target when changed.
    /// </summary>
    public class LayerMaskTransformer : DataTransformerBase<LayerMask?>
    {
        private int? _value;

        /// <summary>
        /// The raw bitmask value. -1 means all layers.
        /// </summary>
        public int? Value
        {
            get => _value;
            set => SetField(ref _value, value);
        }

        protected override bool Equal(LayerMask? lhs, LayerMask? rhs)
            => lhs is null || rhs is null ? lhs is null && rhs is null : lhs.Value.Value == rhs.Value.Value;

        protected override void DisplayValue(LayerMask? value)
        {
            if (value is null)
            {
                Value = null;
                return;
            }
            Value = value.Value.Value;
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (Property is null || Targets is null || propName is not nameof(Value) || !IsFocused)
                return;

            var newMask = new LayerMask(Value ?? 0);
            foreach (var target in Targets)
            {
                if (target is null)
                    continue;
                Property.SetValue(target, newMask);
            }
        }
    }
}

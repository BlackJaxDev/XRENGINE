using System.Reflection;
using XREngine.Components;
using XREngine.Editor.UI;

namespace XREngine.Editor;

public static partial class InspectorPropertyEditors
{
    /// <summary>Adapts a source property to retained component fields without polling.</summary>
    public abstract class DataTransformerBase : XRComponent
    {
        private PropertyInfo? _property;
        private object?[]? _targets;
        private EditorPropertyAccess? _access;
        private bool _refreshing;

        public PropertyInfo? Property
        {
            get => _property;
            set => SetField(ref _property, value);
        }

        public object?[]? Targets
        {
            get => _targets;
            set => SetField(ref _targets, value);
        }

        protected bool CanWriteSource => !_refreshing && _access?.CanWrite == true;

        public PropertyInfo GetTransformedProperty(string name)
            => GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new InvalidOperationException($"Property '{name}' not found.");

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            if (propName is nameof(Property) or nameof(Targets))
                Rebind();
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            Rebind();
        }

        protected override void OnComponentDeactivated()
        {
            Detach();
            base.OnComponentDeactivated();
        }

        private void Rebind()
        {
            Detach();
            if (!IsActiveInHierarchy || Property is null || Targets is null)
                return;
            _access = new EditorPropertyAccess(Property, Targets);
            _access.Changed += Refresh;
            Refresh();
        }

        private void Detach()
        {
            if (_access is not null)
                _access.Changed -= Refresh;
            _access = null;
        }

        private void Refresh()
        {
            if (_access is null)
                return;
            _refreshing = true;
            try
            {
                DisplaySourceValue(_access.TryGetCommonValue(out object? value) ? value : null);
            }
            finally { _refreshing = false; }
        }

        protected void UpdateSourceValue(Func<object?, object?> update) => _access?.UpdateValue(update);
        protected abstract void DisplaySourceValue(object? value);
    }
}

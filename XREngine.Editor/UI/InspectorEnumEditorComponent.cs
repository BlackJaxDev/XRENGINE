using System.Reflection;
using XREngine.Components;
using XREngine.Data.Core;
using XREngine.Rendering.UI;

namespace XREngine.Editor.UI;

/// <summary>Rebinds a retained enum combo and retires its previous property owner.</summary>
public sealed class InspectorEnumEditorComponent : XRComponent
{
    private UIComboBoxComponent? _combo;
    private UITextComponent? _label;
    private UIContextMenuComponent? _popup;
    private EditorEnumPropertyBinding? _ownedBinding;
    private PropertyInfo? _boundProperty;
    private object?[]? _boundTargets;
    private PropertyInfo? _pendingProperty;
    private object?[]? _pendingTargets;
    private bool _pendingBind;
    private bool _pendingLabel;
    private bool _transitioning;
    private bool _retired;
    private UIComboBoxComponent? _retiringCombo;
    private EditorEnumPropertyBinding? _retiringBinding;

    public void Configure(UIComboBoxComponent combo, UITextComponent label,
        PropertyInfo property, object?[]? targets)
    {
        if (_retired || _combo is not null)
            throw new InvalidOperationException("The enum editor already has an owner.");
        SetField(ref _combo, combo, publishNotifications: false);
        SetField(ref _label, label, publishNotifications: false);
        SetField(ref _popup, combo.Popup, publishNotifications: false);
        combo.SelectionChanged += OnSelectionChanged;
        combo.PropertyChanged += OnComboPropertyChanged;
        Bind(property, targets);
    }

    public void Bind(PropertyInfo? property, object?[]? targets)
    {
        if (_retired || _combo is null)
            return;
        _pendingProperty = property;
        _pendingTargets = targets?.ToArray();
        _pendingBind = true;
        DrainTransitions();
    }

    private void DrainTransitions()
    {
        if (_transitioning || _retired)
            return;
        _transitioning = true;
        List<Exception>? failures = null;
        try
        {
            while (!_retired && (_pendingBind || _pendingLabel))
            {
                try
                {
                    if (_pendingBind)
                    {
                        PropertyInfo? property = _pendingProperty;
                        object?[]? targets = _pendingTargets;
                        _pendingBind = false;
                        _pendingProperty = null;
                        _pendingTargets = null;
                        if (!MatchesCurrent(property, targets))
                            ApplyBinding(property, targets);
                    }
                    else
                    {
                        _pendingLabel = false;
                        if (_label is { } label)
                            label.Text = _combo?.AccessibilityValue ?? "Mixed or undefined";
                    }
                }
                catch (Exception error) { (failures ??= []).Add(error); }
            }
        }
        finally
        {
            _transitioning = false;
            try { ReleaseRetiredOwner(); }
            catch (Exception error) { (failures ??= []).Add(error); }
        }
        if (failures is not null)
            throw new AggregateException("The enum editor could not complete all binding updates.", failures);
    }

    private bool MatchesCurrent(PropertyInfo? property, object?[]? targets)
    {
        if (_ownedBinding?.IsRetired == true || !Equals(property, _boundProperty) ||
            !ReferenceEquals(_combo?.Binding, _ownedBinding) ||
            !ReferenceEquals(_combo?.VirtualOptions, _ownedBinding) ||
            !ReferenceEquals(_combo?.Popup, property is null ? null : _popup) ||
            _combo?.IsReadOnly != (property is null || !property.CanWrite))
            return false;
        if (targets is null || _boundTargets is null)
            return targets is null && _boundTargets is null;
        if (targets.Length != _boundTargets.Length)
            return false;
        for (int index = 0; index < targets.Length; index++)
            if (!ReferenceEquals(targets[index], _boundTargets[index]))
                return false;
        return true;
    }

    private void ApplyBinding(PropertyInfo? property, object?[]? targets)
    {
        UIComboBoxComponent combo = _combo!;
        EditorEnumPropertyBinding? previous = _ownedBinding;
        previous?.Dispose();
        // Nested requests wait until this setter sequence returns. They cannot own
        // a row while an older PropertyChanging callback is still on the stack.
        combo.Close();
        if (_retired) return;
        combo.Popup = property is null ? null : _popup;
        if (_retired) return;
        if (combo.Binding is not null && !ReferenceEquals(combo.Binding, previous))
            throw new InvalidOperationException("The enum row has a different binding owner.");
        combo.Binding = null;
        if (_retired) return;
        if (combo.Binding is not null)
            throw new InvalidOperationException("The enum row could not detach its previous binding.");
        if (combo.VirtualOptions is not null && !ReferenceEquals(combo.VirtualOptions, previous))
            throw new InvalidOperationException("The enum row has a different option owner.");
        combo.VirtualOptions = null;
        if (_retired) return;
        if (combo.VirtualOptions is not null)
            throw new InvalidOperationException("The enum row could not detach its previous options.");
        combo.IsReadOnly = property is null || !property.CanWrite;
        if (_retired) return;
        combo.AccessibilityLabel = property?.Name;
        if (_retired) return;
        EditorEnumPropertyBinding? binding = property is not null && targets is not null
            ? new(property, targets) : null;
        SetField(ref _ownedBinding, binding, publishNotifications: false);
        if (binding is not null)
        {
            combo.VirtualOptions = binding;
            if (_retired) return;
            if (!ReferenceEquals(combo.VirtualOptions, binding))
            {
                binding.Dispose();
                throw new InvalidOperationException("The enum row rejected its replacement options.");
            }
            combo.Binding = binding;
            if (!_retired && !ReferenceEquals(combo.Binding, binding))
            {
                binding.Dispose();
                throw new InvalidOperationException("The enum row rejected its replacement binding.");
            }
        }
        if (_retired) return;
        SetField(ref _boundProperty, property, publishNotifications: false);
        SetField(ref _boundTargets, targets, publishNotifications: false);
        _pendingLabel = true;
    }

    private void OnSelectionChanged(UIValueChange<ulong?> change)
    {
        _pendingLabel = true;
        DrainTransitions();
    }

    private void OnComboPropertyChanged(object? sender, IXRPropertyChangedEventArgs change)
    {
        if (!_retired || change.PropertyName != nameof(UIComboBoxComponent.Binding) ||
            _retiringCombo is not { } combo ||
            _retiringBinding is not { } binding ||
            !ReferenceEquals(sender, combo) ||
            !ReferenceEquals(combo.Binding, binding))
            return;
        // A pre-change callback can retire this owner before the combo finishes
        // assigning its binding. Clear only that binding before the combo reads it.
        combo.Binding = null;
    }

    protected override void OnDestroying()
    {
        SetField(ref _retired, true, publishNotifications: false);
        UIComboBoxComponent? combo = _combo;
        EditorEnumPropertyBinding? owned = _ownedBinding;
        // Retire value access even if a control vetoes clearing its visible binding.
        // Deferred cleanup then removes only this exact owner after setters return.
        try { owned?.Dispose(); }
        catch (Exception error)
        {
            try { Debug.LogException(error, "Could not detach enum property notifications."); }
            catch { }
        }
        _pendingBind = false;
        _pendingLabel = false;
        _pendingProperty = null;
        _pendingTargets = null;
        SetField(ref _ownedBinding, null, publishNotifications: false);
        SetField(ref _boundProperty, null, publishNotifications: false);
        SetField(ref _boundTargets, null, publishNotifications: false);
        SetField(ref _combo, null, publishNotifications: false);
        SetField(ref _label, null, publishNotifications: false);
        SetField(ref _popup, null, publishNotifications: false);
        try
        {
            if (combo is not null)
            {
                combo.SelectionChanged -= OnSelectionChanged;
                _retiringCombo = combo;
                _retiringBinding = owned;
                if (!_transitioning)
                    ReleaseRetiredOwner();
            }
        }
        finally { base.OnDestroying(); }
    }

    private void ReleaseRetiredOwner()
    {
        UIComboBoxComponent? combo = _retiringCombo;
        EditorEnumPropertyBinding? owned = _retiringBinding;
        _retiringCombo = null;
        _retiringBinding = null;
        if (combo is null)
            return;
        try
        {
            if (owned is not null && ReferenceEquals(combo.Binding, owned))
                combo.Binding = null;
        }
        finally
        {
            try
            {
                if (owned is not null && ReferenceEquals(combo.VirtualOptions, owned))
                    combo.VirtualOptions = null;
            }
            finally { combo.PropertyChanged -= OnComboPropertyChanged; }
        }
    }
}

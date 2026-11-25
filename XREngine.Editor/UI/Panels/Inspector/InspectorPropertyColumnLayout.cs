using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Editor;

/// <summary>Sizes the two captured inspector cells without changing their bindings.</summary>
internal sealed class InspectorPropertyColumnLayout
{
    private readonly SceneNode _row;
    private readonly UIBoundableTransform _rowTransform;
    private readonly UIBoundableTransform _name;
    private readonly UIBoundableTransform _value;
    private readonly SceneNode _nameNode;
    private readonly SceneNode _valueNode;
    private readonly ObjectConstructionIdentity _rowIdentity;
    private readonly ObjectConstructionIdentity _rowTransformIdentity;
    private readonly ObjectConstructionIdentity _nameIdentity;
    private readonly ObjectConstructionIdentity _valueIdentity;
    private readonly ObjectConstructionIdentity _nameNodeIdentity;
    private readonly ObjectConstructionIdentity _valueNodeIdentity;
    private readonly long _nameMembership;
    private readonly long _valueMembership;
    private Func<bool>? _bindingCurrent;
    private long _rowMembership;

    internal InspectorPropertyColumnLayout(SceneNode row, UIBoundableTransform name,
        UIBoundableTransform value)
    {
        _row = row;
        _rowTransform = (UIBoundableTransform)row.Transform;
        _name = name;
        _value = value;
        _nameNode = name.SceneNode!;
        _valueNode = value.SceneNode!;
        _rowIdentity = ObjectConstructionIdentity.Capture(row);
        _rowTransformIdentity = ObjectConstructionIdentity.Capture(_rowTransform);
        _nameIdentity = ObjectConstructionIdentity.Capture(name);
        _valueIdentity = ObjectConstructionIdentity.Capture(value);
        _nameNodeIdentity = ObjectConstructionIdentity.Capture(_nameNode);
        _valueNodeIdentity = ObjectConstructionIdentity.Capture(_valueNode);
        _nameMembership = name.HierarchyMembershipRevision;
        _valueMembership = value.HierarchyMembershipRevision;
    }

    internal void Bind(Func<bool> current)
    {
        _rowMembership = _rowTransform.HierarchyMembershipRevision;
        _bindingCurrent = current;
    }
    internal void Revoke() => _bindingCurrent = null;

    internal bool TryApply(UIListTransform list, object provider, ulong incarnation,
        Func<bool> ownerCurrent, float viewportWidth)
    {
        Func<bool>? binding = _bindingCurrent;
        if (binding is null || !list.TryGetVirtualRowLease(_rowTransform, out UIVirtualRowLease lease) ||
            !Current(list, provider, incarnation, lease, binding, ownerCurrent, viewportWidth))
            return false;
        float budget = AvailableCellWidth(list, viewportWidth);
        if (!float.IsFinite(budget) || budget <= 2)
            return true;
        float nameWidth = MathF.Min(160, budget * 0.4f);
        float valueWidth = budget - nameWidth;
        var proof = (Owner: this, List: list, Provider: provider, Incarnation: incarnation,
            Lease: lease, Binding: binding, OwnerCurrent: ownerCurrent, ViewportWidth: viewportWidth,
            Budget: budget, NameReady: false, NameWidth: nameWidth, NameIntent: _name.WidthWriteIntentRevision);
        if (_name.Width != nameWidth)
        {
            proof.NameIntent = checked(proof.NameIntent + 1);
            _name.SetWidthGuarded(nameWidth, proof, static state => RequireWrite(state));
        }
        proof.NameReady = true;
        RequireWrite(proof);
        ulong valueIntent = _value.WidthWriteIntentRevision;
        if (_value.Width != valueWidth)
        {
            valueIntent = checked(valueIntent + 1);
            _value.SetWidthGuarded(valueWidth, proof, static state => RequireWrite(state));
        }
        RequireWrite(proof);
        return _value.Width == valueWidth && _value.WidthWriteIntentRevision == valueIntent;
    }

    private static void RequireWrite((InspectorPropertyColumnLayout Owner, UIListTransform List,
        object Provider, ulong Incarnation, UIVirtualRowLease Lease, Func<bool> Binding,
        Func<bool> OwnerCurrent, float ViewportWidth, float Budget, bool NameReady,
        float NameWidth, ulong NameIntent) proof)
    {
        InspectorPropertyColumnLayout owner = proof.Owner;
        if (!owner.Current(proof.List, proof.Provider, proof.Incarnation, proof.Lease,
                proof.Binding, proof.OwnerCurrent, proof.ViewportWidth) ||
            owner.AvailableCellWidth(proof.List, proof.ViewportWidth) != proof.Budget ||
            proof.NameReady && (owner._name.Width != proof.NameWidth ||
                owner._name.WidthWriteIntentRevision != proof.NameIntent))
            throw new InvalidOperationException("The inspector column write lost its exact row owner.");
    }
    private bool Current(UIListTransform list, object provider, ulong incarnation,
        UIVirtualRowLease lease, Func<bool> binding, Func<bool> ownerCurrent, float viewportWidth)
        => ReferenceEquals(_bindingCurrent, binding) && ownerCurrent() && binding() &&
            ReferenceEquals(_bindingCurrent, binding) && ownerCurrent() &&
            Live(_rowIdentity) && Live(_rowTransformIdentity) && Live(_nameIdentity) &&
            Live(_valueIdentity) && Live(_nameNodeIdentity) && Live(_valueNodeIdentity) &&
            list.Parent is UIScrollableTransform scroll && scroll.ActualWidth == viewportWidth &&
            _rowTransform.HasUsableHierarchyMembershipRevision &&
            _rowTransform.HierarchyMembershipRevision == _rowMembership &&
            ReferenceEquals(_row.Transform, _rowTransform) && ReferenceEquals(_rowTransform.Parent, list) &&
            ReferenceEquals(_nameNode.Transform, _name) && ReferenceEquals(_valueNode.Transform, _value) &&
            ReferenceEquals(_name.Parent, _rowTransform) && ReferenceEquals(_value.Parent, _rowTransform) &&
            _rowTransform.Children.Count == 2 && ReferenceEquals(_rowTransform.Children[0], _name) &&
            ReferenceEquals(_rowTransform.Children[1], _value) &&
            _name.HasUsableHierarchyMembershipRevision && _value.HasUsableHierarchyMembershipRevision &&
            _name.HierarchyMembershipRevision == _nameMembership &&
            _value.HierarchyMembershipRevision == _valueMembership &&
            ReferenceEquals(list.VirtualProvider, provider) && list.VirtualProviderIncarnation == incarnation &&
            list.TryGetVirtualRowLease(_rowTransform, out UIVirtualRowLease current) && current == lease;

    private float AvailableCellWidth(UIListTransform list, float viewportWidth)
    {
        float spacing = _rowTransform is UIListTransform rowList ? rowList.ItemSpacing : 0;
        UIScrollableTransform scroll = (UIScrollableTransform)list.Parent!;
        return viewportWidth - scroll.Padding.X - scroll.Padding.Z - list.Padding.X - list.Padding.Z -
            _rowTransform.Padding.X - _rowTransform.Padding.Z - spacing -
            _name.Margins.X - _name.Margins.Z - _value.Margins.X - _value.Margins.Z;
    }
    private static bool Live(ObjectConstructionIdentity identity)
        => identity.IsCurrent && !identity.Target.IsDestroyed && !identity.Target.IsDestroyQueued;
}

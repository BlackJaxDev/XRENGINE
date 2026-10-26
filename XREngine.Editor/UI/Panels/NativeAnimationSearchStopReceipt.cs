using XREngine.Animation;
using XREngine.Components;
using XREngine.Components.Animation;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

/// <summary>Retains one ordinary Animation search draft until its same panel resumes.</summary>
internal sealed class NativeAnimationSearchStopReceipt : IDisposable
{
    private readonly AnimationPanel _panel;
    private readonly ObjectConstructionIdentity _panelIdentity;
    private readonly SceneNode _panelNode;
    private readonly ObjectConstructionIdentity _panelNodeIdentity;
    private readonly TransformBase[] _path;
    private readonly SceneNode[] _pathNodes;
    private readonly ObjectConstructionIdentity[] _pathIdentities;
    private readonly ObjectConstructionIdentity[] _pathNodeIdentities;
    private readonly IRuntimeWorldContext?[] _pathWorlds;
    private readonly IRuntimeWorldContext?[] _pathNodeWorlds;
    private readonly int _panelTransformIndex;
    private readonly UITextInputComponent _input;
    private readonly SceneNode _inputNode;
    private readonly TransformBase _inputTransform;
    private readonly UITextComponent _display;
    private readonly ObjectConstructionIdentity _inputIdentity;
    private readonly ObjectConstructionIdentity _inputNodeIdentity;
    private readonly ObjectConstructionIdentity _inputTransformIdentity;
    private readonly ObjectConstructionIdentity _displayIdentity;
    private readonly IRuntimeWorldContext? _inputWorld;
    private readonly IRuntimeWorldContext? _displayWorld;
    private readonly UICanvasComponent? _canvas;
    private readonly SceneNode? _canvasNode;
    private readonly TransformBase? _canvasTransform;
    private readonly ObjectConstructionIdentity? _canvasIdentity;
    private readonly ObjectConstructionIdentity? _canvasNodeIdentity;
    private readonly ObjectConstructionIdentity? _canvasTransformIdentity;
    private readonly IRuntimeWorldContext? _canvasWorld;
    private readonly IRuntimeWorldContext? _canvasNodeWorld;
    private readonly IRuntimeWorldContext? _canvasTransformWorld;
    private readonly IUICanvasInputSource? _canvasInput;
    private readonly XRObjectBase? _canvasInputOwner;
    private readonly ObjectConstructionIdentity? _canvasInputIdentity;
    private readonly SceneNode? _canvasInputNode;
    private readonly TransformBase? _canvasInputTransform;
    private readonly ObjectConstructionIdentity? _canvasInputNodeIdentity;
    private readonly ObjectConstructionIdentity? _canvasInputTransformIdentity;
    private readonly IRuntimeWorldContext? _canvasInputNodeWorld;
    private readonly IRuntimeWorldContext? _canvasInputTransformWorld;
    private readonly XRWindow? _window;
    private readonly ulong _panelEpoch;
    private readonly ulong _selectionRevision;
    private readonly ulong _assetRevision;
    private readonly SceneNode? _selectedNode;
    private readonly ObjectConstructionIdentity? _selectedNodeIdentity;
    private readonly AnimationClipComponent? _selectedComponent;
    private readonly ObjectConstructionIdentity? _selectedComponentIdentity;
    private readonly AnimationClip? _componentClip;
    private readonly AnimationClip? _selectedAssetClip;
    private readonly AnimationClip? _currentClip;
    private readonly ObjectConstructionIdentity? _currentClipIdentity;
    private readonly string _committedQuery;
    private readonly XRPropertyChangedEventHandler? _componentChanged;
    private NativeAnimationSearchFieldReceipt? _fieldReceipt;
    private ulong _focusIntent;
    private ulong _textIntent;
    private ulong _displayIntent;
    private ulong _caretIntent;
    private long _focusEpoch;
    private string _text = string.Empty;
    private string? _displayText;
    private string _preText = string.Empty;
    private string _postText = string.Empty;
    private int _cursor;
    private int _anchor;
    private bool _focused;
    private bool _componentAnimationChanged;
    private ulong _componentAnimationChangeCount;
    private ulong _resetSelectionRevision;
    private ulong _resetAssetRevision;
    private SceneNode? _resetSelectedNode;
    private AnimationClipComponent? _resetSelectedComponent;
    private AnimationClip? _resetComponentClip;
    private AnimationClip? _resetAssetClip;
    private AnimationClip? _resetCurrentClip;
    private ObjectConstructionIdentity? _resetSelectedNodeIdentity;
    private ObjectConstructionIdentity? _resetSelectedComponentIdentity;
    private ObjectConstructionIdentity? _resetCurrentClipIdentity;
    private IUICanvasInputSource? _resetCanvasInput;
    private ulong _resetFocusIntent;
    private ulong _resetComponentAnimationChangeCount;
    private AnimationClipComponent? _resetObservedComponent;
    private XRPropertyChangedEventHandler? _resetComponentChanged;
    private ulong _resetObservedAnimationChangeCount;
    private ulong _resumePanelEpoch;
    private bool _ready;
    private bool _disposed;
    internal ulong PanelEpoch => _panelEpoch;

    private NativeAnimationSearchStopReceipt(AnimationPanel panel, NativeAnimationSearchInputComponent input)
    {
        _panel = panel;
        _panelIdentity = ObjectConstructionIdentity.Capture(panel);
        _panelNode = panel.SceneNode;
        _panelNodeIdentity = ObjectConstructionIdentity.Capture(_panelNode);
        _input = input;
        _inputNode = input.SceneNode;
        _inputTransform = _inputNode.Transform;
        _display = _inputNode.GetComponent<UITextComponent>() ??
            throw new InvalidOperationException("The Animation search display is unavailable.");
        _inputIdentity = ObjectConstructionIdentity.Capture(input);
        _inputNodeIdentity = ObjectConstructionIdentity.Capture(_inputNode);
        _inputTransformIdentity = ObjectConstructionIdentity.Capture(_inputTransform);
        _displayIdentity = ObjectConstructionIdentity.Capture(_display);
        _inputWorld = input.World;
        _displayWorld = _display.World;
        _canvas = input.UserInterfaceCanvas;
        _canvasNode = _canvas?.SceneNode;
        _canvasTransform = _canvasNode is { IsTransformNull: false } ? _canvasNode.Transform : null;
        _canvasIdentity = _canvas is null ? null : ObjectConstructionIdentity.Capture(_canvas);
        _canvasNodeIdentity = _canvasNode is null ? null : ObjectConstructionIdentity.Capture(_canvasNode);
        _canvasTransformIdentity = _canvasTransform is null
            ? null : ObjectConstructionIdentity.Capture(_canvasTransform);
        _canvasWorld = _canvas?.World;
        _canvasNodeWorld = _canvasNode?.World;
        _canvasTransformWorld = _canvasTransform?.World;
        _canvasInput = _canvas?.GetInputComponent();
        _canvasInputOwner = _canvasInput as XRObjectBase;
        _canvasInputIdentity = _canvasInputOwner is null
            ? null : ObjectConstructionIdentity.Capture(_canvasInputOwner);
        _canvasInputNode = _canvasInputOwner is XRComponent inputComponent &&
            inputComponent.SceneNode is { IsTransformNull: false } inputNode ? inputNode : null;
        _canvasInputTransform = _canvasInputNode is null ? null : _canvasInputNode.Transform;
        _canvasInputNodeIdentity = _canvasInputNode is null
            ? null : ObjectConstructionIdentity.Capture(_canvasInputNode);
        _canvasInputTransformIdentity = _canvasInputTransform is null
            ? null : ObjectConstructionIdentity.Capture(_canvasInputTransform);
        _canvasInputNodeWorld = _canvasInputNode?.World;
        _canvasInputTransformWorld = _canvasInputTransform?.World;
        _window = panel.Window;
        _panelEpoch = panel.AuthoringEpoch;
        _selectionRevision = Selection.PublicationRevision;
        _assetRevision = NativeAssetSelection.PublicationRevision;
        _selectedNode = Selection.SceneNode;
        _selectedNodeIdentity = _selectedNode is null ? null : ObjectConstructionIdentity.Capture(_selectedNode);
        _selectedComponent = _selectedNode?.GetComponent<AnimationClipComponent>();
        _selectedComponentIdentity = _selectedComponent is null
            ? null : ObjectConstructionIdentity.Capture(_selectedComponent);
        _componentClip = _selectedComponent?.Animation;
        _selectedAssetClip = NativeAssetSelection.SelectedTarget as AnimationClip;
        _currentClip = panel.CurrentSearchClip;
        _currentClipIdentity = _currentClip is null ? null : ObjectConstructionIdentity.Capture(_currentClip);
        _committedQuery = panel.CommittedPathSearch;
        List<TransformBase> path = [];
        List<SceneNode> nodes = [];
        for (TransformBase? current = _inputTransform; current is not null; current = current.Parent)
        {
            path.Add(current);
            nodes.Add(current.SceneNode!);
        }
        _path = path.ToArray();
        _pathNodes = nodes.ToArray();
        _pathIdentities = _path.Select(ObjectConstructionIdentity.Capture).ToArray();
        _pathNodeIdentities = _pathNodes.Select(ObjectConstructionIdentity.Capture).ToArray();
        _pathWorlds = _path.Select(transform => transform.World).ToArray();
        _pathNodeWorlds = _pathNodes.Select(node => node.World).ToArray();
        _panelTransformIndex = Array.IndexOf(_path, panel.Transform);
        CaptureInputState();
        if (_selectedComponent is not null)
        {
            _componentChanged = (_, args) =>
            {
                if (args.PropertyName == nameof(AnimationClipComponent.Animation))
                {
                    _componentAnimationChanged = true;
                    _componentAnimationChangeCount = unchecked(_componentAnimationChangeCount + 1);
                }
            };
            _selectedComponent.PropertyChanged += _componentChanged;
        }
    }

    internal static bool TryCreate(AnimationPanel panel, NativeAnimationSearchInputComponent? input,
        IUICanvasInputSource? canvasInput, out NativeAnimationSearchStopReceipt receipt)
    {
        receipt = null!;
        if (input is null || !ReferenceEquals(input.Panel, panel) || input.IsDestroyed || input.IsDestroyQueued ||
            input.Binding is not null || input.Property is not null || input.Targets is not null ||
            input.CommitOnSubmit || input.IsComposing ||
            input.SceneNode is not { IsTransformNull: false } node ||
            node.GetComponent<UITextComponent>() is null || !IsDescendantOf(node.Transform, panel.Transform))
            return false;
        NativeAnimationSearchStopReceipt candidate = new(panel, input);
        if (!ReferenceEquals(canvasInput, candidate._canvasInput) ||
            !candidate.ReleaseOwnerCurrent(panel) || !candidate.IsInputStateCurrent())
        {
            candidate.DisposeCore();
            return false;
        }
        if (input.IsFocused && (canvasInput is null || canvasInput.FocusIntentRevision == 0 ||
            candidate._canvasInputOwner is null || canvasInput.HasPendingFocusRequest ||
            !ReferenceEquals(canvasInput.FocusedComponent, input)) ||
            !input.IsFocused && canvasInput is not null && ReferenceEquals(canvasInput.FocusedComponent, input))
        {
            candidate.DisposeCore();
            return false;
        }
        receipt = candidate;
        return true;
    }

    internal bool TryReleaseFocus()
    {
        if (_disposed || _ready) return false;
        if (_focused && (_canvasInput is null || _canvasInput.FocusIntentRevision == 0 ||
            _canvasInput.HasPendingFocusRequest || !ReferenceEquals(_canvasInput.FocusedComponent, _input) ||
            !TryReleaseFocusedDraft(_canvasInput)))
        {
            return false;
        }
        if (!_focused)
            CaptureInputState();
        if (!NativeAnimationSearchFieldReceipt.TryCapture(_panel, _input, out _fieldReceipt) ||
            !IsInputStateCurrent())
        {
            _fieldReceipt = null;
            return false;
        }
        _ready = true;
        return true;
    }

    internal bool TryResume(AnimationPanel panel, UITextInputComponent input, ulong expectedPanelEpoch)
    {
        bool shouldReset;
        try
        {
            if (_disposed || !_ready || !ReferenceEquals(panel, _panel) || !ReferenceEquals(input, _input) ||
                !panel.IsActiveInHierarchy || panel.AuthoringEpoch != expectedPanelEpoch ||
                !IsInputOwnerCurrent() ||
                !IsInputStateCurrent() || _fieldReceipt?.IsCurrent(0, _committedQuery) != true ||
                !string.Equals(panel.CommittedPathSearch, _committedQuery,
                    StringComparison.Ordinal))
                return false;
            _resumePanelEpoch = panel.AuthoringEpoch;
            shouldReset = SourceChanged(panel);
            if (!shouldReset || string.Equals(input.Text, _committedQuery, StringComparison.Ordinal))
                return true;
            CaptureResetSource(panel);
            try
            {
                if (panel.SearchReceiveReceipt is { } receive)
                    receive.ResetDraft(input, _committedQuery, require =>
                        input.SetTextGuarded(_committedQuery, require, admission =>
                        {
                            RequireResetOwner();
                            admission();
                            RequireResetOwner();
                        }));
                else
                    input.SetTextGuarded(_committedQuery, this,
                        static receipt => receipt.RequireResetOwner());
            }
            catch (InvalidOperationException) when (!ResetWriteStillOwned())
            {
                // A newer owner write or transfer cancels this reset.
            }
            return false;
        }
        finally { DisposeCore(); }
    }

    private void CaptureInputState()
    {
        _textIntent = _input.TextWriteIntentRevision;
        _displayIntent = _display.TextWriteIntentRevision;
        _caretIntent = _input.CaretWriteIntentRevision;
        _focusEpoch = _input.FocusEpoch;
        _text = _input.Text;
        _displayText = _display.Text;
        _preText = _input.PreText;
        _postText = _input.PostText;
        _cursor = _input.CursorPosition;
        _anchor = _input.SelectionAnchor;
        _focused = _input.IsFocused;
        _focusIntent = _canvasInput?.FocusIntentRevision ?? 0;
    }

    private bool TryReleaseFocusedDraft(IUICanvasInputSource canvasInput)
    {
        ulong textIntent = _input.TextWriteIntentRevision;
        ulong displayIntent = _display.TextWriteIntentRevision;
        ulong caretIntent = _input.CaretWriteIntentRevision;
        string text = _input.Text;
        string? displayText = _display.Text;
        int cursor = _input.CursorPosition;
        int anchor = _input.SelectionAnchor;
        string preText = _input.PreText;
        string postText = _input.PostText;
        IRuntimeWorldContext? inputWorld = _input.World;
        bool Current() => ReleaseOwnerCurrent(_panel) && !_disposed &&
            ReferenceEquals(_canvasInput, canvasInput) &&
            _input.TextWriteIntentRevision == textIntent && _input.Text == text &&
            _display.TextWriteIntentRevision == displayIntent && _display.Text == displayText &&
            _input.CaretWriteIntentRevision == caretIntent && _input.CursorPosition == cursor &&
            _input.SelectionAnchor == anchor && _input.PreText == preText && _input.PostText == postText &&
            ReferenceEquals(_input.World, inputWorld);
        if (!Current()) return false;
        ulong requestedIntent = canvasInput.FocusIntentRevision;
        bool released = canvasInput.TryReleaseFocusPreservingDraft(_input, requestedIntent,
            Current, out ulong clearedIntent);
        if (!released || !ReleaseOwnerCurrent(_panel) || _input.IsFocused ||
            !ReferenceEquals(canvasInput.FocusedComponent, null) || canvasInput.FocusIntentRevision != clearedIntent)
            return false;
        CaptureInputState();
        return !_focused && _textIntent == textIntent && _displayIntent == displayIntent &&
            _caretIntent == caretIntent && _text == text && _displayText == displayText &&
            _cursor == cursor && _anchor == anchor;
    }

    private bool IsInputOwnerCurrent()
    {
        if (_disposed || !_panelIdentity.IsCurrent || _panel.IsDestroyed || _panel.IsDestroyQueued ||
            !_panelNodeIdentity.IsCurrent || _panelNode.IsDestroyed || _panelNode.IsDestroyQueued ||
            !ReferenceEquals(_panel.SceneNode, _panelNode) || !_inputIdentity.IsCurrent ||
            _input.IsDestroyed || _input.IsDestroyQueued || !_inputNodeIdentity.IsCurrent ||
            _inputNode.IsDestroyed || _inputNode.IsDestroyQueued || _inputNode.IsTransformNull ||
            !_inputTransformIdentity.IsCurrent || _inputTransform.IsDestroyed || _inputTransform.IsDestroyQueued ||
            !_displayIdentity.IsCurrent || _display.IsDestroyed || _display.IsDestroyQueued ||
            !ReferenceEquals(_input.SceneNode, _inputNode) || !ReferenceEquals(_inputNode.Transform, _inputTransform) ||
            !ReferenceEquals(_display.SceneNode, _inputNode) || !_inputNode.Components.Contains(_input) ||
            !_inputNode.Components.Contains(_display) || !ReferenceEquals(_panel.Window, _window) ||
            !ReferenceEquals(_input.World, _inputWorld) || !ReferenceEquals(_display.World, _displayWorld) ||
            !CanvasOwnerCurrent())
            return false;
        TransformBase? current = _inputTransform;
        for (int index = 0; index < _path.Length; index++)
        {
            TransformBase transform = _path[index];
            SceneNode node = _pathNodes[index];
            if (!ReferenceEquals(current, transform) || !ReferenceEquals(transform.SceneNode, node) ||
                !ReferenceEquals(node.Transform, transform) || !_pathIdentities[index].IsCurrent ||
                !_pathNodeIdentities[index].IsCurrent || transform.IsDestroyed || transform.IsDestroyQueued ||
                node.IsDestroyed || node.IsDestroyQueued || !ReferenceEquals(transform.World, _pathWorlds[index]) ||
                !ReferenceEquals(node.World, _pathNodeWorlds[index]))
                return false;
            current = transform.Parent;
        }
        return current is null && _panelTransformIndex >= 0 &&
            ReferenceEquals(_path[_panelTransformIndex], _panel.BoundableTransform);
    }

    private bool CanvasOwnerCurrent()
    {
        if (!ReferenceEquals(_input.UserInterfaceCanvas, _canvas)) return false;
        if (_canvas is null)
            return _canvasNode is null && _canvasTransform is null && _canvasInput is null;
        if (_canvasNode is null || _canvasTransform is null || _canvasIdentity?.IsCurrent != true ||
            _canvasNodeIdentity?.IsCurrent != true || _canvasTransformIdentity?.IsCurrent != true ||
            _canvas.IsDestroyed || _canvas.IsDestroyQueued || _canvasNode.IsDestroyed || _canvasNode.IsDestroyQueued ||
            _canvasNode.IsTransformNull || _canvasTransform.IsDestroyed || _canvasTransform.IsDestroyQueued ||
            !ReferenceEquals(_canvas.SceneNode, _canvasNode) || !ReferenceEquals(_canvasNode.Transform, _canvasTransform) ||
            !ReferenceEquals(_canvas.World, _canvasWorld) || !ReferenceEquals(_canvasNode.World, _canvasNodeWorld) ||
            !ReferenceEquals(_canvasTransform.World, _canvasTransformWorld) ||
            !ReferenceEquals(_canvas.GetInputComponent(), _canvasInput))
            return false;
        if (_canvasInput is null)
            return _canvasInputOwner is null;
        if (_canvasInputOwner is null)
            return true;
        if (_canvasInputIdentity?.IsCurrent != true || _canvasInputOwner.IsDestroyed ||
            _canvasInputOwner.IsDestroyQueued)
            return false;
        if (_canvasInputOwner is not XRComponent component)
            return true;
        return _canvasInputNode is not null && _canvasInputTransform is not null &&
            _canvasInputNodeIdentity?.IsCurrent == true && _canvasInputTransformIdentity?.IsCurrent == true &&
            !_canvasInputNode.IsDestroyed && !_canvasInputNode.IsDestroyQueued &&
            !_canvasInputNode.IsTransformNull && !_canvasInputTransform.IsDestroyed &&
            !_canvasInputTransform.IsDestroyQueued && ReferenceEquals(component.SceneNode, _canvasInputNode) &&
            ReferenceEquals(_canvasInputNode.Transform, _canvasInputTransform) &&
            ReferenceEquals(_canvasInputNode.World, _canvasInputNodeWorld) &&
            ReferenceEquals(_canvasInputTransform.World, _canvasInputTransformWorld);
    }

    private bool IsInputStateCurrent()
        => _input.TextWriteIntentRevision == _textIntent && _input.Text == _text &&
            _display.TextWriteIntentRevision == _displayIntent && _display.Text == _displayText &&
            _input.CaretWriteIntentRevision == _caretIntent && _input.CursorPosition == _cursor &&
            _input.SelectionAnchor == _anchor && _input.FocusEpoch == _focusEpoch &&
            _input.IsFocused == _focused && _input.PreText == _preText && _input.PostText == _postText &&
            !_input.IsComposing && !_input.CommitOnSubmit && _input.Binding is null &&
            _input.Property is null && _input.Targets is null &&
            ReferenceEquals(_input.UserInterfaceCanvas?.GetInputComponent(), _canvasInput) &&
            (_canvasInput is null || _canvasInput.FocusIntentRevision == _focusIntent &&
                (_focused ? ReferenceEquals(_canvasInput.FocusedComponent, _input) :
                    !ReferenceEquals(_canvasInput.FocusedComponent, _input)));

    private bool SourceChanged(AnimationPanel panel)
        => Selection.PublicationRevision != _selectionRevision ||
            NativeAssetSelection.PublicationRevision != _assetRevision ||
            !ReferenceEquals(Selection.SceneNode, _selectedNode) ||
            _selectedNodeIdentity is { } nodeIdentity &&
                (!nodeIdentity.IsCurrent || _selectedNode!.IsDestroyed || _selectedNode.IsDestroyQueued) ||
            !ReferenceEquals(NativeAssetSelection.SelectedTarget as AnimationClip, _selectedAssetClip) ||
            !ReferenceEquals(Selection.SceneNode?.GetComponent<AnimationClipComponent>(), _selectedComponent) ||
            _componentAnimationChanged || !ReferenceEquals(_selectedComponent?.Animation, _componentClip) ||
            _selectedComponentIdentity is { } componentIdentity &&
                (!componentIdentity.IsCurrent || _selectedComponent!.IsDestroyed || _selectedComponent.IsDestroyQueued) ||
            !ReferenceEquals(panel.CurrentSearchClip, _currentClip) ||
            _currentClipIdentity is { } clipIdentity &&
                (!clipIdentity.IsCurrent || _currentClip!.IsDestroyed || _currentClip.IsDestroyQueued);

    private void RequireResetOwner()
    {
        if (!IsInputOwnerCurrent() || _input.CaretWriteIntentRevision != _caretIntent ||
            _input.FocusEpoch != _focusEpoch || _input.IsFocused != _focused ||
            _input.PreText != _preText || _input.PostText != _postText || _input.IsComposing ||
            _input.CommitOnSubmit || _input.Binding is not null || _input.Property is not null ||
            _input.Targets is not null ||
            _fieldReceipt?.IsCurrent(1, _committedQuery) != true ||
            _panel.AuthoringEpoch != _resumePanelEpoch ||
            !string.Equals(_panel.CommittedPathSearch, _committedQuery, StringComparison.Ordinal) ||
            !ResetSourceCurrent())
            throw new InvalidOperationException("The Animation search field changed during draft restore.");
    }

    private bool ResetWriteStillOwned()
        => IsInputOwnerCurrent() && _input.CaretWriteIntentRevision == _caretIntent &&
            _input.FocusEpoch == _focusEpoch && _input.IsFocused == _focused &&
            _input.PreText == _preText && _input.PostText == _postText &&
            _fieldReceipt?.IsGuardedTextWriteOwned(_committedQuery) == true &&
            ResetSourceCurrent();

    private bool ReleaseOwnerCurrent(AnimationPanel panel)
        => IsInputOwnerCurrent() && panel.AuthoringEpoch == _panelEpoch &&
            ReferenceEquals(panel.CurrentSearchClip, _currentClip) &&
            Selection.PublicationRevision == _selectionRevision &&
            NativeAssetSelection.PublicationRevision == _assetRevision &&
            !_componentAnimationChanged && ReferenceEquals(_selectedComponent?.Animation, _componentClip) &&
            ReferenceEquals(Selection.SceneNode?.GetComponent<AnimationClipComponent>(), _selectedComponent) &&
            (_selectedComponentIdentity is not { } identity || identity.IsCurrent &&
                !_selectedComponent!.IsDestroyed && !_selectedComponent.IsDestroyQueued) &&
            (_selectedNodeIdentity is not { } nodeIdentity || nodeIdentity.IsCurrent &&
                !_selectedNode!.IsDestroyed && !_selectedNode.IsDestroyQueued) &&
            (_currentClipIdentity is not { } clipIdentity || clipIdentity.IsCurrent &&
                !_currentClip!.IsDestroyed && !_currentClip.IsDestroyQueued);

    private static bool IsDescendantOf(TransformBase transform, TransformBase ancestor)
    {
        bool containsAncestor = false;
        for (TransformBase? current = transform; current is not null; current = current.Parent)
        {
            if (current.SceneNode is null) return false;
            containsAncestor |= ReferenceEquals(current, ancestor);
        }
        return containsAncestor;
    }

    private void CaptureResetSource(AnimationPanel panel)
    {
        if (_resetComponentChanged is not null && _resetObservedComponent is not null)
            _resetObservedComponent.PropertyChanged -= _resetComponentChanged;
        _resetComponentChanged = null;
        _resetObservedComponent = null;
        _resetObservedAnimationChangeCount = 0;
        _resetSelectionRevision = Selection.PublicationRevision;
        _resetAssetRevision = NativeAssetSelection.PublicationRevision;
        _resetSelectedNode = Selection.SceneNode;
        _resetSelectedComponent = _resetSelectedNode?.GetComponent<AnimationClipComponent>();
        _resetComponentClip = _resetSelectedComponent?.Animation;
        _resetAssetClip = NativeAssetSelection.SelectedTarget as AnimationClip;
        _resetCurrentClip = panel.CurrentSearchClip;
        _resetSelectedNodeIdentity = _resetSelectedNode is null
            ? null : ObjectConstructionIdentity.Capture(_resetSelectedNode);
        _resetSelectedComponentIdentity = _resetSelectedComponent is null
            ? null : ObjectConstructionIdentity.Capture(_resetSelectedComponent);
        _resetCurrentClipIdentity = _resetCurrentClip is null
            ? null : ObjectConstructionIdentity.Capture(_resetCurrentClip);
        _resetCanvasInput = _input.UserInterfaceCanvas?.GetInputComponent();
        _resetFocusIntent = _resetCanvasInput?.FocusIntentRevision ?? 0;
        _resetComponentAnimationChangeCount = _componentAnimationChangeCount;
        if (_resetSelectedComponent is not null && !ReferenceEquals(_resetSelectedComponent, _selectedComponent))
        {
            _resetObservedComponent = _resetSelectedComponent;
            _resetComponentChanged = (_, args) =>
            {
                if (args.PropertyName == nameof(AnimationClipComponent.Animation))
                    _resetObservedAnimationChangeCount = unchecked(_resetObservedAnimationChangeCount + 1);
            };
            _resetObservedComponent.PropertyChanged += _resetComponentChanged;
        }
    }

    private bool ResetSourceCurrent()
        => _panel.AuthoringEpoch == _resumePanelEpoch &&
            string.Equals(_panel.CommittedPathSearch, _committedQuery, StringComparison.Ordinal) &&
            Selection.PublicationRevision == _resetSelectionRevision &&
            NativeAssetSelection.PublicationRevision == _resetAssetRevision &&
            ReferenceEquals(Selection.SceneNode, _resetSelectedNode) &&
            (_resetSelectedNodeIdentity is not { } nodeIdentity || nodeIdentity.IsCurrent &&
                !_resetSelectedNode!.IsDestroyed && !_resetSelectedNode.IsDestroyQueued) &&
            ReferenceEquals(Selection.SceneNode?.GetComponent<AnimationClipComponent>(), _resetSelectedComponent) &&
            ReferenceEquals(_resetSelectedComponent?.Animation, _resetComponentClip) &&
            (_resetSelectedComponentIdentity is not { } componentIdentity || componentIdentity.IsCurrent &&
                !_resetSelectedComponent!.IsDestroyed && !_resetSelectedComponent.IsDestroyQueued) &&
            ReferenceEquals(NativeAssetSelection.SelectedTarget as AnimationClip, _resetAssetClip) &&
            ReferenceEquals(_panel.CurrentSearchClip, _resetCurrentClip) &&
            (_resetCurrentClipIdentity is not { } clipIdentity || clipIdentity.IsCurrent &&
                !_resetCurrentClip!.IsDestroyed && !_resetCurrentClip.IsDestroyQueued) &&
            ReferenceEquals(_input.UserInterfaceCanvas?.GetInputComponent(), _resetCanvasInput) &&
            (_resetCanvasInput?.FocusIntentRevision ?? 0) == _resetFocusIntent &&
            _componentAnimationChangeCount == _resetComponentAnimationChangeCount &&
            _resetObservedAnimationChangeCount == 0;

    private void DisposeCore()
    {
        if (_disposed) return;
        _disposed = true;
        if (_componentChanged is not null && _selectedComponent is not null)
            _selectedComponent.PropertyChanged -= _componentChanged;
        if (_resetComponentChanged is not null && _resetObservedComponent is not null)
            _resetObservedComponent.PropertyChanged -= _resetComponentChanged;
    }

    public void Dispose() => DisposeCore();
}

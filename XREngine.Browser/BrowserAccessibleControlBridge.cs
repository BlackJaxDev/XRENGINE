using XREngine.Components;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Scene;

namespace XREngine.Browser;

/// <summary>Retains a bounded, ordered projection of engine-owned interactive UI.</summary>
internal sealed class BrowserAccessibleControlBridge
{
    private const int MaximumControls = 256;
    private const int MaximumCanvases = 64;
    private const int MaximumScannedNodes = 16_384;
    private static int _nextGeneration;
    private readonly Dictionary<UIInteractableComponent, Entry> _known = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _ordered = new(MaximumControls);
    private readonly List<Entry> _nextOrder = new(MaximumControls);
    private readonly Stack<Entry> _recycled = new(MaximumControls);
    private readonly List<UICanvasInputComponent> _activeInputs = new(MaximumCanvases);
    private readonly XRComponent[] _inputScratch = new XRComponent[MaximumCanvases];
    private readonly Stack<SceneNode> _nodeScratch = new(MaximumScannedNodes);
    private int _revision = 1;
    private int _scan;
    private int _scannedNodes;
    private bool _refreshChanged;
    private RuntimeWorld? _world;
    private XRViewport? _viewport;
    private IPawnController? _player;

    private sealed class Entry
    {
        public UIInteractableComponent Target = null!;
        public int Generation;
        public int Version;
        public int Seen;
        public EUIAccessibilityRole Role;
        public string Name = string.Empty;
        public bool ReadOnly;
        public bool Multiline;
        public int Checked;
        public bool Focused;
        public float X, Y, Width, Height;
    }

    public int Count => _ordered.Count;

    public int Refresh(RuntimeWorld? world, XRViewport? viewport, IPawnController? player)
    {
        _world = world;
        _viewport = viewport;
        _player = player;
        _nextOrder.Clear();
        _activeInputs.Clear();
        _scan++;
        _scannedNodes = 0;
        _refreshChanged = false;
        if (world is not null && viewport is not null &&
            player?.ControlledPawnComponent is IRuntimeInputControllablePawn pawn)
        {
            int inputCount = pawn.LinkedUiInputComponents.Count;
            if (inputCount <= MaximumCanvases)
            {
                pawn.LinkedUiInputComponents.CopyTo(_inputScratch, 0);
                try
                {
                    for (int index = 0; index < inputCount; index++)
                    {
                        if (_inputScratch[index] is not UICanvasInputComponent
                            { IsActiveInHierarchy: true } input ||
                            !ReferenceEquals(input.OwningPawn?.Controller, player) ||
                            input.GetCameraCanvas() is not { IsActiveInHierarchy: true } canvas ||
                            !ReferenceEquals(canvas.World, world))
                            continue;
                        _activeInputs.Add(input);
                    }
                    for (int index = 0; index < _activeInputs.Count; index++)
                    {
                        if (_activeInputs[index].GetCameraCanvas() is not { } canvas)
                            continue;
                        VisitCanvas(canvas);
                        if (_nextOrder.Count == MaximumControls || _scannedNodes == MaximumScannedNodes)
                            break;
                    }
                }
                finally
                {
                    _inputScratch.AsSpan(0, inputCount).Clear();
                }
            }
        }

        bool changed = _refreshChanged || _ordered.Count != _nextOrder.Count;
        int shared = Math.Min(_ordered.Count, _nextOrder.Count);
        for (int index = 0; index < shared; index++)
            changed |= !ReferenceEquals(_ordered[index], _nextOrder[index]);

        for (int index = 0; index < _ordered.Count; index++)
        {
            Entry entry = _ordered[index];
            if (entry.Seen == _scan)
                continue;
            _known.Remove(entry.Target);
            entry.Target = null!;
            entry.Name = string.Empty;
            _recycled.Push(entry);
        }
        _ordered.Clear();
        for (int index = 0; index < _nextOrder.Count; index++)
            _ordered.Add(_nextOrder[index]);
        if (changed)
            _revision++;
        return _revision;
    }

    private void VisitCanvas(UICanvasComponent canvas)
    {
        if (canvas.SceneNode is not { } root)
            return;
        _nodeScratch.Clear();
        _nodeScratch.Push(root);
        while (_nodeScratch.Count != 0 && _scannedNodes < MaximumScannedNodes &&
            _nextOrder.Count < MaximumControls)
        {
            _scannedNodes++;
            SceneNode node = _nodeScratch.Pop();
            if (!node.IsActiveInHierarchy)
                continue;
            var components = node.Components;
            for (int index = 0; index < components.Count && _nextOrder.Count < MaximumControls; index++)
            {
                if (components[index] is UIInteractableComponent target &&
                    target.AccessibilityRole != EUIAccessibilityRole.None &&
                    !target.AccessibilityHidden && target.IsActiveInHierarchy &&
                    target.UITransform.IsVisibleInHierarchy &&
                    ReferenceEquals(target.UserInterfaceCanvas, canvas) &&
                    BrowserUiBoundsProjection.TryProjectVisible(target, _viewport,
                        out float x, out float y, out float width, out float height))
                    Add(target, x, y, width, height);
            }
            var children = node.Transform.Children;
            for (int index = children.Count - 1; index >= 0 &&
                _nodeScratch.Count < MaximumScannedNodes; index--)
                if (children[index]?.SceneNode is { } child)
                    _nodeScratch.Push(child);
        }
        _nodeScratch.Clear();
    }

    private void Add(UIInteractableComponent target, float x, float y, float width, float height)
    {
        if (_known.TryGetValue(target, out Entry? known) && known.Seen == _scan)
            return;
        bool isNew = !_known.TryGetValue(target, out Entry? entry);
        if (isNew)
        {
            entry = _recycled.Count != 0 ? _recycled.Pop() : new Entry();
            entry.Target = target;
            entry.Generation = Interlocked.Increment(ref _nextGeneration);
            entry.Version = 0;
            _known.Add(target, entry);
        }
        Entry current = entry!;
        EUIAccessibilityRole role = target.AccessibilityRole;
        string name = target.AccessibilityName;
        bool readOnly = target is UITextInputComponent text && !text.RegisterInputsOnFocus;
        bool multiline = target is UITextInputComponent textInput && !textInput.SingleLineMode;
        int checkedState = target is UIToggleComponent toggle ? (int)toggle.LastState : 0;
        bool focused = ReferenceEquals(target, _player?.FocusedInteractable);
        bool changed = isNew || current.Role != role || current.Name != name ||
            current.ReadOnly != readOnly || current.Multiline != multiline ||
            current.Checked != checkedState || current.Focused != focused ||
            current.X != x || current.Y != y || current.Width != width || current.Height != height;
        current.Role = role;
        current.Name = name;
        current.ReadOnly = readOnly;
        current.Multiline = multiline;
        current.Checked = checkedState;
        current.Focused = focused;
        current.X = x;
        current.Y = y;
        current.Width = width;
        current.Height = height;
        current.Seen = _scan;
        if (changed)
        {
            current.Version++;
            _refreshChanged = true;
        }
        _nextOrder.Add(current);
    }

    public void Clear()
    {
        _known.Clear();
        _ordered.Clear();
        _nextOrder.Clear();
        _recycled.Clear();
        _activeInputs.Clear();
        _nodeScratch.Clear();
        _inputScratch.AsSpan().Clear();
        _player = null;
        _viewport = null;
        _world = null;
        _revision++;
    }

    private Entry? At(int index) => index >= 0 && index < _ordered.Count ? _ordered[index] : null;
    public int Generation(int index) => At(index)?.Generation ?? 0;
    public int Version(int index) => At(index)?.Version ?? 0;
    public int Role(int index) => (int)(At(index)?.Role ?? EUIAccessibilityRole.None);
    public string Name(int index) => At(index)?.Name ?? string.Empty;
    public bool ReadOnly(int index) => At(index)?.ReadOnly ?? false;
    public bool Multiline(int index) => At(index)?.Multiline ?? false;
    public int Checked(int index) => At(index)?.Checked ?? 0;
    public bool Focused(int index) => At(index)?.Focused ?? false;
    public float X(int index) => At(index)?.X ?? -1;
    public float Y(int index) => At(index)?.Y ?? -1;
    public float Width(int index) => At(index)?.Width ?? 0;
    public float Height(int index) => At(index)?.Height ?? 0;

    public bool Focus(int generation)
    {
        Entry? entry = Find(generation);
        if (entry is null || !StillAvailable(entry.Target) ||
            FindInput(entry.Target.UserInterfaceCanvas) is not { } input)
            return false;
        for (int index = 0; index < _activeInputs.Count; index++)
        {
            UICanvasInputComponent other = _activeInputs[index];
            if (!ReferenceEquals(other, input) && other.FocusedComponent is not null)
                other.FocusedComponent = null;
        }
        if (ReferenceEquals(input.FocusedComponent, entry.Target) &&
            !ReferenceEquals(_player?.FocusedInteractable, entry.Target))
            input.FocusedComponent = null;
        input.FocusedComponent = entry.Target;
        return ReferenceEquals(_player?.FocusedInteractable, entry.Target);
    }

    public bool Activate(int generation)
    {
        Entry? entry = Find(generation);
        if (entry is null || entry.Role is not (EUIAccessibilityRole.Button or EUIAccessibilityRole.CheckBox) ||
            !Focus(generation))
            return false;
        return entry.Target.AccessibilityActivate();
    }

    private Entry? Find(int generation)
    {
        if (generation == 0)
            return null;
        for (int index = 0; index < _ordered.Count; index++)
            if (_ordered[index].Generation == generation)
                return _ordered[index];
        return null;
    }

    private UICanvasInputComponent? FindInput(UICanvasComponent? canvas)
    {
        for (int index = 0; index < _activeInputs.Count; index++)
        {
            UICanvasInputComponent input = _activeInputs[index];
            if (input.IsActiveInHierarchy && ReferenceEquals(input.OwningPawn?.Controller, _player) &&
                ReferenceEquals(input.GetCameraCanvas(), canvas))
                return input;
        }
        return null;
    }

    public bool CanEdit(UITextInputComponent target) => StillAvailable(target);

    private bool StillAvailable(UIInteractableComponent target)
        => ReferenceEquals(target.World, _world) && target.IsActiveInHierarchy &&
           target.UserInterfaceCanvas is { IsActiveInHierarchy: true } &&
           FindInput(target.UserInterfaceCanvas) is not null &&
           target.AccessibilityRole != EUIAccessibilityRole.None && !target.AccessibilityHidden &&
           target.UITransform.IsVisibleInHierarchy &&
           BrowserUiBoundsProjection.TryProjectVisible(target, _viewport, out _, out _, out _, out _);
}

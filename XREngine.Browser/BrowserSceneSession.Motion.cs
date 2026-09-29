using System.Numerics;
using XREngine.Rendering;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Browser;

public sealed partial class BrowserSceneSession
{
    private readonly BrowserPointerSnapshot[] _pointerSlots = new BrowserPointerSnapshot[8];
    private BrowserKinematicCharacter? _character;
    private Vector2 _movement;
    private Vector2 _look;
    private Vector2 _lookDelta;
    private float _yaw;
    private float _pitch;
    private bool _jumpHeld;
    private bool _jumpQueued;
    private double _wheelPixels;

    public float MotionSpeed => _character?.Speed ?? 0;
    public bool MotionGrounded => _character?.Grounded ?? false;
    public string PhysicsProfile => _character is null ? "excluded-no-collision-descriptors" : "static-aabb-character-v1";
    public Vector3 MotionPosition => _character is not null && _cameraOverride is null ? _character.Eye : GetImportedEye();
    public Vector3 MotionForward => _character is null || _cameraOverride is not null ? GetImportedForward() :
        new(MathF.Sin(_yaw) * MathF.Cos(_pitch), MathF.Sin(_pitch), -MathF.Cos(_yaw) * MathF.Cos(_pitch));
    public ReadOnlySpan<BrowserPointerSnapshot> Pointers => _pointerSlots;
    public double WheelPixels => _wheelPixels;

    private void InitializeMotion()
    {
        ResetMotionInput();
        if (_snapshot is not null || _cookedContent)
            return;
        // These explicit proxies are the entire selected sample collision world. Decorative
        // rotating meshes are non-solid; arbitrary imported content never inherits this world.
        BrowserCollisionBox[] boxes =
        [
            new(new(-8, -1.8f, -12), new(8, -1.55f, 4)),
            new(new(-8.2f, -1.55f, -12.2f), new(-8, 3, 4.2f)),
            new(new(8, -1.55f, -12.2f), new(8.2f, 3, 4.2f)),
            new(new(-8, -1.55f, -12.2f), new(8, 3, -12)),
            new(new(-8, -1.55f, 4), new(8, 3, 4.2f)),
            new(new(-3, -1.55f, -4), new(-1.7f, -0.5f, -2.5f)),
            new(new(2, -1.55f, -7), new(3.4f, 0.1f, -5.5f))
        ];
        _character = new BrowserKinematicCharacter(new Vector3(0, -0.699f, 0), boxes);
        BrowserMaterialData material = new(new Vector4(0.2f, 0.3f, 0.38f, 1), shading: "lambert");
        foreach (BrowserCollisionBox box in boxes)
            AddMotionBox(box, material);
        ReservePacket();
    }

    private void AddMotionBox(BrowserCollisionBox box, BrowserMaterialData material)
    {
        Transform transform = (Transform)BrowserStaticRegistrations.CreateRequiredTransform(
            BrowserStaticRegistrations.TransformId);
        transform.Translation = (box.Minimum + box.Maximum) * 0.5f;
        // The shared cube has width 0.8. Nodes live at world root so sample spin cannot move colliders.
        transform.Scale = (box.Maximum - box.Minimum) / 0.8f;
        SceneNode node = new("BrowserStaticCollider", transform);
        _host.RootNodes.Add(node);
        BrowserMeshComponent component = (BrowserMeshComponent)BrowserStaticRegistrations.AddRequiredComponent(
            BrowserStaticRegistrations.MeshComponentId, node);
        component.Mesh = _cubeMesh!;
        component.Material = material;
        _customRenderables.Add(component);
    }

    public void InputActions(double moveX, double moveY, double lookX, double lookY, bool jump)
    {
        ThrowIfFrameBusy();
        Vector2 movement = new(CheckedAction(moveX), CheckedAction(moveY));
        Vector2 look = new(CheckedAction(lookX), CheckedAction(lookY));
        _movement = movement;
        _look = look;
        _jumpQueued |= jump && !_jumpHeld;
        _jumpHeld = jump;
    }

    private static float CheckedAction(double value)
    {
        if (!double.IsFinite(value) || value is < -1 or > 1)
            throw new ArgumentOutOfRangeException(nameof(value), "Input actions must be finite and normalized.");
        return (float)value;
    }

    public void InputLookDelta(double x, double y)
    {
        ThrowIfFrameBusy();
        Vector2 delta = new(CheckedAction(x), CheckedAction(y));
        _lookDelta = Vector2.Clamp(_lookDelta + delta * 4.4f,
            new Vector2(-MathF.PI), new Vector2(MathF.PI));
    }

    public void InputPointer(int pointerId, int phase, double x, double y, int kind)
    {
        ThrowIfFrameBusy();
        if (pointerId < 0 || phase is < 0 or > 3 || kind is < 0 or > 2 ||
            !double.IsFinite(x) || !double.IsFinite(y) || x is < 0 or > 1 || y is < 0 or > 1)
            throw new ArgumentException("Pointer snapshot is invalid.");
        int slot = -1, free = -1;
        for (int i = 0; i < _pointerSlots.Length; i++)
        {
            if (_pointerSlots[i].Active && _pointerSlots[i].Id == pointerId)
                slot = i;
            if (!_pointerSlots[i].Active && free < 0)
                free = i;
        }
        if (slot < 0)
        {
            if (phase != 0)
                return; // A late release after focus cancellation has no active owner.
            slot = free;
            if (slot < 0)
                return; // Same explicit eight-pointer budget as the browser collector.
        }
        _pointerSlots[slot] = new BrowserPointerSnapshot(pointerId, phase, (float)x, (float)y, kind);
    }

    public void InputWheel(double pixels)
    {
        ThrowIfFrameBusy();
        if (!double.IsFinite(pixels) || Math.Abs(pixels) > 240)
            throw new ArgumentOutOfRangeException(nameof(pixels));
        _wheelPixels = Math.Clamp(_wheelPixels + pixels, -4096, 4096);
    }

    public double ConsumeWheel()
    {
        double pixels = _wheelPixels;
        _wheelPixels = 0;
        return pixels;
    }

    public void ResetInput()
    {
        ThrowIfFrameBusy();
        ResetMotionInput();
    }

    private void ResetMotionInput()
    {
        _movement = default;
        _look = default;
        _lookDelta = default;
        _jumpHeld = false;
        _jumpQueued = false;
        _wheelPixels = 0;
        for (int i = 0; i < _pointerSlots.Length; i++)
            _pointerSlots[i] = _pointerSlots[i] with { Phase = 3 };
    }

    private void AdvanceMotion(float deltaSeconds)
    {
        if (_character is null || _cameraOverride is not null)
            return;
        _yaw = MathF.IEEERemainder(_yaw + _lookDelta.X + _look.X * 2.2f * deltaSeconds, MathF.Tau);
        _pitch = Math.Clamp(_pitch + _lookDelta.Y + _look.Y * 1.8f * deltaSeconds, -1.4f, 1.4f);
        _lookDelta = default;
        _character.Step(deltaSeconds, _movement, _yaw, _jumpQueued);
        _jumpQueued = false;
        RebuildViews();
    }

    private bool TryBuildMotionViews(int width, int height)
    {
        if (_character is null || _cameraOverride is not null)
            return false;
        Vector3 eye = MotionPosition;
        Matrix4x4 view = Matrix4x4.CreateLookAt(eye, eye + MotionForward, Vector3.UnitY);
        int leftWidth = _splitView ? width / 2 : width;
        _left = BrowserViewport.Create(0, 0, leftWidth, height, view);
        _right = _splitView ? BrowserViewport.Create(leftWidth, 0, width - leftWidth, height, view) : default;
        return true;
    }

    private Vector3 GetImportedEye()
    {
        Matrix4x4 view = _cameraOverride?.View ?? _snapshot?.Camera.View ?? Matrix4x4.Identity;
        return Matrix4x4.Invert(view, out Matrix4x4 world) ? world.Translation : Vector3.Zero;
    }

    private Vector3 GetImportedForward()
    {
        Matrix4x4 view = _cameraOverride?.View ?? _snapshot?.Camera.View ?? Matrix4x4.Identity;
        if (!Matrix4x4.Invert(view, out Matrix4x4 world))
            return -Vector3.UnitZ;
        Vector3 direction = Vector3.TransformNormal(-Vector3.UnitZ, world);
        return direction.LengthSquared() > 1e-10f ? Vector3.Normalize(direction) : -Vector3.UnitZ;
    }

    private void DisposeMotion()
    {
        ResetMotionInput();
        _character = null;
    }
}

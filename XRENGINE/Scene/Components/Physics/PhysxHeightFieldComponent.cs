using XREngine.Core.Attributes;
using XREngine.Rendering.Physics.Physx;
using XREngine.Components.Physics;

namespace XREngine.Components;

[RequireComponents(typeof(DynamicRigidBodyComponent))]
public class PhysxHeightFieldComponent : XRComponent
{
    private float _heightScale = 1.0f;
    private float _rowScale = 1.0f;
    private float _columnScale = 1.0f;
    private bool _tightBounds = false;
    private bool _doubleSided = false;

    public DynamicRigidBodyComponent RigidBodyComponent => GetSiblingComponent<DynamicRigidBodyComponent>(true)!;

    public PhysxHeightField? HeightField { get; set; }
    public void LoadHeightField(string imagePath)
    {
        HeightField = new PhysxHeightField(imagePath);
    }
    public void ReleaseHeightField()
    {
        HeightField?.Release();
        HeightField = null;
    }

    public float HeightScale
    {
        get => _heightScale;
        set => SetField(ref _heightScale, value);
    }
    public float RowScale
    {
        get => _rowScale;
        set => SetField(ref _rowScale, value);
    }
    public float ColumnScale
    {
        get => _columnScale;
        set => SetField(ref _columnScale, value);
    }
    public bool TightBounds
    {
        get => _tightBounds;
        set => SetField(ref _tightBounds, value);
    }
    public bool DoubleSided
    {
        get => _doubleSided;
        set => SetField(ref _doubleSided, value);
    }

    protected internal unsafe override void OnComponentActivated()
    {
        base.OnComponentActivated();

        if (HeightField is null)
            return;
        
        var mat = new PhysxMaterial(0.5f, 0.5f, 0.1f);
        IPhysicsGeometry.HeightField hf = new(HeightField.HeightFieldPtr, HeightScale, RowScale, ColumnScale, TightBounds, DoubleSided);
        RigidBodyComponent.RigidBody = new PhysxDynamicRigidBody(mat, hf, 1.0f);
    }
    protected internal override void OnComponentDeactivated()
    {
        base.OnComponentDeactivated();
    }
}
using Extensions;
using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Core.Attributes;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Vectors;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Actors.Types
{
    [RequiresTransform(typeof(DrivenWorldTransform))]
    public class TransformTool3D : XRComponent, IRenderable
    {
        public DrivenWorldTransform RootTransform => SceneNode.GetTransformAs<DrivenWorldTransform>(true)!;
        
        public RenderInfo3D RenderInfo { get; }

        private ETransformSpace _transformSpace = ETransformSpace.World;
        public ETransformSpace TransformSpace
        {
            get => _transformSpace;
            set => SetField(ref _transformSpace, value);
        }

        private ETransformType _mode = ETransformType.Translate;
        public ETransformType TransformMode
        {
            get => _mode;
            set => SetField(ref _mode, value);
        }

        /// <summary>
        /// The root node of the transformation tool, if spawned, containing all of the tool's components.
        /// </summary>
        private static SceneNode? _instanceNode;
        public static SceneNode? InstanceNode => _instanceNode;

        /// <summary>
        /// Removes the current instance of the transformation tool from the scene.
        /// </summary>
        public static void DestroyInstance()
        {
            _instanceNode?.Destroy();
            _instanceNode = null;
        }

        /// <summary>
        /// Spawns and retrieves the transformation tool in the current scene.
        /// </summary>
        /// <param name="world">The world to spawn the transform tool in.</param>
        /// <param name="comp"></param>
        /// <param name="transformType"></param>
        /// <returns></returns>
        public static TransformTool3D? GetInstance(TransformBase comp, ETransformType transformType)
        {
            XRWorldInstance? world = comp?.World;
            if (world is null)
                return null;

            if (_instanceNode?.World != world)
            {
                _instanceNode?.Destroy();
                _instanceNode = new SceneNode(world);
            }

            TransformTool3D tool = _instanceNode.GetOrAddComponent<TransformTool3D>(out _)!;
            tool.TargetSocket = comp;
            tool.TransformMode = transformType;

            return tool;
        }

        public TransformTool3D() : base()
        {
            TransformSpace = ETransformSpace.World;
            _rc = new RenderCommandMethod3D((int)EDefaultRenderPass.OnTopForward, Render);
            RenderInfo = RenderInfo3D.New(this, _rc);
            RenderedObjects = [RenderInfo];
            UpdateModelComponent();
        }

        protected internal override void OnComponentActivated()
        {
            base.OnComponentActivated();
        }
        protected internal override void OnComponentDeactivated()
        {
            base.OnComponentDeactivated();
        }

        public event Action? MouseDown, MouseUp;

        private readonly RenderCommandMethod3D _rc;

        private readonly XRMaterial[] _axisMat = new XRMaterial[3];
        private readonly XRMaterial[] _transPlaneMat = new XRMaterial[6];
        private readonly XRMaterial[] _scalePlaneMat = new XRMaterial[3];
        private XRMaterial? _screenMat;

        public Matrix4x4 PrevRootWorldMatrix { get; private set; } = Matrix4x4.Identity;
        public RenderInfo[] RenderedObjects { get; }

        private ModelComponent? _translationModel;
        private ModelComponent? _nonRotationModel;
        private ModelComponent? _scaleModel;
        private ModelComponent? _rotationModel;
        private ModelComponent? _screenRotationModel;
        private ModelComponent? _screenTranslationModel;

        protected void UpdateModelComponent()
        {
            GenerateMeshes(
                out var translationMeshes,
                out var nonRotationMeshes,
                out var scaleMeshes,
                out var rotationMeshes,
                out var screenRotationMeshes,
                out var screenTranslationMeshes);

            //Generate skeleton: root node should scale by distance
            SceneNode skelRoot = SceneNode.NewChild();

            BillboardTransform rootBillboardTfm = skelRoot.GetTransformAs<BillboardTransform>(true)!;
            rootBillboardTfm.BillboardActive = false;
            rootBillboardTfm.Perspective = true;
            rootBillboardTfm.ScaleByDistance = true;
            rootBillboardTfm.ScaleReferenceDistance = 10.0f;

            ModelComponent translationModelComp = skelRoot.AddComponent<ModelComponent>("Translation Model")!;
            translationModelComp.Model = new Model(translationMeshes);
            _translationModel = translationModelComp;

            ModelComponent nonRotationModelComp = skelRoot.AddComponent<ModelComponent>("Non-Rotation Model")!;
            nonRotationModelComp.Model = new Model(nonRotationMeshes);
            _nonRotationModel = nonRotationModelComp;

            ModelComponent scaleModelComp = skelRoot.AddComponent<ModelComponent>("Scale Model")!;
            scaleModelComp.Model = new Model(scaleMeshes);
            _scaleModel = scaleModelComp;

            ModelComponent rotationModelComp = skelRoot.AddComponent<ModelComponent>("Rotation Model")!;
            rotationModelComp.Model = new Model(rotationMeshes);
            _rotationModel = rotationModelComp;

            SceneNode screenNode = skelRoot.NewChild();
            BillboardTransform screenBillboard = screenNode.GetTransformAs<BillboardTransform>(true)!;
            screenBillboard.Perspective = false;
            screenBillboard.BillboardActive = true;

            ModelComponent screenRotationModelComp = screenNode.AddComponent<ModelComponent>("Screen Rotation Model")!;
            screenRotationModelComp.Model = new Model(screenRotationMeshes);
            _screenRotationModel = screenRotationModelComp;

            ModelComponent screenTranslationModelComp = screenNode.AddComponent<ModelComponent>("Screen Translation Model")!;
            screenTranslationModelComp.Model = new Model(screenTranslationMeshes);
            _screenTranslationModel = screenTranslationModelComp;

            ModeChanged();
        }

        private void GenerateMeshes(
            out List<SubMesh> translationMeshes,
            out List<SubMesh> nonRotationMeshes,
            out List<SubMesh> scaleMeshes,
            out List<SubMesh> rotationMeshes,
            out List<SubMesh> screenRotationMeshes,
            out List<SubMesh> screenTranslationMeshes)
        {
            translationMeshes = [];
            nonRotationMeshes = [];
            scaleMeshes = [];
            rotationMeshes = [];
            screenRotationMeshes = [];
            screenTranslationMeshes = [];

            _screenMat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.LightGray);
            _screenMat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
            //_screenMat.RenderOptions.LineWidth = 1.0f;

            GetSphere(rotationMeshes);

            for (int normalAxis = 0; normalAxis < 3; ++normalAxis)
            {
                GetUnits(
                    normalAxis,
                    out Vector3 unit,
                    out Vector3 unit1,
                    out Vector3 unit2);

                GetMaterials(
                    normalAxis,
                    unit,
                    unit1,
                    unit2,
                    out XRMaterial axisMat,
                    out XRMaterial planeMat1,
                    out XRMaterial planeMat2,
                    out XRMaterial scalePlaneMat);

                GetLines(
                    unit,
                    unit1,
                    unit2,
                    out VertexLine axisLine,
                    out VertexLine transLine1,
                    out VertexLine transLine2,
                    out VertexLine scaleLine1,
                    out VertexLine scaleLine2);

                GetMeshes(
                    unit,
                    axisLine,
                    transLine1,
                    transLine2,
                    scaleLine1,
                    scaleLine2,
                    out XRMesh axisPrim,
                    out XRMesh arrowPrim,
                    out XRMesh transPrim1,
                    out XRMesh transPrim2,
                    out XRMesh scalePrim,
                    out XRMesh rotPrim);

                //isRotate = false
                nonRotationMeshes.Add(new SubMesh(axisPrim, axisMat));
                nonRotationMeshes.Add(new SubMesh(arrowPrim, axisMat));

                //isTranslate = true
                translationMeshes.Add(new SubMesh(transPrim1, planeMat1));
                translationMeshes.Add(new SubMesh(transPrim2, planeMat2));

                //isScale = true
                scaleMeshes.Add(new SubMesh(scalePrim, scalePlaneMat));

                //isRotate = true
                rotationMeshes.Add(new SubMesh(rotPrim, axisMat));
            }

            //Screen-aligned rotation: view-aligned circle around the center
            var screenRotPrim = XRMesh.Shapes.WireframeCircle(_circRadius, Vector3.UnitZ, Vector3.Zero, _circlePrecision);

            //Screen-aligned translation: small view-aligned square at the center
            Vertex v1 = new Vector3(-_screenTransExtent, -_screenTransExtent, 0.0f);
            Vertex v2 = new Vector3(_screenTransExtent, -_screenTransExtent, 0.0f);
            Vertex v3 = new Vector3(_screenTransExtent, _screenTransExtent, 0.0f);
            Vertex v4 = new Vector3(-_screenTransExtent, _screenTransExtent, 0.0f);
            VertexLineStrip strip = new(true, v1, v2, v3, v4);
            var screenTransPrim = XRMesh.Create(strip);

            //isRotate = true
            screenRotationMeshes.Add(new SubMesh(screenRotPrim, _screenMat));
            //isTranslate = true
            screenTranslationMeshes.Add(new SubMesh(screenTransPrim, _screenMat));
        }

        private static void GetMeshes(Vector3 unit, VertexLine axisLine, VertexLine transLine1, VertexLine transLine2, VertexLine scaleLine1, VertexLine scaleLine2, out XRMesh axisPrim, out XRMesh arrowPrim, out XRMesh transPrim1, out XRMesh transPrim2, out XRMesh scalePrim, out XRMesh rotPrim)
        {
            //string axis = ((char)('X' + normalAxis)).ToString();

            float coneHeight = _axisLength - _coneDistance;

            axisPrim = XRMesh.Create(axisLine)!;
            arrowPrim = XRMesh.Shapes.SolidCone(unit * (_coneDistance + coneHeight * 0.5f), unit, coneHeight, _coneRadius, 6, false);
            transPrim1 = XRMesh.Create(transLine1);
            transPrim2 = XRMesh.Create(transLine2);
            scalePrim = XRMesh.Create(scaleLine1, scaleLine2);
            rotPrim = XRMesh.Shapes.WireframeCircle(_orbRadius, unit, Vector3.Zero, _circlePrecision);
        }

        private static void GetLines(Vector3 unit, Vector3 unit1, Vector3 unit2, out VertexLine axisLine, out VertexLine transLine1, out VertexLine transLine2, out VertexLine scaleLine1, out VertexLine scaleLine2)
        {
            axisLine = new(Vector3.Zero, unit * _axisLength);
            Vector3 halfUnit = unit * _axisHalfLength;

            transLine1 = new(halfUnit, halfUnit + unit1 * _axisHalfLength);
            transLine1.Vertex0.ColorSets.Add(new Vector4(unit1, 1.0f));
            transLine1.Vertex1.ColorSets.Add(new Vector4(unit1, 1.0f));

            transLine2 = new(halfUnit, halfUnit + unit2 * _axisHalfLength);
            transLine2.Vertex0.ColorSets.Add(new Vector4(unit2, 1.0f));
            transLine2.Vertex1.ColorSets.Add(new Vector4(unit2, 1.0f));

            scaleLine1 = new(unit1 * _scaleHalf1LDist, unit2 * _scaleHalf1LDist);
            scaleLine1.Vertex0.ColorSets.Add(new Vector4(unit, 1.0f));
            scaleLine1.Vertex1.ColorSets.Add(new Vector4(unit, 1.0f));

            scaleLine2 = new(unit1 * _scaleHalf2LDist, unit2 * _scaleHalf2LDist);
            scaleLine2.Vertex0.ColorSets.Add(new Vector4(unit, 1.0f));
            scaleLine2.Vertex1.ColorSets.Add(new Vector4(unit, 1.0f));
        }

        private void GetMaterials(int normalAxis, Vector3 unit, Vector3 unit1, Vector3 unit2, out XRMaterial axisMat, out XRMaterial planeMat1, out XRMaterial planeMat2, out XRMaterial scalePlaneMat)
        {
            axisMat = XRMaterial.CreateUnlitColorMaterialForward(unit);
            axisMat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
            //axisMat.RenderOptions.LineWidth = 1.0f;
            _axisMat[normalAxis] = axisMat;

            planeMat1 = XRMaterial.CreateUnlitColorMaterialForward(unit1);
            planeMat1.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
            //planeMat1.RenderOptions.LineWidth = 1.0f;
            _transPlaneMat[(normalAxis << 1) + 0] = planeMat1;

            planeMat2 = XRMaterial.CreateUnlitColorMaterialForward(unit2);
            planeMat2.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
            //planeMat2.RenderOptions.LineWidth = 1.0f;
            _transPlaneMat[(normalAxis << 1) + 1] = planeMat2;

            scalePlaneMat = XRMaterial.CreateUnlitColorMaterialForward(unit);
            scalePlaneMat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
            //scalePlaneMat.RenderOptions.LineWidth = 1.0f;
            _scalePlaneMat[normalAxis] = scalePlaneMat;
        }

        private static void GetUnits(int normalAxis, out Vector3 unit, out Vector3 unit1, out Vector3 unit2)
        {
            int planeAxis1 = normalAxis + 1 - (normalAxis >> 1) * 3; //0 = 1, 1 = 2, 2 = 0
            int planeAxis2 = planeAxis1 + 1 - (normalAxis & 1) * 3; //0 = 2, 1 = 0, 2 = 1

            unit = Vector3.Zero;
            unit[normalAxis] = 1.0f;

            unit1 = Vector3.Zero;
            unit1[planeAxis1] = 1.0f;

            unit2 = Vector3.Zero;
            unit2[planeAxis2] = 1.0f;
        }

        private static void GetSphere(List<SubMesh> rotationMeshes)
        {
            XRMaterial sphereMat = XRMaterial.CreateUnlitColorMaterialForward(ColorF4.Orange);
            sphereMat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Enabled;
            sphereMat.RenderOptions.DepthTest.UpdateDepth = true;
            sphereMat.RenderOptions.DepthTest.Function = EComparison.Lequal;
            //sphereMat.RenderOptions.LineWidth = 1.0f;
            sphereMat.RenderOptions.WriteRed = false;
            sphereMat.RenderOptions.WriteGreen = false;
            sphereMat.RenderOptions.WriteBlue = false;
            sphereMat.RenderOptions.WriteAlpha = false;

            XRMesh spherePrim = XRMesh.Shapes.SolidSphere(Vector3.Zero, _orbRadius, 10, 10);
            //isRotate = true
            rotationMeshes.Add(new SubMesh(spherePrim, sphereMat));
        }

        private TransformBase? _targetSocket = null;

        private void UpdateScreenSpace()
        {
            if (_targetSocket != null)
                SocketTransformChanged(null);
        }

        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(TransformMode):
                    ModeChanged();
                    break;
                case nameof(TransformSpace):
                    SetWorldMatrices(GetSocketSpacialTransform(), GetSocketSpacialTransformInverse());
                    //_dragMatrix = RootComponent.WorldMatrix;
                    //_invDragMatrix = RootComponent.InverseWorldMatrix;
                    if (_transformSpace == ETransformSpace.Screen)
                        RegisterTick(ETickGroup.PostPhysics, ETickOrder.Logic, UpdateScreenSpace);
                    else
                        UnregisterTick(ETickGroup.PostPhysics, ETickOrder.Logic, UpdateScreenSpace);
                    break;
            }
        }

        private void ModeChanged()
        {
            SetMethods();
            UpdateVisibility();
            GetDependentColors();
        }

        private void SetMethods()
        {
            switch (_mode)
            {
                case ETransformType.Rotate:
                    _highlight = HighlightRotation;
                    _drag = DragRotation;
                    _mouseDown = MouseDownRotation;
                    _mouseUp = MouseUpRotation;
                    break;
                case ETransformType.Translate:
                    _highlight = HighlightTranslation;
                    _drag = DragTranslation;
                    _mouseDown = MouseDownTranslation;
                    _mouseUp = MouseUpTranslation;
                    break;
                case ETransformType.Scale:
                    _highlight = HighlightScale;
                    _drag = DragScale;
                    _mouseDown = MouseDownScale;
                    _mouseUp = MouseUpScale;
                    break;
            }
        }

        private void UpdateVisibility()
        {
            switch (_mode)
            {
                case ETransformType.Rotate:
                    if (_translationModel is not null)
                        _translationModel.IsActive = false;
                    if (_nonRotationModel is not null)
                        _nonRotationModel.IsActive = false;
                    if (_scaleModel is not null)
                        _scaleModel.IsActive = false;
                    if (_rotationModel is not null)
                        _rotationModel.IsActive = true;
                    if (_screenRotationModel is not null)
                        _screenRotationModel.IsActive = true;
                    if (_screenTranslationModel is not null)
                        _screenTranslationModel.IsActive = false;
                    break;
                case ETransformType.Translate:
                    if (_translationModel is not null)
                        _translationModel.IsActive = true;
                    if (_nonRotationModel is not null)
                        _nonRotationModel.IsActive = true;
                    if (_scaleModel is not null)
                        _scaleModel.IsActive = false;
                    if (_rotationModel is not null)
                        _rotationModel.IsActive = false;
                    if (_screenRotationModel is not null)
                        _screenRotationModel.IsActive = false;
                    if (_screenTranslationModel is not null)
                        _screenTranslationModel.IsActive = true;
                    break;
                case ETransformType.Scale:
                    if (_translationModel is not null)
                        _translationModel.IsActive = false;
                    if (_nonRotationModel is not null)
                        _nonRotationModel.IsActive = true;
                    if (_scaleModel is not null)
                        _scaleModel.IsActive = true;
                    if (_rotationModel is not null)
                        _rotationModel.IsActive = false;
                    if (_screenRotationModel is not null)
                        _screenRotationModel.IsActive = false;
                    if (_screenTranslationModel is not null)
                        _screenTranslationModel.IsActive = false;
                    break;
            }

            //int x = 0;

            //ModelComponent modelComp = GetSiblingComponent<ModelComponent>(true)!;
            //SceneNode? screen = SceneNode.Transform.TryGetChildAt(0)?.SceneNode;
            //ModelComponent? screenModelComp = screen?.GetOrAddComponent<ModelComponent>(out _);

            //var meshes = modelComp.Meshes;
            //var screenMeshes = screenModelComp?.Meshes;

            //if (meshes.Count != 21)
            //    return;

            //meshes[x++].RenderInfo.IsVisible = _mode == ETransformType.Rotate;

            //for (int i = 0; i < 3; ++i)
            //{
            //    meshes[x++].RenderInfo.IsVisible = _mode != ETransformType.Rotate;
            //    meshes[x++].RenderInfo.IsVisible = _mode != ETransformType.Rotate;
            //    meshes[x++].RenderInfo.IsVisible = _mode == ETransformType.Translate;
            //    meshes[x++].RenderInfo.IsVisible = _mode == ETransformType.Translate;
            //    meshes[x++].RenderInfo.IsVisible = _mode == ETransformType.Scale;
            //    meshes[x++].RenderInfo.IsVisible = _mode == ETransformType.Rotate;
            //}

            //if (screenMeshes != null)
            //{
            //    screenMeshes[0].RenderInfo.IsVisible = _mode == ETransformType.Rotate;
            //    screenMeshes[1].RenderInfo.IsVisible = _mode == ETransformType.Translate;
            //}
        }

        private void MouseUpScale()
        {

        }

        private void MouseDownScale()
        {

        }

        private void MouseUpTranslation()
        {

        }

        private void MouseDownTranslation()
        {

        }

        private void MouseUpRotation()
        {

        }

        private void MouseDownRotation()
        {

        }

        /// <summary>
        /// The socket transform that is being manipulated by this transform tool.
        /// </summary>
        public TransformBase? TargetSocket
        {
            get => _targetSocket;
            set
            {
                if (_targetSocket != null)
                {
                    _targetSocket.WorldMatrixChanged -= SocketTransformChanged;
                }
                _targetSocket = value;
                if (_targetSocket != null)
                {
                    SetWorldMatrices(GetSocketSpacialTransform(), GetSocketSpacialTransformInverse());
                    _targetSocket.WorldMatrixChanged += SocketTransformChanged;
                }
                else
                    SetWorldMatrices(Matrix4x4.Identity, Matrix4x4.Identity);

                //_dragMatrix = RootComponent.WorldMatrix;
                //_invDragMatrix = RootComponent.InverseWorldMatrix;
            }
        }

        private Matrix4x4 GetSocketSpacialTransform()
        {
            if (_targetSocket is null)
                return Matrix4x4.Identity;

            switch (TransformSpace)
            {
                case ETransformSpace.Local:
                    {
                        return _targetSocket.WorldMatrix; //TODO: clear scale
                    }
                case ETransformSpace.Parent:
                    {
                        if (_targetSocket.Parent != null)
                            return _targetSocket.Parent.WorldMatrix; //TODO: clear scale
                        else
                            return Matrix4x4.CreateTranslation(_targetSocket.WorldMatrix.Translation);
                    }
                case ETransformSpace.Screen:
                    {
                        Vector3 point = _targetSocket.WorldMatrix.Translation;
                        XRCamera? camera = Engine.State.MainPlayer.Viewport?.ActiveCamera;
                        if (camera != null)
                        {
                            Vector3 forward = (camera.Transform.WorldTranslation - point).Normalized();
                            Vector3 up = camera.Transform.WorldUp;
                            return Matrix4x4.CreateWorld(point, forward, up);
                        }
                        return Matrix4x4.Identity;
                    }
                case ETransformSpace.World:
                default:
                    {
                        return Matrix4x4.CreateTranslation(_targetSocket.WorldMatrix.Translation);
                    }
            }
        }
        private Matrix4x4 GetSocketSpacialTransformInverse()
        {
            if (_targetSocket is null)
                return Matrix4x4.Identity;

            switch (TransformSpace)
            {
                case ETransformSpace.Local:
                    {
                        return _targetSocket.InverseWorldMatrix; //TODO: clear scale
                    }
                case ETransformSpace.Parent:
                    {
                        if (_targetSocket.Parent != null)
                            return _targetSocket.Parent.InverseWorldMatrix; //TODO: clear scale
                        else
                            return Matrix4x4.CreateTranslation(_targetSocket.InverseWorldMatrix.Translation);
                    }
                case ETransformSpace.Screen:
                    {
                        Vector3 point = _targetSocket.WorldMatrix.Translation;
                        XRCamera? camera = Engine.State.MainPlayer.Viewport?.ActiveCamera;
                        if (camera != null)
                        {
                            Vector3 forward = (camera.Transform.WorldTranslation - point).Normalized();
                            Vector3 up = camera.Transform.WorldUp;
                            return Matrix4x4.CreateWorld(point, forward, up).Inverted();
                        }
                        return Matrix4x4.Identity;
                    }
                case ETransformSpace.World:
                default:
                    {
                        return Matrix4x4.CreateTranslation(_targetSocket.InverseWorldMatrix.Translation);
                    }
            }
        }

        private void SocketTransformChanged(TransformBase? socket)
        {
            if (_pressed)
                return;
            
            _pressed = true;
            SetWorldMatrices(GetSocketSpacialTransform(), GetSocketSpacialTransformInverse());
            //_dragMatrix = RootComponent.WorldMatrix;
            //_invDragMatrix = RootComponent.InverseWorldMatrix;
            _pressed = false;
        }

        //public override void OnSpawnedPreComponentSetup()
        //{
        //    //OwningWorld.Scene.Add(this);
        //}
        //public override void OnDespawned()
        //{
        //    //OwningWorld.Scene.Remove(this);
        //}

        private BoolVector3 _hiAxis;
        private bool _hiCam, _hiSphere;
        private const int _circlePrecision = 20;
        private const float _orbRadius = 1.0f;
        private const float _circRadius = _orbRadius * _circOrbScale;
        private const float _screenTransExtent = _orbRadius * 0.1f;
        private const float _axisSnapRange = 7.0f;
        private const float _selectRange = 0.03f; //Selection error range for orb and circ
        private const float _axisSelectRange = 0.1f; //Selection error range for axes
        private const float _selectOrbScale = _selectRange / _orbRadius;
        private const float _circOrbScale = 1.2f;
        private const float _axisLength = _orbRadius * 2.0f;
        private const float _axisHalfLength = _orbRadius * 0.75f;
        private const float _coneRadius = _orbRadius * 0.1f;
        private const float _coneDistance = _orbRadius * 1.5f;
        private const float _scaleHalf1LDist = _orbRadius * 0.8f;
        private const float _scaleHalf2LDist = _orbRadius * 1.2f;

        Vector3 _lastPointWorld;
        Vector3 _localDragPlaneNormal;

        private Action? _mouseUp, _mouseDown;
        private DelDrag? _drag;
        private DelHighlight? _highlight;
        private delegate bool DelHighlight(XRCamera camera, Segment localRay);
        private delegate void DelDrag(Vector3 dragPoint);
        private delegate void DelDragRot(Quaternion dragPoint);

        #region Drag
        private bool
            _snapRotations = false,
            _snapTranslations = false,
            _snapScale = false;
        private float _rotationSnapBias = 0.0f;
        private float _rotationSnapInterval = 5.0f;
        private float _translationSnapBias = 0.0f;
        private float _translationSnapInterval = 30.0f;
        private float _scaleSnapBias = 0.0f;
        private float _scaleSnapInterval = 0.25f;
        private void DragRotation(Vector3 dragPointWorld)
        {
            XRMath.AxisAngleBetween(_lastPointWorld, dragPointWorld, out Vector3 axis, out float angle);

            //if (angle == 0.0f)
            //    return;

            //if (_snapRotations)
            //    angle = angle.RoundToNearest(_rotationSnapBias, _rotationSnapInterval);

            Quaternion worldDelta = Quaternion.CreateFromAxisAngle(axis, angle);

            //TODO: convert to socket space
            //_targetSocket.Rotation *= worldDelta;

            SetWorldMatrices(GetSocketSpacialTransform(), GetSocketSpacialTransformInverse());
        }

        private void DragTranslation(Vector3 dragPointWorld)
        {
            Vector3 worldDelta = dragPointWorld - _lastPointWorld;

            //var parent = _targetSocket?.Parent;
            //Matrix4x4 mtx = _targetSocket.LocalMatrix * Matrix4x4.CreateTranslation(worldDelta) * (parent?.WorldMatrix ?? Matrix4x4.Identity);
            _targetSocket?.DeriveWorldMatrix(_targetSocket.WorldMatrix * Matrix4x4.CreateTranslation(worldDelta));

            //Matrix4 m = _targetSocket.InverseWorldMatrix.ClearScale();
            //m = m.ClearTranslation();
            //Vector3 worldTrans = m * delta;

            //if (_snapTranslations)
            //{
            //    //Modify delta to move resulting world point to nearest snap
            //    Vector3 worldPoint = _targetSocket.WorldMatrix.Translation;
            //    Vector3 resultPoint = worldPoint + worldTrans;

            //    resultPoint.X = resultPoint.X.RoundToNearest(_translationSnapBias, _translationSnapInterval);
            //    resultPoint.Y = resultPoint.Y.RoundToNearest(_translationSnapBias, _translationSnapInterval);
            //    resultPoint.Z = resultPoint.Z.RoundToNearest(_translationSnapBias, _translationSnapInterval);

            //    worldTrans = resultPoint - worldPoint;
            //}

            //TODO: convert world delta to local socket delta
            //if (_targetSocket != null)
            //    _targetSocket.Translation.Value += worldDelta;

            SetWorldMatrices(GetSocketSpacialTransform(), GetSocketSpacialTransformInverse());
        }
        private void DragScale(Vector3 dragPointWorld)
        {
            Vector3 worldDelta = dragPointWorld - _lastPointWorld;

            //TODO: better method for scaling

            //_targetSocket.Scale += worldDelta;

            SetWorldMatrices(GetSocketSpacialTransform(), GetSocketSpacialTransformInverse());
        }
        /// <summary>
        /// Returns a point relative to the local space of the target socket (origin at 0,0,0), clamped to the highlighted drag plane.
        /// </summary>
        /// <param name="camera">The camera viewing this tool, used for camera space drag clamping.</param>
        /// <param name="localRay">The mouse ray, transformed into the socket's local space.</param>
        /// <returns></returns>
        private Vector3 GetLocalDragPoint(XRCamera camera, Segment localRay)
        {
            //Convert all coordinates to local space

            Vector3 localCamPoint = Vector3.Transform(camera.Transform.WorldTranslation, Transform.InverseWorldMatrix);
            Vector3 localDragPoint, unit;

            var start = localRay.Start;
            var dir = Vector3.Normalize(localRay.End - localRay.Start);

            switch (_mode)
            {
                case ETransformType.Scale:
                case ETransformType.Translate:
                    {
                        if (_hiCam)
                        {
                            _localDragPlaneNormal = localCamPoint;
                            _localDragPlaneNormal.Normalized();
                        }
                        else if (_hiAxis.X)
                        {
                            if (_hiAxis.Y)
                            {
                                _localDragPlaneNormal = Vector3.UnitZ;
                            }
                            else if (_hiAxis.Z)
                            {
                                _localDragPlaneNormal = Vector3.UnitY;
                            }
                            else
                            {
                                unit = Vector3.UnitX;
                                Vector3 perpPoint = Ray.GetClosestColinearPoint(Vector3.Zero, unit, localCamPoint);
                                _localDragPlaneNormal = localCamPoint - perpPoint;
                                _localDragPlaneNormal.Normalized();

                                if (!GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _localDragPlaneNormal, out localDragPoint))
                                    return _lastPointWorld;

                                return Ray.GetClosestColinearPoint(Vector3.Zero, unit, localDragPoint);
                            }
                        }
                        else if (_hiAxis.Y)
                        {
                            if (_hiAxis.X)
                            {
                                _localDragPlaneNormal = Vector3.UnitZ;
                            }
                            else if (_hiAxis.Z)
                            {
                                _localDragPlaneNormal = Vector3.UnitX;
                            }
                            else
                            {
                                unit = Vector3.UnitY;
                                Vector3 perpPoint = Ray.GetClosestColinearPoint(Vector3.Zero, unit, localCamPoint);
                                _localDragPlaneNormal = localCamPoint - perpPoint;
                                _localDragPlaneNormal.Normalized();

                                if (!GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _localDragPlaneNormal, out localDragPoint))
                                    return _lastPointWorld;

                                return Ray.GetClosestColinearPoint(Vector3.Zero, unit, localDragPoint);
                            }
                        }
                        else if (_hiAxis.Z)
                        {
                            if (_hiAxis.X)
                            {
                                _localDragPlaneNormal = Vector3.UnitY;
                            }
                            else if (_hiAxis.Y)
                            {
                                _localDragPlaneNormal = Vector3.UnitX;
                            }
                            else
                            {
                                unit = Vector3.UnitZ;
                                Vector3 perpPoint = Ray.GetClosestColinearPoint(Vector3.Zero, unit, localCamPoint);
                                _localDragPlaneNormal = localCamPoint - perpPoint;
                                _localDragPlaneNormal.Normalized();

                                if (!GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _localDragPlaneNormal, out localDragPoint))
                                    return _lastPointWorld;

                                return Ray.GetClosestColinearPoint(Vector3.Zero, unit, localDragPoint);
                            }
                        }

                        if (GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _localDragPlaneNormal, out localDragPoint))
                            return localDragPoint;
                    }
                    break;
                case ETransformType.Rotate:
                    {
                        if (_hiCam)
                        {
                            _localDragPlaneNormal = localCamPoint;
                            _localDragPlaneNormal.Normalized();

                            if (GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _localDragPlaneNormal, out localDragPoint))
                                return localDragPoint;
                        }
                        else if (_hiAxis.Any)
                        {
                            if (_hiAxis.X)
                                unit = Vector3.UnitX;
                            else if (_hiAxis.Y)
                                unit = Vector3.UnitY;
                            else// if (_hiAxis.Z)
                                unit = Vector3.UnitZ;

                            _localDragPlaneNormal = unit;
                            _localDragPlaneNormal.Normalized();

                            if (GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _localDragPlaneNormal, out localDragPoint))
                                return localDragPoint;
                        }
                        else if (_hiSphere)
                        {
                            Vector3 worldPoint = Transform.WorldTranslation;
                            float radius = camera.DistanceScaleOrthographic(worldPoint, _orbRadius);

                            if (GeoUtil.RayIntersectsSphere(start, dir, Vector3.Zero, radius * _circOrbScale, out localDragPoint))
                            {
                                _localDragPlaneNormal = localDragPoint.Normalized();
                                return localDragPoint;
                            }
                        }
                    }
                    break;
            }

            return _lastPointWorld;
        }
        #endregion

        #region Highlighting
        private bool HighlightRotation(XRCamera camera, Segment localRay)
        {
            var start = localRay.Start;
            var dir = Vector3.Normalize(localRay.End - localRay.Start);

            if (!GeoUtil.RayIntersectsSphere(start, dir, Vector3.Zero, _circOrbScale, out Vector3 point))
            {
                //If no intersect is found, project the ray through the plane perpendicular to the camera.
                //localRay.LinePlaneIntersect(Vector3.Zero, (camera.WorldPoint - worldPoint).Normalized(), out point);
                GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, Vector3.Transform((camera.Transform.WorldTranslation), Transform.InverseWorldMatrix), out point);

                //Clamp the point to edge of the sphere
                point = Ray.PointAtLineDistance(Vector3.Zero, point, 1.0f);

                //Point lies on circ line?
                float distance = point.Length();
                if (Math.Abs(distance - _circOrbScale) < _selectOrbScale)
                    _hiCam = true;
            }
            else
            {
                point = point.Normalized();

                _hiSphere = true;

                float x = point.Dot(Vector3.UnitX);
                float y = point.Dot(Vector3.UnitY);
                float z = point.Dot(Vector3.UnitZ);

                if (Math.Abs(x) < 0.3f)
                    _hiAxis.X = true;
                else if (Math.Abs(y) < 0.3f)
                    _hiAxis.Y = true;
                else if (Math.Abs(z) < 0.3f)
                    _hiAxis.Z = true;
            }

            return _hiAxis.Any || _hiCam || _hiSphere;
        }
        private bool HighlightTranslation(XRCamera camera, Segment localRay)
        {
            var start = localRay.Start;
            var dir = Vector3.Normalize(localRay.End - localRay.Start);

            Vector3?[] intersectionPoints = new Vector3?[3];

            bool snapFound = false;
            for (int normalAxis = 0; normalAxis < 3; ++normalAxis)
            {
                Vector3 unit = Vector3.Zero;
                unit[normalAxis] = start[normalAxis] < 0.0f ? -1.0f : 1.0f;

                //Get plane intersection point for cursor ray and each drag plane
                if (GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, unit, out Vector3 point))
                    intersectionPoints[normalAxis] = point;
            }

            foreach (Vector3? d in intersectionPoints)
            {
                if (d is null)
                    continue;
                var diff = d.Value;
                
                //int planeAxis1 = normalAxis + 1 - (normalAxis >> 1) * 3;    //0 = 1, 1 = 2, 2 = 0
                //int planeAxis2 = planeAxis1 + 1 - (normalAxis  & 1) * 3;    //0 = 2, 1 = 0, 2 = 1

                if (diff.X > -_axisSelectRange && diff.X <= _axisLength &&
                    diff.Y > -_axisSelectRange && diff.Y <= _axisLength &&
                    diff.Z > -_axisSelectRange && diff.Z <= _axisLength)
                {
                    float errorRange = _axisSelectRange;

                    _hiAxis.X = diff.X > _axisHalfLength && Math.Abs(diff.Y) < errorRange && Math.Abs(diff.Z) < errorRange;
                    _hiAxis.Y = diff.Y > _axisHalfLength && Math.Abs(diff.X) < errorRange && Math.Abs(diff.Z) < errorRange;
                    _hiAxis.Z = diff.Z > _axisHalfLength && Math.Abs(diff.X) < errorRange && Math.Abs(diff.Y) < errorRange;

                    if (snapFound = _hiAxis.Any)
                        break;

                    if (diff.X < _axisHalfLength &&
                        diff.Y < _axisHalfLength &&
                        diff.Z < _axisHalfLength)
                    {
                        //Point lies inside the double drag areas
                        _hiAxis.X = diff.X > _axisSelectRange;
                        _hiAxis.Y = diff.Y > _axisSelectRange;
                        _hiAxis.Z = diff.Z > _axisSelectRange;
                        _hiCam = _hiAxis.None;

                        snapFound = true;
                        break;
                    }
                }
            }

            return snapFound;
        }
        private bool HighlightScale(XRCamera camera, Segment localRay)
        {
            var start = localRay.Start;
            var dir = Vector3.Normalize(localRay.End - localRay.Start);

            Vector3?[] intersectionPoints = new Vector3?[3];

            bool snapFound = false;
            for (int normalAxis = 0; normalAxis < 3; ++normalAxis)
            {
                Vector3 unit = Vector3.Zero;
                unit[normalAxis] = start[normalAxis] < 0.0f ? -1.0f : 1.0f;

                //Get plane intersection point for cursor ray and each drag plane
                if (GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, unit, out Vector3 point))
                    intersectionPoints[normalAxis] = point;
            }

            //_intersectionPoints.Sort((l, r) => l.DistanceToSquared(camera.WorldPoint).CompareTo(r.DistanceToSquared(camera.WorldPoint)));

            foreach (Vector3? d in intersectionPoints)
            {
                if (d is null)
                    continue;
                Vector3 diff = d.Value;

                //int planeAxis1 = normalAxis + 1 - (normalAxis >> 1) * 3;    //0 = 1, 1 = 2, 2 = 0
                //int planeAxis2 = planeAxis1 + 1 - (normalAxis  & 1) * 3;    //0 = 2, 1 = 0, 2 = 1

                if (diff.X > -_axisSelectRange && diff.X <= _axisLength &&
                    diff.Y > -_axisSelectRange && diff.Y <= _axisLength &&
                    diff.Z > -_axisSelectRange && diff.Z <= _axisLength)
                {
                    float errorRange = _axisSelectRange;

                    _hiAxis.X = diff.X > _axisHalfLength && Math.Abs(diff.Y) < errorRange && Math.Abs(diff.Z) < errorRange;
                    _hiAxis.Y = diff.Y > _axisHalfLength && Math.Abs(diff.X) < errorRange && Math.Abs(diff.Z) < errorRange;
                    _hiAxis.Z = diff.Z > _axisHalfLength && Math.Abs(diff.X) < errorRange && Math.Abs(diff.Y) < errorRange;

                    if (snapFound = _hiAxis.Any)
                        break;

                    //Determine if the point is in the double or triple drag triangles
                    float halfDist = _scaleHalf2LDist;
                    float centerDist = _scaleHalf1LDist;

                    if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(halfDist, 0, 0), new Vector3(0, halfDist, 0)))
                    {
                        if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(centerDist, 0, 0), new Vector3(0, centerDist, 0)))
                            _hiAxis.X = _hiAxis.Y = _hiAxis.Z = true;
                        else
                            _hiAxis.X = _hiAxis.Y = true;
                    }
                    else if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(halfDist, 0, 0), new Vector3(0, 0, halfDist)))
                    {
                        if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(centerDist, 0, 0), new Vector3(0, 0, centerDist)))
                            _hiAxis.X = _hiAxis.Y = _hiAxis.Z = true;
                        else
                            _hiAxis.X = _hiAxis.Y = true;
                    }
                    else if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(0, halfDist, 0), new Vector3(0, 0, halfDist)))
                    {
                        if (XRMath.IsInTriangle(diff, new Vector3(), new Vector3(0, centerDist, 0), new Vector3(0, 0, centerDist)))
                            _hiAxis.X = _hiAxis.Y = _hiAxis.Z = true;
                        else
                            _hiAxis.Y = _hiAxis.Z = true;
                    }

                    snapFound = _hiAxis.Any;

                    if (snapFound)
                        break;
                }
            }

            return snapFound;
        }
        #endregion

        private bool _pressed = false;
        //private Matrix4 _dragMatrix, _invDragMatrix;

        /// <summary>
        /// Returns true if intersecting one of the transform tool's various parts.
        /// </summary>
        public bool MouseMove(Segment cursor, XRCamera camera, bool pressed)
        {
            bool snapFound = true;
            if (pressed)
            {
                if (_hiAxis.None && !_hiCam && !_hiSphere)
                    return false;

                Matrix4x4 invRoot = Transform.FirstChild()!.InverseWorldMatrix;

                if (!_pressed)
                    OnPressed();

                Segment localRay = cursor.TransformedBy(invRoot);
                Vector3 localDragPoint = GetLocalDragPoint(camera, localRay);
                Vector3 worldDragPoint = Vector3.Transform(localDragPoint, Transform.FirstChild()!.WorldMatrix);
                _drag?.Invoke(worldDragPoint);

                _lastPointWorld = worldDragPoint;
            }
            else
            {
                Matrix4x4 invRoot = Transform.FirstChild()!.InverseWorldMatrix;

                if (_pressed)
                    OnReleased();

                Segment localRay = cursor.TransformedBy(invRoot);

                _hiAxis.X = _hiAxis.Y = _hiAxis.Z = false;
                _hiCam = _hiSphere = false;

                snapFound = _highlight?.Invoke(camera, localRay) ?? false;

                _axisMat[0].Parameter<ShaderVector4>(0)!.Value = _hiAxis.X ? ColorF4.Yellow : ColorF4.Red;
                _axisMat[1].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Y ? ColorF4.Yellow : ColorF4.Green;
                _axisMat[2].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Z ? ColorF4.Yellow : ColorF4.Blue;
                _screenMat!.Parameter<ShaderVector4>(0)!.Value = _hiCam ? ColorF4.Yellow : ColorF4.LightGray;

                GetDependentColors();

                Vector3 localDragPoint = GetLocalDragPoint(camera, localRay);
                _lastPointWorld = Vector3.Transform(localDragPoint, Transform.FirstChild()!.WorldMatrix);
            }
            return snapFound;
        }
        private void GetDependentColors()
        {
            if (_transPlaneMat.Any(m => m == null) || _scalePlaneMat.Any(m => m == null) || TransformMode == ETransformType.Rotate)
                return;

            if (TransformMode == ETransformType.Translate)
            {
                _transPlaneMat[0].Parameter<ShaderVector4>(0)!.Value = _hiAxis.X && _hiAxis.Y ? (ColorF4)ColorF4.Yellow : ColorF4.Red;
                _transPlaneMat[1].Parameter<ShaderVector4>(0)!.Value = _hiAxis.X && _hiAxis.Z ? (ColorF4)ColorF4.Yellow : ColorF4.Red;
                _transPlaneMat[2].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Y && _hiAxis.Z ? (ColorF4)ColorF4.Yellow : ColorF4.Green;
                _transPlaneMat[3].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Y && _hiAxis.X ? (ColorF4)ColorF4.Yellow : ColorF4.Green;
                _transPlaneMat[4].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Z && _hiAxis.X ? (ColorF4)ColorF4.Yellow : ColorF4.Blue;
                _transPlaneMat[5].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Z && _hiAxis.Y ? (ColorF4)ColorF4.Yellow : ColorF4.Blue;
            }
            else
            {
                _scalePlaneMat[0].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Y && _hiAxis.Z ? (ColorF4)ColorF4.Yellow : ColorF4.Red;
                _scalePlaneMat[1].Parameter<ShaderVector4>(0)!.Value = _hiAxis.X && _hiAxis.Z ? (ColorF4)ColorF4.Yellow : ColorF4.Green;
                _scalePlaneMat[2].Parameter<ShaderVector4>(0)!.Value = _hiAxis.X && _hiAxis.Y ? (ColorF4)ColorF4.Yellow : ColorF4.Blue;
            }
        }

        private void OnPressed()
        {
            if (_targetSocket != null)
            {
                SetWorldMatrices(GetSocketSpacialTransform(), GetSocketSpacialTransformInverse());
                PrevRootWorldMatrix = _targetSocket.WorldMatrix;
            }
            else
            {
                SetWorldMatrices(Matrix4x4.Identity, Matrix4x4.Identity);
                PrevRootWorldMatrix = Matrix4x4.Identity;
            }

            _pressed = true;
            MouseDown?.Invoke();
        }

        private void SetWorldMatrices(Matrix4x4 transform, Matrix4x4 invTransform)
            => RootTransform.SetWorldMatrix(transform);

        private void OnReleased()
        {
            _pressed = false;
            MouseUp?.Invoke();
        }

        private void Render()
        {
            if ((!_hiCam && !_hiSphere && !_hiAxis.Any) || Engine.Rendering.State.IsShadowPass)
                return;
            
            Engine.Rendering.Debug.RenderPoint(_lastPointWorld, ColorF4.Black);

            var camera = Engine.Rendering.State.RenderingCamera;
            if (camera != null)
                Engine.Rendering.Debug.RenderLine(
                    _lastPointWorld,
                    _lastPointWorld + Vector3.TransformNormal(_localDragPlaneNormal, Transform.WorldMatrix) * camera.DistanceScaleOrthographic(Transform.WorldTranslation, 2.0f),
                    ColorF4.Black);
        }
    }
}

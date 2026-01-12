using Extensions;
using MagicPhysX;
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
using XREngine.Rendering.Physics.Physx;
using XREngine.Rendering.Physics.Physx.Joints;
using XREngine.Scene;
using XREngine.Scene.Transforms;
using XREngine.Components.Scene.Transforms;

namespace XREngine.Scene.Components.Editing
{
    [RequiresTransform(typeof(DrivenWorldTransform))]
    public class TransformTool3D : XRComponent, IRenderable
    {
        public TransformTool3D() : base()
        {
            TransformSpace = ETransformSpace.World;
            _rc = new RenderCommandMethod3D((int)EDefaultRenderPass.OnTopForward, Render);
            RenderInfo = RenderInfo3D.New(this, _rc);
            RenderInfo.Layer = DefaultLayers.GizmosIndex;
            RenderedObjects = [RenderInfo];
            UpdateModelComponent();
        }

        public DrivenWorldTransform RootTransform => SceneNode.GetTransformAs<DrivenWorldTransform>(true)!;

        private float _toolScale = 0.1f;
        public float ToolScale
        {
            get => _toolScale;
            set => SetField(ref _toolScale, value);
        }

        #region Single Instance

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

        public static bool GetActiveInstance(out TransformTool3D? comp)
        {
            comp = null;
            var instance = InstanceNode;
            return instance is not null && instance.TryGetComponent(out comp) && comp is not null;
        }

        private static ETransformSpace _transformSpace = ETransformSpace.World;
        public static ETransformSpace TransformSpace
        {
            get => _transformSpace;
            set
            {
                _transformSpace = value;

                if (!GetActiveInstance(out var instance) || instance is null)
                    return;
                
                if (_transformSpace == ETransformSpace.Screen)
                    instance.RegisterTick(ETickGroup.Late, ETickOrder.Logic, instance.UpdateScreenSpace);
                else
                    instance.UnregisterTick(ETickGroup.Late, ETickOrder.Logic, instance.UpdateScreenSpace);
            }
        }

        private static ETransformMode _mode = ETransformMode.Translate;
        public static ETransformMode TransformMode
        {
            get => _mode;
            set
            {
                _mode = value;
                if (GetActiveInstance(out var instance))
                    instance?.ModeChanged();
            }
        }

        /// <summary>
        /// Spawns and retrieves the transformation tool in the current scene.
        /// </summary>
        /// <param name="world">The world to spawn the transform tool in.</param>
        /// <param name="comp"></param>
        /// <param name="transformType"></param>
        /// <returns></returns>
        public static TransformTool3D? GetInstance(TransformBase comp)
        {
            XRWorldInstance? world = comp?.World;
            if (world is null)
                return null;

            if (_instanceNode?.World != world)
            {
                _instanceNode?.Destroy();
                _instanceNode = new SceneNode("TransformTool3D");
                // Add the transform tool to the hidden editor scene so it's not saved or shown in hierarchy
                world.AddToEditorScene(_instanceNode);
            }

            TransformTool3D instance = _instanceNode.GetOrAddComponent<TransformTool3D>(out _)!;
            instance.TargetSocket = comp;

            if (_transformSpace == ETransformSpace.Screen)
                instance.RegisterTick(ETickGroup.Late, ETickOrder.Logic, instance.UpdateScreenSpace);

            return instance;
        }

        #endregion

        public event Action? MouseDown, MouseUp;

        #region Rendering

        public RenderInfo3D RenderInfo { get; }
        public RenderInfo[] RenderedObjects { get; }
        private readonly RenderCommandMethod3D _rc;

        private readonly XRMaterial[] _axisMat = new XRMaterial[3];
        private readonly XRMaterial[] _transPlaneMat = new XRMaterial[6];
        private readonly XRMaterial[] _scalePlaneMat = new XRMaterial[3];
        private XRMaterial? _screenMat;

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
            rootBillboardTfm.DistanceScale = ToolScale;
            rootBillboardTfm.ScaleByVerticalFov = true;

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

        private static void GetMeshes(
            Vector3 unit,
            VertexLine axisLine,
            VertexLine transLine1,
            VertexLine transLine2,
            VertexLine scaleLine1,
            VertexLine scaleLine2,
            out XRMesh axisPrim,
            out XRMesh arrowPrim,
            out XRMesh transPrim1,
            out XRMesh transPrim2,
            out XRMesh scalePrim,
            out XRMesh rotPrim)
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
            transLine1.Vertex0.ColorSets = [new Vector4(unit1, 1.0f)];
            transLine1.Vertex1.ColorSets = [new Vector4(unit1, 1.0f)];

            transLine2 = new(halfUnit, halfUnit + unit2 * _axisHalfLength);
            transLine2.Vertex0.ColorSets = [new Vector4(unit2, 1.0f)];
            transLine2.Vertex1.ColorSets = [new Vector4(unit2, 1.0f)];

            scaleLine1 = new(unit1 * _scaleHalf1LDist, unit2 * _scaleHalf1LDist);
            scaleLine1.Vertex0.ColorSets = [new Vector4(unit, 1.0f)];
            scaleLine1.Vertex1.ColorSets = [new Vector4(unit, 1.0f)];

            scaleLine2 = new(unit1 * _scaleHalf2LDist, unit2 * _scaleHalf2LDist);
            scaleLine2.Vertex0.ColorSets = [new Vector4(unit, 1.0f)];
            scaleLine2.Vertex1.ColorSets = [new Vector4(unit, 1.0f)];
        }

        private void GetMaterials(
            int normalAxis,
            Vector3 unit,
            Vector3 unit1,
            Vector3 unit2,
            out XRMaterial axisMat,
            out XRMaterial planeMat1,
            out XRMaterial planeMat2,
            out XRMaterial scalePlaneMat)
        {
            axisMat = XRMaterial.CreateUnlitColorMaterialForward(unit);
            axisMat.RenderOptions.DepthTest.Enabled = ERenderParamUsage.Disabled;
            axisMat.RenderOptions.CullMode = ECullMode.None;
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
            sphereMat.RenderOptions.DepthTest.Function = Rendering.Models.Materials.EComparison.Lequal;
            //sphereMat.RenderOptions.LineWidth = 1.0f;
            sphereMat.RenderOptions.WriteRed = false;
            sphereMat.RenderOptions.WriteGreen = false;
            sphereMat.RenderOptions.WriteBlue = false;
            sphereMat.RenderOptions.WriteAlpha = false;

            XRMesh spherePrim = XRMesh.Shapes.SolidSphere(Vector3.Zero, _orbRadius, 10, 10);
            //isRotate = true
            rotationMeshes.Add(new SubMesh(spherePrim, sphereMat));
        }

        #endregion

        private TransformBase? _targetSocket = null;
        private bool _updatingDisplayTransform;

        private void UpdateScreenSpace()
        {
            if (_targetSocket != null)
                UpdateDisplayTransform();
        }

        private PhysxDynamicRigidBody? _linkRB = null;

        protected override bool OnPropertyChanging<T>(string? propName, T field, T @new)
        {
            bool change = base.OnPropertyChanging(propName, field, @new);
            if (change)
            {
                switch (propName)
                {
                    case nameof(TargetSocket):
                        if (_targetSocket != null)
                        {
                            _targetSocket.WorldMatrixChanged -= SocketTransformChangedCallback;
                            _linkRB?.Destroy();
                            _linkRB = null;
                        }
                        break;
                }
            }
            return change;
        }
        protected override void OnPropertyChanged<T>(string? propName, T prev, T field)
        {
            base.OnPropertyChanged(propName, prev, field);
            switch (propName)
            {
                case nameof(TargetSocket):
                    {
                        if (_targetSocket != null)
                        {
                            _targetSocket.WorldMatrixChanged += SocketTransformChangedCallback;
                            LinkRigidBodySocket();
                        }
                        UpdateDisplayTransform();
                    }
                    break;
            }
        }

        private void LinkRigidBodySocket()
        {
            if (_targetSocket is not RigidBodyTransform rbt)
                return;

            if (rbt.World?.PhysicsScene is not PhysxScene phys)
                return;

            var rb = rbt.RigidBody as PhysxDynamicRigidBody;
            if (rb is not null && _linkRB is null)
                phys.AddActor(_linkRB = new() { Flags = PxRigidBodyFlags.Kinematic, ActorFlags = PxActorFlags.DisableGravity });
        }

        private void LinkPhysxJoint()
        {
            if (_targetSocket is not RigidBodyTransform rbt)
                return;

            if (rbt.World?.PhysicsScene is not PhysxScene phys)
                return;

            var rb = rbt.RigidBody as PhysxDynamicRigidBody;
            if (rb is not null)
                LinkJoint(phys, rb);
        }

        private PhysxJoint? _dragJoint = null;
        private void LinkJoint(PhysxScene phys, PhysxDynamicRigidBody rb)
            => _dragJoint = MakeDistanceJoint(phys, rb);

        private PhysxJoint_Distance MakeDistanceJoint(PhysxScene phys, PhysxDynamicRigidBody rb)
        {
            if (_linkRB is null)
                phys.AddActor(_linkRB = new() { Flags = PxRigidBodyFlags.Kinematic, ActorFlags = PxActorFlags.DisableGravity });

            (Vector3 Zero, Quaternion Identity) identityTfm = (Vector3.Zero, Quaternion.Identity);

            if (_targetSocket is not null)
                _linkRB.Transform = (_targetSocket.WorldTranslation, _targetSocket.WorldRotation);
            
            PhysxJoint_Distance? joint = phys.NewDistanceJoint(rb, identityTfm, _linkRB, identityTfm);
            joint.MaxDistance = 0.0f;
            joint.MinDistance = 0.0f;
            joint.Stiffness = 10.0f;
            joint.Damping = 0.1f;
            joint.Tolerance = 0.1f;
            joint.ContactDistance = 0.1f;
            joint.DistanceFlags = PxDistanceJointFlags.MaxDistanceEnabled | PxDistanceJointFlags.SpringEnabled;
            joint.Flags = PxConstraintFlags.CollisionEnabled;
            return joint;
        }

        private void UnlinkPhysxJoint()
        {
            if (_dragJoint is null)
                return;
            
            _dragJoint.Release();
            _dragJoint = null;
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
                case ETransformMode.Rotate:
                    _highlight = HighlightRotation;
                    _drag = DragRotation;
                    _mouseDown = MouseDownRotation;
                    _mouseUp = MouseUpRotation;
                    break;
                case ETransformMode.Translate:
                    _highlight = HighlightTranslation;
                    _drag = DragTranslation;
                    _mouseDown = MouseDownTranslation;
                    _mouseUp = MouseUpTranslation;
                    break;
                case ETransformMode.Scale:
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
                case ETransformMode.Rotate:
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
                case ETransformMode.Translate:
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
                case ETransformMode.Scale:
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
        }

        private void MouseUpScale()
        {

        }

        private void MouseDownScale()
        {
            StoreInitialLocalTransform();
        }

        private void MouseUpTranslation()
        {
            UnlinkPhysxJoint();
        }

        private void MouseDownTranslation()
        {
            StoreInitialLocalTransform();
            LinkPhysxJoint();
        }

        private void MouseUpRotation()
        {
            UnlinkPhysxJoint();
        }

        private void MouseDownRotation()
        {
            StoreInitialLocalTransform();
            LinkPhysxJoint();
        }

        private Vector3 _localTranslationDragStart = Vector3.Zero;
        private Quaternion _localRotationDragStart = Quaternion.Identity;
        private Vector3 _localScaleDragStart = Vector3.One;

        private void StoreInitialLocalTransform()
        {
            if (TargetSocket is null)
                return;

            if (TargetSocket is Transform t)
            {
                _localTranslationDragStart = t.Translation;
                _localRotationDragStart = t.Rotation;
                _localScaleDragStart = t.Scale;
            }
            else
                Matrix4x4.Decompose(TargetSocket.LocalMatrix, out _localScaleDragStart, out _localRotationDragStart, out _localTranslationDragStart);
        }

        /// <summary>
        /// The socket transform that is being manipulated by this transform tool.
        /// </summary>
        public TransformBase? TargetSocket
        {
            get => _targetSocket;
            set
            {
                // Prevent feedback loops if the user selects the tool node (or any of its children)
                // as the target socket.
                if (value is not null && IsTransformInToolHierarchy(value))
                    value = null;

                SetField(ref _targetSocket, value);
            }
        }

        private bool IsTransformInToolHierarchy(TransformBase? transform)
        {
            if (transform?.SceneNode is null)
                return false;

            SceneNode toolRoot = SceneNode;
            SceneNode? node = transform.SceneNode;
            while (node is not null)
            {
                if (ReferenceEquals(node, toolRoot))
                    return true;

                node = node.Transform?.Parent?.SceneNode;
            }

            return false;
        }

        /// <summary>
        /// Returns the transform of the target socket in the specified transform space.
        /// </summary>
        /// <returns></returns>
        private Matrix4x4 GetSocketSpacialTransform()
        {
            if (_targetSocket is null)
                return Matrix4x4.Identity;

            return TransformSpace switch
            {
                ETransformSpace.Local => WithoutScale(_targetSocket.WorldMatrix),
                ETransformSpace.Parent => _targetSocket.Parent is null ? Matrix4x4.Identity : WithoutScale(_targetSocket.ParentWorldMatrix),
                ETransformSpace.Screen => GetScreenSpaceTransform(),
                _ => Matrix4x4.CreateTranslation(_targetSocket.WorldMatrix.Translation),
            };
        }

        private Matrix4x4 GetScreenSpaceTransform(XRCamera? referenceCamera = null)
        {
            referenceCamera ??= Engine.State.MainPlayer.Viewport?.ActiveCamera;
            if (referenceCamera is null)
                return Matrix4x4.Identity;
            
            Vector3 socketPos = _targetSocket?.WorldMatrix.Translation ?? Vector3.Zero;
            Vector3 socketToCam = referenceCamera.Transform.WorldTranslation - socketPos;
            return Matrix4x4.CreateWorld(socketPos, socketToCam.Normalized(), referenceCamera.Transform.WorldUp);
        }

        private static Matrix4x4 WithoutScale(Matrix4x4 mtx)
        {
            Matrix4x4.Decompose(mtx, out _, out Quaternion rotation, out Vector3 translation);
            return Matrix4x4.CreateFromQuaternion(rotation) with { Translation = translation };
        }

        private void SocketTransformChangedCallback(TransformBase? socket, Matrix4x4 worldMatrix)
        {
            if (_updatingDisplayTransform)
                return;

            if (socket is null || IsTransformInToolHierarchy(socket))
                return;

            if (TransformSpace != ETransformSpace.Screen)
                UpdateDisplayTransform();
        }

        /// <summary>
        /// Updates the transform of the tool to match the target socket.
        /// If <paramref name="updateDragMatrix"/> is true, the drag matrix is also updated.
        /// The drag matrix should only be set at the start of a drag, on mouse down.
        /// </summary>
        /// <param name="updateDragMatrix"></param>
        private void UpdateDisplayTransform()
        {
            if (_updatingDisplayTransform)
                return;

            if (_targetSocket is null || IsTransformInToolHierarchy(_targetSocket))
                return;

            _updatingDisplayTransform = true;
            try
            {
                SetRootTransform(GetSocketSpacialTransform());
            }
            finally
            {
                _updatingDisplayTransform = false;
            }
        }

        private BoolVector3 _hiAxis;
        private bool _hiCam, _hiSphere;
        private const int _circlePrecision = 20;
        private const float _orbRadius = 1.0f;
        private const float _circRadius = _orbRadius * _circOrbScale;
        private const float _screenTransExtent = _orbRadius * 0.1f;
        private const float _axisSnapRange = 7.0f;
        private const float _selectRange = 0.05f; //Selection error range for orb and circ
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
        Vector3 _worldDragPlaneNormal;

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
            _snapScale = false,
            _snapInLocalSpace = false;

        private float
            _rotationSnapBias = 0.0f,
            _rotationSnapInterval = 5.0f,
            _translationSnapBias = 0.0f,
            _translationSnapInterval = 30.0f,
            _scaleSnapBias = 0.0f,
            _scaleSnapInterval = 0.25f;

        public bool SnapRotations
        {
            get => _snapRotations;
            set => SetField(ref _snapRotations, value);
        }
        public bool SnapTranslations
        {
            get => _snapTranslations;
            set => SetField(ref _snapTranslations, value);
        }
        public bool SnapScale
        {
            get => _snapScale;
            set => SetField(ref _snapScale, value);
        }

        public float RotationSnapBias
        {
            get => _rotationSnapBias;
            set => SetField(ref _rotationSnapBias, value);
        }
        public float RotationSnapInterval
        {
            get => _rotationSnapInterval;
            set => SetField(ref _rotationSnapInterval, value);
        }

        public float TranslationSnapBias
        {
            get => _translationSnapBias;
            set => SetField(ref _translationSnapBias, value);
        }
        public float TranslationSnapInterval
        {
            get => _translationSnapInterval;
            set => SetField(ref _translationSnapInterval, value);
        }

        public float ScaleSnapBias
        {
            get => _scaleSnapBias;
            set => SetField(ref _scaleSnapBias, value);
        }
        public float ScaleSnapInterval
        {
            get => _scaleSnapInterval;
            set => SetField(ref _scaleSnapInterval, value);
        }

        private void DragRotation(Vector3 dragPointWorld)
        {
            if (_targetSocket is null || _lastPointWorld == dragPointWorld)
                return;

            Vector3 socketWorldPos = _targetSocket.WorldTranslation;
            Vector3 dragVecWorld = (dragPointWorld - socketWorldPos).Normalized();
            Vector3 startVecWorld = _worldDragOffsetToSocket.Normalized();

            // Transform to local space for consistent rotation
            Vector3 dragVecLocal = Vector3.TransformNormal(dragVecWorld, _inverseSocketDragMatrix).Normalized();
            Vector3 startVecLocal = Vector3.TransformNormal(startVecWorld, _inverseSocketDragMatrix).Normalized();

            Vector3 dragNormalLocal = _hiSphere
                ? Vector3.Cross(dragVecLocal, startVecLocal)
                : Vector3.TransformNormal(_worldDragPlaneNormal, _inverseSocketDragMatrix).Normalized();

            Quaternion localDelta = Quaternion.CreateFromAxisAngle(dragNormalLocal, XRMath.GetFullAngleRadiansBetween(startVecLocal, dragVecLocal, dragNormalLocal));
            Quaternion newRotationLocal = Quaternion.Normalize(_localRotationDragStart * localDelta);
            
            //// Apply snapping if enabled
            //if (_snapRotations)
            //    angleRad = float.DegreesToRadians(float.RadiansToDegrees(angleRad).RoundedToNearest(_rotationSnapBias, _rotationSnapInterval));

            if (_targetSocket is Transform t)
                t.Rotation = newRotationLocal;
            else if (_targetSocket is RigidBodyTransform rbt)
            {
                Matrix4x4 localMatrix =
                     Matrix4x4.CreateFromQuaternion(newRotationLocal) *
                    Matrix4x4.CreateTranslation(_localTranslationDragStart);

                var parentMtx = _targetSocket.ParentWorldMatrix;
                Matrix4x4 worldMtx = localMatrix * parentMtx;
                if (Matrix4x4.Decompose(worldMtx, out _, out Quaternion rotation, out Vector3 translation))
                {
                    // In edit mode (physics not running), set transform directly
                    // This triggers UpdateComponentInitialPose to sync initial pose
                    if (Engine.PlayMode.IsEditing)
                    {
                        rbt.SetPositionAndRotation(translation, rotation);
                    }
                    else if (_linkRB is not null)
                    {
                        _linkRB.KinematicTarget = (translation, rotation);
                    }
                }
            }
            else
                _targetSocket.DeriveLocalMatrix(
                    Matrix4x4.CreateScale(_localScaleDragStart) *
                    Matrix4x4.CreateFromQuaternion(newRotationLocal) *
                    Matrix4x4.CreateTranslation(_localTranslationDragStart));
        }

        private void DragTranslation(Vector3 dragPointWorld)
        {
            if (_targetSocket is null || _lastPointWorld == dragPointWorld)
                return;

            dragPointWorld -= _worldDragOffsetToSocket;

            if (_snapTranslations && !_snapInLocalSpace)
                SnapAbsoluteTranslation(ref dragPointWorld);

            Vector3 localDragPoint = Vector3.Transform(dragPointWorld, _inverseSocketParentDragMatrix);

            if (_snapTranslations && _snapInLocalSpace)
                SnapAbsoluteTranslation(ref localDragPoint);

            if (_targetSocket is Transform t) //Set directly for regular transforms
                t.Translation = localDragPoint;
            else if (_targetSocket is RigidBodyTransform rbt)
            {
                Matrix4x4 localMatrix =
                    Matrix4x4.CreateFromQuaternion(_localRotationDragStart) *
                    Matrix4x4.CreateTranslation(localDragPoint);

                var parentMtx = _targetSocket.ParentWorldMatrix;
                Matrix4x4 worldMtx = localMatrix * parentMtx;
                if (Matrix4x4.Decompose(worldMtx, out _, out Quaternion rotation, out Vector3 translation))
                {
                    // In edit mode (physics not running), set transform directly
                    // This triggers UpdateComponentInitialPose to sync initial pose
                    if (Engine.PlayMode.IsEditing)
                    {
                        rbt.SetPositionAndRotation(translation, rotation);
                    }
                    else if (_linkRB is not null)
                    {
                        _linkRB.KinematicTarget = (translation, rotation);
                    }
                }
            }
            else //Other transform types have to handle this themselves
            {
                Matrix4x4 localMatrix =
                    Matrix4x4.CreateScale(_localScaleDragStart) *
                    Matrix4x4.CreateFromQuaternion(_localRotationDragStart) *
                    Matrix4x4.CreateTranslation(localDragPoint);

                _targetSocket.DeriveLocalMatrix(localMatrix);
            }

        }

        private void SnapAbsoluteTranslation(ref Vector3 translation)
        {
            translation.X = translation.X.RoundedToNearest(_translationSnapBias, _translationSnapInterval);
            translation.Y = translation.Y.RoundedToNearest(_translationSnapBias, _translationSnapInterval);
            translation.Z = translation.Z.RoundedToNearest(_translationSnapBias, _translationSnapInterval);
        }

        private void DragScale(Vector3 dragPointWorld)
        {
            if (_targetSocket is null)
                return;

            Vector3 worldDelta = dragPointWorld - _lastPointWorld;
            if (worldDelta.LengthSquared() == 0.0f)
                return;

            if (_snapScale)
            {
                //Modify delta to move resulting world point to nearest snap
                Vector3 worldPoint = _targetSocket.WorldMatrix.Translation;
                Vector3 resultPoint = worldPoint + worldDelta;
                resultPoint.X = resultPoint.X.RoundedToNearest(_scaleSnapBias, _scaleSnapInterval);
                resultPoint.Y = resultPoint.Y.RoundedToNearest(_scaleSnapBias, _scaleSnapInterval);
                resultPoint.Z = resultPoint.Z.RoundedToNearest(_scaleSnapBias, _scaleSnapInterval);
                worldDelta = resultPoint - worldPoint;
            }

            if (_targetSocket is Transform t)
                t.AddWorldScaleDelta(worldDelta);
            else
            {
                Matrix4x4 parentInvMtx = _targetSocket.ParentInverseWorldMatrix;
                Vector3 localDelta = Vector3.TransformNormal(worldDelta, parentInvMtx);
                Matrix4x4.Decompose(_targetSocket.LocalMatrix, out Vector3 scale, out Quaternion rotation, out Vector3 translation);
                scale += localDelta;
                _targetSocket.DeriveLocalMatrix(Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation));
            }
        }

        private Matrix4x4 _invDragTfm = Matrix4x4.Identity;

        /// <summary>
        /// Returns a point relative to the local space of the target socket (origin at 0,0,0), clamped to the highlighted drag plane.
        /// </summary>
        /// <param name="camera">The camera viewing this tool, used for camera space drag clamping.</param>
        /// <param name="localRay">The mouse ray, transformed into the socket's local space.</param>
        /// <returns></returns>
        private Vector3 GetLocalDragPoint(XRCamera camera, Segment localRay)
        {
            //Convert all coordinates to local space

            Vector3 localCamPoint = Vector3.Transform(camera.Transform.WorldTranslation, _invDragTfm);
            Vector3 localDragPoint, unit;

            var start = localRay.Start;
            var dir = (localRay.End - localRay.Start).Normalized();

            switch (_mode)
            {
                case ETransformMode.Scale:
                case ETransformMode.Translate:
                    {
                        if (_hiCam)
                            _worldDragPlaneNormal = localCamPoint.Normalized();
                        else if (_hiAxis.X)
                        {
                            if (_hiAxis.Y)
                                _worldDragPlaneNormal = Vector3.UnitZ;
                            else if (_hiAxis.Z)
                                _worldDragPlaneNormal = Vector3.UnitY;
                            else
                            {
                                unit = Vector3.UnitX;
                                Vector3 perpPoint = Ray.GetClosestColinearPoint(Vector3.Zero, unit, localCamPoint);
                                _worldDragPlaneNormal = (localCamPoint - perpPoint).Normalized();

                                if (!GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _worldDragPlaneNormal, out localDragPoint))
                                    return _lastPointWorld;

                                return Ray.GetClosestColinearPoint(Vector3.Zero, unit, localDragPoint);
                            }
                        }
                        else if (_hiAxis.Y)
                        {
                            if (_hiAxis.X)
                                _worldDragPlaneNormal = Vector3.UnitZ;
                            else if (_hiAxis.Z)
                                _worldDragPlaneNormal = Vector3.UnitX;
                            else
                            {
                                unit = Vector3.UnitY;
                                Vector3 perpPoint = Ray.GetClosestColinearPoint(Vector3.Zero, unit, localCamPoint);
                                _worldDragPlaneNormal = localCamPoint - perpPoint;
                                _worldDragPlaneNormal.Normalized();

                                if (!GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _worldDragPlaneNormal, out localDragPoint))
                                    return _lastPointWorld;

                                return Ray.GetClosestColinearPoint(Vector3.Zero, unit, localDragPoint);
                            }
                        }
                        else if (_hiAxis.Z)
                        {
                            if (_hiAxis.X)
                                _worldDragPlaneNormal = Vector3.UnitY;
                            else if (_hiAxis.Y)
                                _worldDragPlaneNormal = Vector3.UnitX;
                            else
                            {
                                unit = Vector3.UnitZ;
                                Vector3 perpPoint = Ray.GetClosestColinearPoint(Vector3.Zero, unit, localCamPoint);
                                _worldDragPlaneNormal = (localCamPoint - perpPoint).Normalized();
                                if (!GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _worldDragPlaneNormal, out localDragPoint))
                                    return _lastPointWorld;

                                return Ray.GetClosestColinearPoint(Vector3.Zero, unit, localDragPoint);
                            }
                        }

                        if (GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _worldDragPlaneNormal, out localDragPoint))
                            return localDragPoint;
                    }
                    break;
                case ETransformMode.Rotate:
                    {
                        if (_hiCam)
                        {
                            if (GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _worldDragPlaneNormal = localCamPoint.Normalized(), out localDragPoint))
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

                            if (GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, _worldDragPlaneNormal = unit.Normalized(), out localDragPoint))
                                return localDragPoint;
                        }
                        else if (_hiSphere)
                        {
                            Vector3 worldPoint = -_invDragTfm.Translation;
                            float radius = camera.DistanceScaleOrthographic(worldPoint, _orbRadius);

                            if (GeoUtil.RayIntersectsSphere(start, dir, Vector3.Zero, radius * _circOrbScale, out localDragPoint))
                            {
                                _worldDragPlaneNormal = localDragPoint.Normalized();
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
            var dir = (localRay.End - localRay.Start).Normalized();

            if (!GeoUtil.RayIntersectsSphere(start, dir, Vector3.Zero, _circOrbScale, out Vector3 point))
            {
                //If no intersect is found, project the ray through the plane perpendicular to the camera.
                Vector3 localCameraPos = Vector3.Transform((camera.Transform.WorldTranslation), Transform.FirstChild()!.InverseWorldMatrix);
                if (GeoUtil.RayIntersectsPlane(start, dir, Vector3.Zero, localCameraPos, out point))
                {
                    //Clamp the point to edge of the sphere
                    point = Ray.PointAtLineDistance(Vector3.Zero, point, 1.0f);

                    //Point lies on circ line?
                    float distance = point.Length();
                    if (Math.Abs(distance - _circOrbScale) < _selectOrbScale)
                        _hiCam = true;
                }
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
            var dir = (localRay.End - localRay.Start).Normalized();

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
            var dir = (localRay.End - localRay.Start).Normalized();

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
        private Vector3 _worldDragOffsetToSocket;
        private Vector3 _worldDragOffsetToSocketParent;
        private Matrix4x4 _inverseSocketParentDragMatrix = Matrix4x4.Identity;
        private Matrix4x4 _inverseSocketDragMatrix = Matrix4x4.Identity;

        /// <summary>
        /// Returns true if intersecting one of the transform tool's various parts.
        /// </summary>
        public bool MouseMove(Segment cursor, XRCamera camera, bool pressed)
        {
            bool snapFound = true;
            if (pressed)
            {
                if ((_hiAxis.None && !_hiCam && !_hiSphere) || _targetSocket is null)
                    return false;

                Vector3 worldDragPoint = GetWorldDragPoint(cursor, camera);

                if (!_pressed)
                {
                    _invDragTfm = Transform.WorldMatrix.Inverted();
                    _inverseSocketParentDragMatrix = _targetSocket.ParentWorldMatrix.Inverted();
                    _inverseSocketDragMatrix = _targetSocket.WorldMatrix.Inverted();

                    _worldDragOffsetToSocketParent = worldDragPoint - _targetSocket.ParentWorldMatrix.Translation;
                    _worldDragOffsetToSocket = worldDragPoint - _targetSocket.WorldTranslation;

                    OnPressed();
                }

                _drag?.Invoke(worldDragPoint);
                _lastPointWorld = worldDragPoint;
            }
            else
            {
                if (_pressed)
                    OnReleased();

                Segment localRay = cursor.TransformedBy(Transform.FirstChild()!.InverseWorldMatrix);

                _hiAxis.X = _hiAxis.Y = _hiAxis.Z = _hiCam = _hiSphere = false;

                snapFound = _highlight?.Invoke(camera, localRay) ?? false;

                _axisMat[0].Parameter<ShaderVector4>(0)!.Value = _hiAxis.X ? ColorF4.Yellow : ColorF4.Red;
                _axisMat[1].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Y ? ColorF4.Yellow : ColorF4.Green;
                _axisMat[2].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Z ? ColorF4.Yellow : ColorF4.Blue;
                _screenMat!.Parameter<ShaderVector4>(0)!.Value = _hiCam ? ColorF4.Yellow : ColorF4.LightGray;

                GetDependentColors();

                _lastPointWorld = Vector3.Transform(GetLocalDragPoint(camera, localRay), Transform.FirstChild()!.WorldMatrix);
            }
            return snapFound;
        }

        private Vector3 GetWorldDragPoint(Segment cursor, XRCamera camera)
        {
            Matrix4x4 invRoot = Transform.FirstChild()!.InverseWorldMatrix;
            Segment localRay = cursor.TransformedBy(invRoot);
            Vector3 worldDragPoint = GetWorldDragPoint(camera, localRay);
            return worldDragPoint;
        }

        public BillboardTransform BillboardScaleTransform => (BillboardTransform)Transform.FirstChild()!;
        /// <summary>
        /// The root transform of this tool, scaled by the billboard transform.
        /// </summary>
        public Matrix4x4 ScaledWorldMatrix => Transform.FirstChild()!.WorldMatrix;
        public Matrix4x4 InvScaledWorldMatrix => Transform.FirstChild()!.InverseWorldMatrix;

        private Vector3 GetWorldDragPoint(XRCamera camera, Segment localRay)
            => Vector3.Transform(GetLocalDragPoint(camera, localRay), Transform.FirstChild()!.WorldMatrix);

        private void GetDependentColors()
        {
            if (_transPlaneMat.Any(m => m == null) || _scalePlaneMat.Any(m => m == null) || TransformMode == ETransformMode.Rotate)
                return;

            if (TransformMode == ETransformMode.Translate)
            {
                _transPlaneMat[0].Parameter<ShaderVector4>(0)!.Value = _hiAxis.X && _hiAxis.Y ? ColorF4.Yellow : ColorF4.Red;
                _transPlaneMat[1].Parameter<ShaderVector4>(0)!.Value = _hiAxis.X && _hiAxis.Z ? ColorF4.Yellow : ColorF4.Red;
                _transPlaneMat[2].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Y && _hiAxis.Z ? ColorF4.Yellow : ColorF4.Green;
                _transPlaneMat[3].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Y && _hiAxis.X ? ColorF4.Yellow : ColorF4.Green;
                _transPlaneMat[4].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Z && _hiAxis.X ? ColorF4.Yellow : ColorF4.Blue;
                _transPlaneMat[5].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Z && _hiAxis.Y ? ColorF4.Yellow : ColorF4.Blue;
            }
            else
            {
                _scalePlaneMat[0].Parameter<ShaderVector4>(0)!.Value = _hiAxis.Y && _hiAxis.Z ? ColorF4.Yellow : ColorF4.Red;
                _scalePlaneMat[1].Parameter<ShaderVector4>(0)!.Value = _hiAxis.X && _hiAxis.Z ? ColorF4.Yellow : ColorF4.Green;
                _scalePlaneMat[2].Parameter<ShaderVector4>(0)!.Value = _hiAxis.X && _hiAxis.Y ? ColorF4.Yellow : ColorF4.Blue;
            }
        }

        private void OnPressed()
        {
            UpdateDisplayTransform();

            _pressed = true;
            _mouseDown?.Invoke();
            MouseDown?.Invoke();
        }

        private void SetRootTransform(Matrix4x4 transform)
            => RootTransform.SetWorldMatrix(transform);

        private void OnReleased()
        {
            _pressed = false;
            _mouseUp?.Invoke();
            MouseUp?.Invoke();
        }

        public static bool RenderDebugInfo { get; set; } = false;
        public bool Highlighted => _hiAxis.Any || _hiCam || _hiSphere;

        private void Render()
        {
            if ((!_hiCam && !_hiSphere && !_hiAxis.Any) || Engine.Rendering.State.IsShadowPass || !RenderDebugInfo)
                return;

            var camera = Engine.Rendering.State.RenderingCamera;
            if (camera != null && !camera.CullingMask.Contains(DefaultLayers.GizmosIndex))
                return;
            
            Engine.Rendering.Debug.RenderPoint(_lastPointWorld, ColorF4.Black);

            if (camera != null)
                Engine.Rendering.Debug.RenderLine(
                    _lastPointWorld,
                    _lastPointWorld + Vector3.TransformNormal(_worldDragPlaneNormal, Transform.WorldMatrix) * camera.DistanceScaleOrthographic(Transform.WorldTranslation, 5.0f),
                    ColorF4.Black);
        }
    }
}

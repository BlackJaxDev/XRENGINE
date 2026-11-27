using System.Numerics;
using XREngine.Components;
using XREngine.Components.Scene.Mesh;
using XREngine.Components.Scene.Transforms;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Input;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.VR
{
    public abstract class VRDeviceModelComponent : XRComponent
    {
        private IRuntimeVrRenderModelHandle? _renderModelHandle;
        private IRuntimeVrRenderModelProvider? _subscribedRenderModelProvider;
        private SceneNode? _fallbackModelNode;
        private VRPlayerCharacterComponent? _cachedVrPlayer;
        private SceneNode? _cachedVrPlayerParent;
        private bool _onlyVisibleDuringCalibration = true;
        private bool _isActivated;
        private bool _fallbackSyncPending;
        private bool _fallbackNeedsRebuild;
        private DateTime _nextModelReverifyUtc = DateTime.MinValue;

        /// <summary>
        /// When true and this device belongs to a <see cref="VRPlayerCharacterComponent"/>,
        /// shows the controller or tracker model only while calibration is active.
        /// </summary>
        public bool OnlyVisibleDuringCalibration
        {
            get => _onlyVisibleDuringCalibration;
            set => SetField(ref _onlyVisibleDuringCalibration, value);
        }

        protected IRuntimeVrRenderModelHandle RenderModelHandle
            => _renderModelHandle ??= RuntimeVrRenderingServices.CreateRenderModelHandle(SceneNode, $"{GetType().Name} Render Model");

        public bool IsLoaded => _renderModelHandle?.IsLoaded ?? false;

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            _isActivated = true;
            _fallbackSyncPending = true;

            RuntimeVrStateServices.DeviceDetected += DeviceDetected;
            RuntimeVrStateServices.FrameAdvanced += ReverifyDevicesThrottled;
            _subscribedRenderModelProvider = RuntimeVrRenderingServices.RenderModelProvider;
            _subscribedRenderModelProvider.ModelsChanged += RenderModelsChanged;

            if (!IsLoaded)
                VerifyDevices();
        }

        protected override void OnComponentDeactivated()
        {
            _isActivated = false;
            _fallbackSyncPending = false;
            if (_subscribedRenderModelProvider is not null)
            {
                _subscribedRenderModelProvider.ModelsChanged -= RenderModelsChanged;
                _subscribedRenderModelProvider = null;
            }

            RuntimeVrStateServices.FrameAdvanced -= ReverifyDevicesThrottled;
            RuntimeVrStateServices.DeviceDetected -= DeviceDetected;
            _renderModelHandle?.Clear();
            if (_fallbackModelNode is { IsDestroyed: false, IsDestroyQueued: false } fallback &&
                !SceneNode.IsDestroyed && !SceneNode.IsDestroyQueued)
            {
                fallback.IsActiveSelf = false;
            }
            base.OnComponentDeactivated();
        }

        protected override void OnDestroying()
        {
            _isActivated = false;
            _fallbackSyncPending = false;
            if (SceneNode.IsDestroyed || SceneNode.IsDestroyQueued)
                _fallbackModelNode = null;
            else
                DestroyFallbackModel();

            _renderModelHandle?.Dispose();
            _renderModelHandle = null;
            base.OnDestroying();
        }

        private void DeviceDetected(RuntimeVrDeviceInfo device)
            => VerifyDevices();

        private void RenderModelsChanged()
            => VerifyDevices();

        private bool IsVisibleForCalibrationState()
        {
            if (!_onlyVisibleDuringCalibration)
                return true;

            VRPlayerCharacterComponent? player = ResolveOwningVrPlayer();
            return player is null || player.IsCalibrating;
        }

        private VRPlayerCharacterComponent? ResolveOwningVrPlayer()
        {
            SceneNode? parent = SceneNode?.Parent;
            if (_cachedVrPlayer is { IsDestroyed: false } cached && ReferenceEquals(_cachedVrPlayerParent, parent))
                return cached;

            _cachedVrPlayerParent = parent;
            _cachedVrPlayer = null;
            for (SceneNode? current = SceneNode; current is not null; current = current.Parent)
            {
                if (current.GetComponent<VRPlayerCharacterComponent>() is { IsDestroyed: false } found)
                {
                    _cachedVrPlayer = found;
                    break;
                }
            }

            return _cachedVrPlayer;
        }

        private void ReverifyDevicesThrottled()
        {
            bool trackerUsable = true;
            if (SceneNode.Transform is XREngine.Data.Components.Scene.VRTrackerTransform trackerTransform)
                trackerUsable = trackerTransform.PoseCurrentlyUsable;

            bool shouldShow = trackerUsable && IsVisibleForCalibrationState();
            _renderModelHandle?.SetVisible(shouldShow);
            if (_fallbackModelNode is { IsDestroyed: false, IsDestroyQueued: false } fallbackNode)
            {
                bool showFallback = shouldShow && !IsLoaded;
                if (fallbackNode.IsActiveSelf != showFallback)
                    fallbackNode.IsActiveSelf = showFallback;
            }

            if (IsLoaded)
            {
                _fallbackSyncPending = false;
                if (_fallbackModelNode is not null)
                    DestroyFallbackModel();
                return;
            }

            if (!trackerUsable)
                return;

            if (_fallbackSyncPending && shouldShow)
                SynchronizeFallbackModel();

            DateTime now = DateTime.UtcNow;
            if (now < _nextModelReverifyUtc)
                return;

            _nextModelReverifyUtc = now + TimeSpan.FromSeconds(1);
            VerifyDevices();
        }

        protected void VerifyDevices()
        {
            if (IsLoaded)
            {
                _fallbackSyncPending = false;
                return;
            }

            // Do not load or display a model for a paired tracker that has no usable pose.
            if (SceneNode.Transform is XREngine.Data.Components.Scene.VRTrackerTransform { PoseCurrentlyUsable: false })
                return;

            if (TryGetRenderModel(out RuntimeVrRenderModelDescriptor? model))
                LoadModelAsync(model);

            if (_fallbackModelNode is null || _fallbackNeedsRebuild)
                _fallbackSyncPending = true;
        }

        protected void LoadModelAsync(RuntimeVrRenderModelDescriptor? model)
        {
            if (model is null)
                return;

            RenderModelHandle.LoadModelAsync(model);
            RenderModelHandle.SetVisible(IsVisibleForCalibrationState());
        }

        protected void ClearLoadedModel()
        {
            _renderModelHandle?.Clear();
            _fallbackNeedsRebuild = true;
            _fallbackSyncPending = true;
        }

        /// <summary>
        /// Creates a canonical-scene fallback mesh node shown while no runtime render model is loaded.
        /// </summary>
        protected virtual SceneNode? CreateFallbackModelNode()
            => null;

        /// <summary>
        /// Builds a detached child node with opaque deferred PBR boxes, then attaches it so the
        /// fallback renders in ARP, RVC OpenXR eyes, and mirrors without mutating attached transforms.
        /// </summary>
        protected SceneNode? CreateFallbackBoxModelNode(string nodeName, ReadOnlySpan<(Vector3 Min, Vector3 Max, ColorF4 Color, float Roughness, float Metallic, float Emission)> parts)
        {
            if (SceneNode.IsDestroyed || SceneNode.World is null || parts.Length == 0 || ShaderHelper.LitColorFragDeferred() is null)
                return null;

            SubMesh[] subMeshes = new SubMesh[parts.Length];
            for (int index = 0; index < parts.Length; ++index)
            {
                var part = parts[index];
                XRMaterial material = XRMaterial.CreateAuthoredLitPbrColorMaterial(
                    part.Color,
                    specular: 0.5f,
                    roughness: part.Roughness,
                    metallic: part.Metallic,
                    emission: part.Emission);
                subMeshes[index] = new SubMesh(XRMesh.Shapes.SolidBox(part.Min, part.Max), material)
                {
                    CullingBounds = new AABB(part.Min, part.Max)
                };
            }

            SceneNode fallbackNode = new()
            {
                Name = nodeName,
                Layer = DefaultLayers.CharactersIndex,
            };
            ModelComponent modelComponent = fallbackNode.AddComponent<ModelComponent>()!;
            modelComponent.Model = new Model(subMeshes);
            var meshes = modelComponent.Meshes;
            for (int index = 0; index < meshes.Count; ++index)
                if (meshes[index].RenderInfo.Layer != DefaultLayers.CharactersIndex)
                    meshes[index].RenderInfo.Layer = DefaultLayers.CharactersIndex;
            fallbackNode.Transform.SetParent(SceneNode.Transform, false, EParentAssignmentMode.Immediate);
            return fallbackNode;
        }

        private void SynchronizeFallbackModel()
        {
            _fallbackSyncPending = false;

            if (IsDestroyed || SceneNode.IsDestroyed || SceneNode.IsDestroyQueued ||
                !_isActivated || !IsActiveInHierarchy || SceneNode.World is null || IsLoaded)
            {
                DestroyFallbackModel();
                return;
            }

            if (_fallbackNeedsRebuild)
            {
                _fallbackNeedsRebuild = false;
                DestroyFallbackModel();
            }

            if (_fallbackModelNode is { IsDestroyed: true })
                _fallbackModelNode = null;

            bool shouldShow = IsVisibleForCalibrationState();
            _fallbackModelNode ??= CreateFallbackModelNode();
            if (_fallbackModelNode is not null && _fallbackModelNode.IsActiveSelf != shouldShow)
                _fallbackModelNode.IsActiveSelf = shouldShow;
        }

        private void DestroyFallbackModel()
        {
            if (_fallbackModelNode is null)
                return;

            SceneNode node = _fallbackModelNode;
            _fallbackModelNode = null;
            if (!node.IsDestroyed && !node.IsDestroyQueued)
                node.Destroy();
        }

        protected abstract bool TryGetRenderModel(out RuntimeVrRenderModelDescriptor? model);
    }
}

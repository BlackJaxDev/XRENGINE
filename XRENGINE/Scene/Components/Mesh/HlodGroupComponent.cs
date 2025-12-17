using Extensions;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Numerics;
using XREngine.Data;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Info;
using XREngine.Rendering.Models;
using XREngine.Rendering.Tools;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components.Scene.Mesh
{
    /// <summary>
    /// Runtime HLOD group.
    ///
    /// - Discovers eligible <see cref="ModelComponent"/> renderables under this node.
    /// - Builds a batched proxy mesh (merged geometry grouped by material).
    /// - Swaps proxy vs originals per-camera during render collection.
    /// </summary>
    [Serializable]
    [Category("Rendering")]
    [DisplayName("HLOD Group")]
    [Description("Builds a batched proxy for child static meshes and swaps based on camera distance.")]
    public sealed class HLODGroupComponent : XRComponent, IRenderable
    {
        private const string ImposterNodeName = "__HLOD_Imposter";
        private const string ImposterCaptureNodeName = "__HLOD_ImposterCapture";

        private sealed record SourceHook(RenderInfo RenderInfo, RenderInfo.DelAddRenderCommandsCallback? OriginalCallback);

        private readonly RenderCommandMesh3D _renderCommand;
        private readonly RenderInfo3D _renderInfo;

        private readonly List<SourceHook> _sourceHooks = new();
        private XRMeshRenderer? _proxyRenderer;

        private SceneNode? _imposterNode;
        private OctahedralBillboardComponent? _imposterBillboard;
        private XRTexture2DArray? _imposterViews;

        private bool _built;
        private bool _building;

        public HLODGroupComponent()
        {
            _renderCommand = new RenderCommandMesh3D((int)EDefaultRenderPass.OpaqueForward)
            {
                WorldMatrixIsModelMatrix = true,
                WorldMatrix = Matrix4x4.Identity,
                Instances = 1u
            };

            _renderInfo = RenderInfo3D.New(this, _renderCommand);
            _renderInfo.PreCollectCommandsCallback = ProxyPreCollect;
            RenderedObjects = [_renderInfo];
        }

        [Category("HLOD")]
        [DisplayName("Enabled")]
        [Description("If false, HLOD swap is disabled and originals always render.")]
        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }
        private bool _enabled = true;

        [Category("HLOD")]
        [DisplayName("Build On Begin Play")]
        [Description("If true, builds the proxy automatically when the world begins play.")]
        public bool BuildOnBeginPlay
        {
            get => _buildOnBeginPlay;
            set => SetField(ref _buildOnBeginPlay, value);
        }
        private bool _buildOnBeginPlay = true;

        [Category("HLOD")]
        [DisplayName("Proxy Min Distance")]
        [Description("Camera distance threshold where the proxy becomes active.")]
        public float ProxyMinDistance
        {
            get => _proxyMinDistance;
            set => SetField(ref _proxyMinDistance, MathF.Max(0.0f, value));
        }
        private float _proxyMinDistance = 50.0f;

        [Category("HLOD")]
        [DisplayName("Use Octahedral Imposters")]
        [Description("If true, the HLOD group can bake and render a far-distance octahedral billboard imposter.")]
        public bool UseOctahedralImposters
        {
            get => _useOctahedralImposters;
            set => SetField(ref _useOctahedralImposters, value);
        }
        private bool _useOctahedralImposters;

        [Category("HLOD")]
        [DisplayName("Imposter Min Distance")]
        [Description("Camera distance threshold where the imposter becomes active (must be >= Proxy Min Distance).")]
        public float ImposterMinDistance
        {
            get => _imposterMinDistance;
            set => SetField(ref _imposterMinDistance, MathF.Max(0.0f, value));
        }
        private float _imposterMinDistance = 150.0f;

        [Category("HLOD")]
        [DisplayName("Generate Imposter On Rebuild")]
        [Description("If true, rebuild bakes the octahedral imposter from the generated proxy mesh (can be slow).")]
        public bool GenerateImposterOnRebuild
        {
            get => _generateImposterOnRebuild;
            set => SetField(ref _generateImposterOnRebuild, value);
        }
        private bool _generateImposterOnRebuild = true;

        [Category("HLOD")]
        [DisplayName("Imposter Sheet Size")]
        [Description("Resolution of each captured view in the 26-layer texture array.")]
        public uint ImposterSheetSize
        {
            get => _imposterSheetSize;
            set => SetField(ref _imposterSheetSize, Math.Max(128u, value));
        }
        private uint _imposterSheetSize = 512u;

        [Category("HLOD")]
        [DisplayName("Imposter Capture Padding")]
        [Description("Expands the capture bounds to avoid clipping. Typical values are ~1.1 - 1.3.")]
        public float ImposterCapturePadding
        {
            get => _imposterCapturePadding;
            set => SetField(ref _imposterCapturePadding, MathF.Max(1.0f, value));
        }
        private float _imposterCapturePadding = 1.15f;

        [Category("HLOD")]
        [DisplayName("Imposter Capture Depth")]
        [Description("If true, capture a depth texture during bake (optional).")]
        public bool ImposterCaptureDepth
        {
            get => _imposterCaptureDepth;
            set => SetField(ref _imposterCaptureDepth, value);
        }
        private bool _imposterCaptureDepth = true;

        [Category("HLOD")]
        [DisplayName("Use Furthest LOD")]
        [Description("If true, the proxy is built from each mesh's furthest LOD (lowest detail). If false, uses the current LOD at build time.")]
        public bool UseFurthestLod
        {
            get => _useFurthestLod;
            set => SetField(ref _useFurthestLod, value);
        }
        private bool _useFurthestLod = true;

        [Category("HLOD")]
        [DisplayName("Include Inactive Nodes")]
        [Description("If true, includes renderables under inactive scene nodes when building.")]
        public bool IncludeInactiveNodes
        {
            get => _includeInactiveNodes;
            set => SetField(ref _includeInactiveNodes, value);
        }
        private bool _includeInactiveNodes;

        [Browsable(false)]
        public RenderInfo[] RenderedObjects { get; private set; }

        protected internal override void OnBeginPlay()
        {
            base.OnBeginPlay();

            if (BuildOnBeginPlay)
                Rebuild();
        }

        protected override void OnTransformRenderWorldMatrixChanged(TransformBase transform, Matrix4x4 renderMatrix)
        {
            base.OnTransformRenderWorldMatrixChanged(transform, renderMatrix);
            _renderCommand.WorldMatrix = renderMatrix;
            _renderInfo.CullingOffsetMatrix = renderMatrix;
        }

        protected override void OnDestroying()
        {
            base.OnDestroying();
            UnhookSources();
            DestroyProxy();
            DestroyImposter();
        }

        public void Rebuild()
        {
            if (_building)
                return;

            _building = true;
            try
            {
                UnhookSources();
                DestroyProxy();
                DestroyImposter();

                var sources = CollectSources();
                if (sources.Count == 0)
                {
                    _built = false;
                    _renderCommand.Mesh = null;
                    _renderInfo.LocalCullingVolume = null;
                    return;
                }

                BuildProxyFromSources(sources);
                HookSources(sources);

                if (UseOctahedralImposters && GenerateImposterOnRebuild && _proxyRenderer is not null)
                    TryBuildImposterFromProxy();

                _built = _proxyRenderer is not null;
            }
            finally
            {
                _building = false;
            }
        }

        private bool ProxyPreCollect(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            if (!_built || !Enabled || _proxyRenderer is null)
                return false;

            if (camera is null)
                return false;

            return IsProxyActive(camera);
        }

        private bool IsProxyActive(XRCamera camera)
        {
            if (!Enabled || !_built)
                return false;

            float distance = camera.DistanceFromNearPlane(Transform.RenderTranslation);
            if (IsImposterActive(distance))
                return false;

            return distance >= ProxyMinDistance;
        }

        private bool IsImposterActive(float cameraDistance)
        {
            if (!Enabled || !_built)
                return false;

            if (!UseOctahedralImposters || !HasImposter)
                return false;

            float threshold = MathF.Max(ProxyMinDistance, ImposterMinDistance);
            return cameraDistance >= threshold;
        }

        private bool IsImposterActive(XRCamera camera)
            => IsImposterActive(camera.DistanceFromNearPlane(Transform.RenderTranslation));

        private bool ShouldHideSources(float cameraDistance)
            => cameraDistance >= ProxyMinDistance;

        private bool HasImposter => _imposterBillboard is not null && _imposterViews is not null;

        private void HookSources(List<RenderableMesh> sources)
        {
            foreach (var renderable in sources)
            {
                RenderInfo ri = renderable.RenderInfo;
                var prev = ri.PreCollectCommandsCallback;

                ri.PreCollectCommandsCallback = (info, passes, cam) =>
                {
                    // Preserve existing behavior (LOD selection, etc.), but skip when proxy is active.
                    if (Enabled && _built && cam is not null && ShouldHideSources(cam.DistanceFromNearPlane(Transform.RenderTranslation)))
                        return false;

                    return prev?.Invoke(info, passes, cam) ?? true;
                };

                _sourceHooks.Add(new SourceHook(ri, prev));
            }
        }

        private void UnhookSources()
        {
            foreach (var hook in _sourceHooks)
            {
                hook.RenderInfo.PreCollectCommandsCallback = hook.OriginalCallback;
            }
            _sourceHooks.Clear();
        }

        private void DestroyProxy()
        {
            _renderCommand.Mesh = null;

            _proxyRenderer?.Destroy();
            _proxyRenderer = null;
        }

        private void DestroyImposter()
        {
            _imposterViews = null;

            if (_imposterBillboard is not null)
            {
                _imposterBillboard.ImposterViews = null;
                _imposterBillboard.IsActive = false;
            }

            if (_imposterNode is not null)
            {
                _imposterNode.Destroy();
                _imposterNode = null;
                _imposterBillboard = null;
            }
        }

        private List<RenderableMesh> CollectSources()
        {
            var results = new List<RenderableMesh>();

            void VisitNode(TransformBase t)
            {
                var node = t.SceneNode;
                if (node is null)
                    return;

                if (node.Name.StartsWith("__HLOD_", StringComparison.Ordinal))
                    return;

                if (!IncludeInactiveNodes && !node.IsActiveInHierarchy)
                    return;

                foreach (var model in node.GetComponents<ModelComponent>())
                {
                    foreach (var mesh in model.Meshes)
                    {
                        if (mesh?.CurrentLODRenderer?.Mesh is not null && !mesh.IsSkinned)
                            results.Add(mesh);
                    }
                }

                foreach (var child in t.Children)
                    if (child is not null)
                        VisitNode(child);
            }

            if (!SceneNode.IsTransformNull)
                VisitNode(SceneNode.Transform);

            // Avoid including ourselves if we happen to be under a ModelComponent (rare).
            results.RemoveAll(x => ReferenceEquals(x.Component, this));
            return results;
        }

        private void TryBuildImposterFromProxy()
        {
            if (_proxyRenderer is null)
                return;

            _imposterNode ??= SceneNode.NewChild(name: ImposterNodeName);
            _imposterNode.IsActiveSelf = true;

            _imposterBillboard ??= _imposterNode.AddComponent<OctahedralBillboardComponent>();
            if (_imposterBillboard is null)
                return;

            _imposterBillboard.Name = "HLOD Octahedral Imposter";
            _imposterBillboard.IsActive = true;

            if (_imposterBillboard.RenderedObjects.FirstOrDefault() is RenderInfo ri)
                ri.PreCollectCommandsCallback = ImposterPreCollect;

            SceneNode captureNode = SceneNode.NewChild(name: ImposterCaptureNodeName);
            captureNode.IsActiveSelf = true;

            var captureModel = captureNode.AddComponent<ModelComponent>();
            if (captureModel is null)
            {
                captureNode.Destroy();
                return;
            }

            captureModel.Name = "HLOD Imposter Capture";
            captureModel.Model = BuildCaptureModelFromProxy(_proxyRenderer);
            if (captureModel.Model is null)
            {
                captureNode.Destroy();
                return;
            }

            var settings = new OctahedralImposterGenerator.Settings(ImposterSheetSize, ImposterCapturePadding, ImposterCaptureDepth);
            var result = OctahedralImposterGenerator.Generate(captureModel, settings);
            captureNode.Destroy();

            if (result is null)
                return;

            _imposterViews = result.Views;
            _imposterBillboard.ApplyCaptureResult(result, matchBounds: true);
        }

        private bool ImposterPreCollect(RenderInfo info, RenderCommandCollection passes, XRCamera? camera)
        {
            if (!_built || !Enabled)
                return false;

            if (!UseOctahedralImposters || !HasImposter)
                return false;

            if (camera is null)
                return false;

            return IsImposterActive(camera);
        }

        private static Model? BuildCaptureModelFromProxy(XRMeshRenderer proxy)
        {
            if (proxy.Submeshes is null || proxy.Submeshes.Count == 0)
                return null;

            List<SubMesh> subMeshes = new(proxy.Submeshes.Count);
            foreach (var sub in proxy.Submeshes)
            {
                if (sub.Mesh is null || sub.Material is null)
                    continue;

                var lod = new SubMeshLOD(sub.Material, sub.Mesh, 0.0f);
                subMeshes.Add(new SubMesh(lod));
            }

            return subMeshes.Count == 0 ? null : new Model(subMeshes);
        }

        private void BuildProxyFromSources(List<RenderableMesh> sources)
        {
            // Group-local space so the proxy moves with this node.
            Matrix4x4 groupInv = Matrix4x4.Invert(Transform.RenderMatrix, out var inv) ? inv : Matrix4x4.Identity;

            // Build per-material triangle primitives.
            var trianglesByMaterial = new Dictionary<XRMaterial, List<VertexTriangle>>(System.Collections.Generic.ReferenceEqualityComparer.Instance);

            AABB? localBounds = null;

            foreach (var renderable in sources)
            {
                XRMeshRenderer? srcRenderer = UseFurthestLod
                    ? renderable.LODs.Last?.Value?.Renderer
                    : renderable.CurrentLODRenderer;

                if (srcRenderer is null)
                    continue;

                // Determine which meshes/materials to consume.
                IEnumerable<(XRMesh mesh, XRMaterial material)> EnumerateMeshes()
                {
                    if (srcRenderer.Mesh is not null && srcRenderer.Material is not null)
                        yield return (srcRenderer.Mesh, srcRenderer.Material);
                    foreach (var sm in srcRenderer.Submeshes)
                        if (sm.Mesh is not null && sm.Material is not null)
                            yield return (sm.Mesh, sm.Material);
                }

                // Local-to-group transform = local * world * groupInv (row-vector convention).
                Matrix4x4 toGroup = renderable.Component.Transform.RenderMatrix * groupInv;

                // Update bounds using the renderable's world culling volume corners.
                if (renderable.RenderInfo is IOctreeItem item && item.WorldCullingVolume is Box box)
                {
                    foreach (var worldCorner in box.WorldCorners)
                    {
                        var localCorner = Vector3.Transform(worldCorner, groupInv);
                        localBounds = localBounds?.ExpandedToInclude(localCorner) ?? new AABB(localCorner, localCorner);
                    }
                }

                foreach (var (mesh, material) in EnumerateMeshes())
                {
                    if (mesh.Type != EPrimitiveType.Triangles)
                        continue;

                    var indices = mesh.GetIndices(EPrimitiveType.Triangles);
                    if (indices is null || indices.Length < 3)
                        continue;

                    var verts = mesh.Vertices;
                    if (verts is null || verts.Length == 0)
                        continue;

                    if (!trianglesByMaterial.TryGetValue(material, out var tris))
                    {
                        tris = new List<VertexTriangle>(Math.Max(64, indices.Length / 3));
                        trianglesByMaterial.Add(material, tris);
                    }

                    Vertex MakeVertex(int vertexIndex)
                    {
                        var src = verts[vertexIndex];
                        var v = src.HardCopy();
                        v.Weights = null;
                        v.Blendshapes = null;

                        v.Position = Vector3.Transform(v.Position, toGroup);
                        if (src.Normal.HasValue)
                            v.Normal = Vector3.Normalize(Vector3.TransformNormal(src.Normal.Value, toGroup));
                        if (src.Tangent.HasValue)
                            v.Tangent = Vector3.Normalize(Vector3.TransformNormal(src.Tangent.Value, toGroup));

                        return v;
                    }

                    for (int i = 0; i < indices.Length; i += 3)
                    {
                        int i0 = indices[i];
                        int i1 = indices[i + 1];
                        int i2 = indices[i + 2];
                        if ((uint)i0 >= (uint)verts.Length || (uint)i1 >= (uint)verts.Length || (uint)i2 >= (uint)verts.Length)
                            continue;

                        tris.Add(new VertexTriangle(MakeVertex(i0), MakeVertex(i1), MakeVertex(i2)));
                    }
                }
            }

            if (trianglesByMaterial.Count == 0)
            {
                _renderCommand.Mesh = null;
                _renderInfo.LocalCullingVolume = null;
                return;
            }

            var submeshes = new List<(XRMesh mesh, XRMaterial material)>(trianglesByMaterial.Count);
            foreach (var kvp in trianglesByMaterial)
            {
                XRMaterial mat = kvp.Key;
                var tris = kvp.Value;
                if (tris.Count == 0)
                    continue;

                XRMesh proxyMesh = XRMesh.Create(tris);
                proxyMesh.Name = $"HLOD_{SceneNode.Name}_{mat.Name ?? mat.ID.ToString()}";
                submeshes.Add((proxyMesh, mat));
            }

            if (submeshes.Count == 0)
            {
                _renderCommand.Mesh = null;
                _renderInfo.LocalCullingVolume = null;
                return;
            }

            _proxyRenderer = new XRMeshRenderer(submeshes)
            {
                GenerateAsync = false,
                Name = $"HLOD_{SceneNode.Name}",
            };

            _renderCommand.Mesh = _proxyRenderer;

            // Prefer the first material's render pass; this is a simplification.
            // The renderer supports per-submesh materials; the pass is used for sorting/collection.
            var firstMat = submeshes[0].material;
            _renderCommand.RenderPass = firstMat.RenderPass;

            _renderCommand.WorldMatrix = Transform.RenderMatrix;
            _renderInfo.CullingOffsetMatrix = Transform.RenderMatrix;
            _renderInfo.LocalCullingVolume = localBounds;
        }
    }
}

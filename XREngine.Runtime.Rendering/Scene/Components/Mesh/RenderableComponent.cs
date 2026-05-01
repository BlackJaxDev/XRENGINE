using System.Collections.Generic;
using XREngine.Core.Files;
using XREngine.Extensions;
using XREngine.Rendering;
using XREngine.Rendering.Info;

namespace XREngine.Components.Scene.Mesh
{
    /// <summary>
    /// Base scene component for anything that owns renderable meshes and exposes them to the runtime renderer.
    /// </summary>
    [Serializable]
    public abstract class RenderableComponent : XRComponent, IRenderable, IPostCookedBinaryDeserialize
    {
        #region State / override values

        /// <summary>
        /// Explicit layer override applied to every managed mesh (<c>-1</c> means no override).
        /// </summary>
        private int _meshLayer = -1;

        /// <summary>
        /// Explicit shadow-cast override applied to every managed mesh (<c>null</c> means no override).
        /// </summary>
        private bool? _meshCastsShadows = null;

        #endregion

        #region Mesh ownership defaults

        /// <summary>
        /// When >= 0, overrides the render layer for all meshes owned by this component.
        /// Set before or after assigning meshes; the setter propagates to existing render infos.
        /// </summary>
        public int MeshLayer
        {
            get => _meshLayer;
            set
            {
                _meshLayer = value;
                if (value >= 0)
                    foreach (var m in Meshes)
                        m.RenderInfo.Layer = value;
            }
        }

        /// <summary>
        /// When non-null, overrides shadow-casting for all meshes owned by this component.
        /// Set before or after assigning meshes; the setter propagates to existing render infos.
        /// </summary>
        public bool? MeshCastsShadows
        {
            get => _meshCastsShadows;
            set
            {
                _meshCastsShadows = value;
                if (value.HasValue)
                    foreach (var m in Meshes)
                        m.RenderInfo.CastsShadows = value.Value;
            }
        }

        /// <summary>
        /// Meshes owned by this component. Adding/removing from this collection updates
        /// <see cref="RenderedObjects"/> automatically via event handlers.
        /// </summary>
        public EventList<RenderableMesh> Meshes { get; private set; } = new EventList<RenderableMesh>() { ThreadSafe = true };

        /// <summary>
        /// Unique set of render infos currently tracked for world registration.
        /// </summary>
        public RenderInfo[] RenderedObjects { get; private set; } = [];

        #endregion

        #region Construction and lifecycle

        public RenderableComponent()
        {
            Meshes.PostAnythingAdded += Meshes_PostAnythingAdded;
            Meshes.PostAnythingRemoved += Meshes_PostAnythingRemoved;
        }

        protected override void OnComponentActivated()
        {
            base.OnComponentActivated();
            SyncRenderedObjectsWithWorld();
        }

        protected override void OnComponentDeactivated()
        {
            // Unregister all render infos from the world so they stop drawing.
            foreach (var ri in RenderedObjects)
                ri.WorldInstance = null;
            base.OnComponentDeactivated();
        }

        #endregion

        #region Mesh event handlers

        protected virtual void Meshes_PostAnythingRemoved(RenderableMesh item)
        {
            using var t = Engine.Profiler.Start("RenderableComponent.Meshes_PostAnythingRemoved");

            var ri = item.RenderInfo;
            int i = RenderedObjects.IndexOf(ri);
            if (i < 0)
                return;
            
            RenderedObjects = [.. RenderedObjects.Where((_, x) => x != i)];

            if (IsActive && ReferenceEquals(ri.WorldInstance, World))
                ri.WorldInstance = null;
        }

        protected virtual void Meshes_PostAnythingAdded(RenderableMesh item)
        {
            using var t = Engine.Profiler.Start("RenderableComponent.Meshes_PostAnythingAdded");

            var ri = item.RenderInfo;
            int i = RenderedObjects.IndexOf(ri);
            if (i >= 0)
                return;

            ApplyRenderInfoDefaults(ri);

            RenderedObjects = [.. RenderedObjects, ri];

            RegisterRenderInfoWithWorldIfActive(ri);
        }

        #endregion

        #region Mesh batch operations

        /// <summary>
        /// Adds meshes and updates <see cref="RenderedObjects"/> without raising list-add/remove events.
        /// Useful for import/restore paths where events are emitted separately.
        /// </summary>
        protected void AddMeshesWithoutEvents(IReadOnlyList<RenderableMesh> meshes)
        {
            if (meshes.Count == 0)
                return;

            for (int i = 0; i < meshes.Count; i++)
                Meshes.Add(meshes[i], reportAdded: false, reportModified: false);

            AppendRenderedObjects(meshes);
        }

        /// <summary>
        /// Clears all meshes and unregisters tracked render infos without event dispatch.
        /// </summary>
        protected void ClearMeshesWithoutEvents()
        {
            foreach (var ri in RenderedObjects)
                ri.WorldInstance = null;

            RenderedObjects = [];
            Meshes.Clear(reportRemovedRange: false, reportModified: false);
        }

        /// <summary>
        /// Appends new meshes into <see cref="RenderedObjects"/>, skipping duplicates.
        /// </summary>
        protected void AppendRenderedObjects(IReadOnlyList<RenderableMesh> meshes)
        {
            if (meshes.Count == 0)
                return;

            RenderInfo[] existing = RenderedObjects;
            RenderInfo[] combined = new RenderInfo[existing.Length + meshes.Count];
            Array.Copy(existing, combined, existing.Length);

            HashSet<RenderInfo>? seen = null;
            if (existing.Length > 0 || meshes.Count > 16)
            {
                seen = new HashSet<RenderInfo>(existing.Length + meshes.Count, ReferenceEqualityComparer.Instance);
                for (int i = 0; i < existing.Length; i++)
                    seen.Add(existing[i]);
            }

            int count = existing.Length;
            for (int i = 0; i < meshes.Count; i++)
            {
                RenderInfo ri = meshes[i].RenderInfo;
                if (seen is not null)
                {
                    if (!seen.Add(ri))
                        continue;
                }
                else if (ContainsRenderInfo(combined, count, ri))
                {
                    continue;
                }

                ApplyRenderInfoDefaults(ri);
                combined[count++] = ri;
                RegisterRenderInfoWithWorldIfActive(ri);
            }

            if (count == existing.Length)
                return;

            if (count != combined.Length)
                Array.Resize(ref combined, count);

            RenderedObjects = combined;
        }

        /// <summary>
        /// Rebuilds <see cref="RenderedObjects"/> from the current <see cref="Meshes"/> list,
        /// then re-synchronizes world registration state.
        /// </summary>
        protected void RebuildRenderedObjectsFromMeshes()
        {
            if (Meshes.Count == 0)
            {
                RenderedObjects = [];
                return;
            }

            RenderInfo[] renderInfos = new RenderInfo[Meshes.Count];
            HashSet<RenderInfo>? seen = Meshes.Count > 16
                ? new HashSet<RenderInfo>(Meshes.Count, ReferenceEqualityComparer.Instance)
                : null;

            int count = 0;
            foreach (RenderableMesh mesh in Meshes)
            {
                RenderInfo ri = mesh.RenderInfo;
                if (seen is not null)
                {
                    if (!seen.Add(ri))
                        continue;
                }
                else if (ContainsRenderInfo(renderInfos, count, ri))
                {
                    continue;
                }

                ApplyRenderInfoDefaults(ri);
                renderInfos[count++] = ri;
            }

            if (count != renderInfos.Length)
                Array.Resize(ref renderInfos, count);

            RenderedObjects = renderInfos;
            SyncRenderedObjectsWithWorld();
        }

        #endregion

        #region Internal helpers

        /// <summary>
        /// Applies component-level render defaults to a new render info before registration.
        /// </summary>
        private void ApplyRenderInfoDefaults(RenderInfo ri)
        {
            if (ri is not RenderInfo3D ri3d)
                return;

            if (_meshLayer >= 0)
                ri3d.Layer = _meshLayer;
            if (_meshCastsShadows.HasValue)
                ri3d.CastsShadows = _meshCastsShadows.Value;
        }

        /// <summary>
        /// Registers the render info with the current world when this component is active.
        /// </summary>
        private void RegisterRenderInfoWithWorldIfActive(RenderInfo ri)
        {
            if (IsActive)
                ri.WorldInstance = World as IRuntimeRenderInfo3DRegistrationTarget;
        }

        /// <summary>
        /// Returns true when <paramref name="renderInfo"/> already exists in the first
        /// <paramref name="count"/> items of <paramref name="renderInfos"/>.
        /// </summary>
        private static bool ContainsRenderInfo(RenderInfo[] renderInfos, int count, RenderInfo renderInfo)
        {
            for (int i = 0; i < count; i++)
                if (ReferenceEquals(renderInfos[i], renderInfo))
                    return true;

            return false;
        }

        /// <summary>
        /// Ensures every <see cref="RenderedObjects"/> entry is registered (or unregistered)
        /// with the current world instance to match the component's active state.
        /// </summary>
        private void SyncRenderedObjectsWithWorld()
        {
            var world = World as IRuntimeRenderInfo3DRegistrationTarget;
            foreach (var ri in RenderedObjects)
            {
                if (IsActive && world is not null)
                {
                    if (!ReferenceEquals(ri.WorldInstance, world))
                        ri.WorldInstance = world;
                }
                else
                {
                    ri.WorldInstance = null;
                }
            }
        }

        #endregion

        #region Deserialization lifecycle

        /// <summary>
        /// Restores event subscriptions and render-info indexing after cooked-binary deserialization.
        /// </summary>
        void IPostCookedBinaryDeserialize.OnPostCookedBinaryDeserialize()
        {
            // Cooked-binary restore can replace the Meshes EventList instance (setter/deserializer),
            // which drops the constructor-installed event subscriptions.
            Meshes.PostAnythingAdded -= Meshes_PostAnythingAdded;
            Meshes.PostAnythingRemoved -= Meshes_PostAnythingRemoved;
            Meshes.PostAnythingAdded += Meshes_PostAnythingAdded;
            Meshes.PostAnythingRemoved += Meshes_PostAnythingRemoved;

            // Rebuild and register render infos from current meshes.
            RebuildRenderedObjectsFromMeshes();

            //if (Environment.GetEnvironmentVariable("XRE_DEBUG_RENDER_DUMP") == "1")
            {
                Debug.RenderingEvery(
                    $"RenderableComponent.PostCooked.{GetHashCode()}",
                    TimeSpan.FromDays(1),
                    "[RenderDiag] RenderableComponent post-cooked. Node={0} Active={1} Meshes={2} RenderedObjects={3} WorldNull={4}",
                    SceneNode?.Name ?? "<null>",
                    IsActive,
                    Meshes.Count,
                    RenderedObjects.Length,
                    WorldAs<XREngine.Rendering.IRuntimeRenderWorld>() is null);
            }
        }

        #endregion
    }
}

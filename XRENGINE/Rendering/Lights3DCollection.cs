using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Threading;
using MIConvexHull;
using YamlDotNet.Serialization;
using XREngine.Components;
using XREngine.Components.Capture.Lights;
using XREngine.Components.Capture.Lights.Types;
using XREngine.Components.Lights;
using XREngine.Data;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Data.Trees;
using XREngine.Rendering;
using XREngine.Rendering.Lightmapping;

namespace XREngine.Scene
{
    public partial class Lights3DCollection(XRWorldInstance world) : XRBase
    {
        #region Constants

        private const float ProbePositionQuantization = 0.001f;

        #endregion

        #region Static Fields

        /// <summary>
        /// A 1x1 white texture used as a fallback shadow map when shadows are disabled.
        /// This prevents sampling from stale texture unit state.
        /// </summary>
        private static XRTexture2D? _dummyShadowMap;
        private static XRTexture2D DummyShadowMap => _dummyShadowMap ??= new XRTexture2D(1, 1, ColorF4.White);

        /// <summary>
        /// A 1x1x1 dummy depth texture array used as a fallback for forward cascaded shadow sampling.
        /// </summary>
        private static XRTexture2DArray? _dummyShadowMapArray;
        private static XRTexture2DArray DummyShadowMapArray => _dummyShadowMapArray ??= new XRTexture2DArray(
            1, 1, 1,
            EPixelInternalFormat.DepthComponent16,
            EPixelFormat.DepthComponent,
            EPixelType.Float);

        private static XRTextureCube? _dummyPointShadowMap;
        internal static XRTextureCube DummyPointShadowMap => _dummyPointShadowMap ??= new XRTextureCube(1u, EPixelInternalFormat.R16f, EPixelFormat.Red, EPixelType.Float, true, 1)
        {
            Resizable = false,
        };

        private static XRTexture2DArray? _dummyAmbientOcclusionArray;
        private static XRTexture2DArray DummyAmbientOcclusionArray => _dummyAmbientOcclusionArray ??= new XRTexture2DArray(
            1, 1, 2,
            EPixelInternalFormat.R16f,
            EPixelFormat.Red,
            EPixelType.Float);

        /// <summary>
        /// A 1x1 black cubemap used as a fallback environment/reflection map when no light probe is available.
        /// Prevents "program texture usage" GL errors from unbound samplerCube uniforms.
        /// </summary>
        private static XRTextureCube? _dummyEnvironmentCubemap;
        private static XRTextureCube DummyEnvironmentCubemap => _dummyEnvironmentCubemap ??= new XRTextureCube(1u);

        private static bool _loggedForwardLightingOnce = false;
        private static bool _loggedShadowMapEnabledOnce = false;

        // Last state-key logged by the ForwardShadowDiag line. Bumped every time a watched uniform
        // transitions so we get exactly one log line per toggle rather than per frame.
        private static ulong _lastForwardShadowDiagKey = ulong.MaxValue;

        // Last reason logged by the "No shadow tex" diagnostic in SetForwardLightingUniforms.
        // Logged exactly once per distinct reason so we don't spam the log every frame.
        private static string? _lastForwardShadowNoTexReason = null;

        #endregion

        #region Instance Fields

        private bool _capturing = false;
        private ITriangulation<LightProbeComponent, LightProbeCell>? _cells;
        private readonly List<PreparedFrustum> _frustumScratch = new(6);
        private readonly List<(Frustum Frustum, Vector3 Position, float MaxDistance)> _cameraFrustumScratch = new(4);
        private readonly ConcurrentQueue<CaptureWorkItem> _captureWorkQueue = new();
        private readonly HashSet<SceneCaptureComponentBase> _pendingCaptureComponents = new();
        private ulong _lastShadowMapsRenderFrameId = ulong.MaxValue;
        private readonly Stopwatch _captureBudgetStopwatch = new();
        private long _lastStreamingPressureLogFrameTicks = -1;
        private int _captureWorkQueueDepth;
        private int _lightProbeBatchCaptureNesting;
        private long _lightProbeBatchStructuralRefreshTicks;
        private int _lightProbeBatchStructuralRefreshCount;
        private long _lightProbeBatchContentRefreshTicks;
        private int _lightProbeBatchContentRefreshCount;

        // When shadow collection is culled by camera frusta, some lights may be intentionally skipped.
        // If we still swap their internal shadow-viewport buffers, we can end up swapping in an empty
        // visibility buffer (depending on viewport implementation), causing shadows to flicker on/off.
        // Track which lights actually collected this tick so SwapBuffers can preserve the last good buffers.
        private readonly HashSet<LightComponent> _shadowLightsCollectedThisTick = new();

        #endregion

        #region Properties

        public XRWorldInstance World { get; } = world;
        public bool IBLCaptured { get; private set; } = false;
        public bool RenderingShadowMaps { get; private set; } = false;
        public bool CollectingVisibleShadowMaps { get; private set; } = false;
        public bool LightProbeBatchCaptureActive => Volatile.Read(ref _lightProbeBatchCaptureNesting) > 0;
        public int PendingCaptureWorkItemCount => Math.Max(0, Volatile.Read(ref _captureWorkQueueDepth));
        public int PendingCaptureComponentCount
        {
            get
            {
                lock (_pendingCaptureComponents)
                    return _pendingCaptureComponents.Count;
            }
        }

        /// <summary>
        /// Budget in milliseconds for processing capture work (collect + render) per frame on the main thread.
        /// </summary>
        public double CaptureBudgetMilliseconds { get; set; } = 2.0;

        [YamlIgnore]
        public Octree<LightProbeCell> LightProbeTree { get; } = new(new AABB());
        public LightmapBakeManager LightmapBaking { get; } = new LightmapBakeManager(world);

        #endregion

        #region Light Collections

        /// <summary>
        /// All directional lights that are not baked and need to be rendered.
        /// </summary>
        public EventList<DirectionalLightComponent> DynamicDirectionalLights { get; } = new() { ThreadSafe = true };
        /// <summary>
        /// All point lights that are not baked and need to be rendered.
        /// </summary>
        public EventList<PointLightComponent> DynamicPointLights { get; } = new() { ThreadSafe = true };
        /// <summary>
        /// All spotlights that are not baked and need to be rendered.
        /// </summary>
        public EventList<SpotLightComponent> DynamicSpotLights { get; } = new() { ThreadSafe = true };
        /// <summary>
        /// All light probes in the scene.
        /// </summary>
        public EventList<LightProbeComponent> LightProbes { get; } = new() { ThreadSafe = true };

        #endregion

        #region Scene Capture

        public List<SceneCaptureComponentBase> CaptureComponents { get; } = [];

        public readonly record struct LightProbeBatchDiagnosticSnapshot(
            TimeSpan StructuralRefreshTime,
            int StructuralRefreshCount,
            TimeSpan ContentRefreshTime,
            int ContentRefreshCount);

        public void BeginLightProbeBatchCapture()
        {
            int nesting = Interlocked.Increment(ref _lightProbeBatchCaptureNesting);
            if (nesting == 1)
                ResetLightProbeBatchDiagnostics();
        }

        public void EndLightProbeBatchCapture()
        {
            int nesting = Interlocked.Decrement(ref _lightProbeBatchCaptureNesting);
            if (nesting < 0)
                Interlocked.Exchange(ref _lightProbeBatchCaptureNesting, 0);
        }

        public LightProbeBatchDiagnosticSnapshot ConsumeLightProbeBatchDiagnostics()
            => new(
                TimeSpan.FromTicks(Interlocked.Exchange(ref _lightProbeBatchStructuralRefreshTicks, 0)),
                Interlocked.Exchange(ref _lightProbeBatchStructuralRefreshCount, 0),
                TimeSpan.FromTicks(Interlocked.Exchange(ref _lightProbeBatchContentRefreshTicks, 0)),
                Interlocked.Exchange(ref _lightProbeBatchContentRefreshCount, 0));

        /// <summary>
        /// Enqueues a scene capture component for rendering.
        /// Progressive captures are decomposed into per-face work items so the
        /// per-frame budget can limit how many cubemap faces are rendered each frame.
        /// </summary>
        public void QueueForCapture(SceneCaptureComponentBase component)
        {
            lock (_pendingCaptureComponents)
            {
                if (!_pendingCaptureComponents.Add(component))
                    return;
            }

            if (component is SceneCaptureComponent scc && scc.ProgressiveRenderEnabled)
            {
                for (int i = 0; i < 6; i++)
                    EnqueueCaptureWorkItem(new CaptureWorkItem(scc, ECaptureWorkType.CubemapFace, i));
                EnqueueCaptureWorkItem(new CaptureWorkItem(scc, ECaptureWorkType.CaptureFinalize));
            }
            else
            {
                EnqueueCaptureWorkItem(new CaptureWorkItem(component, ECaptureWorkType.FullCapture));
            }
        }

        /// <summary>
        /// Removes a component from the pending-capture tracking set.
        /// Called after a full-capture or finalize work item completes.
        /// </summary>
        internal void CompletePendingCapture(SceneCaptureComponentBase component)
        {
            lock (_pendingCaptureComponents)
                _pendingCaptureComponents.Remove(component);
        }

        private bool ShouldDeferAuxiliaryCaptures()
        {
            if (!XRTexture2D.HasLargeProgressiveUploadBacklog)
                return false;

            long frameTicks = RuntimeRenderingHostServices.Current.LastRenderTimestampTicks;
            if (_lastStreamingPressureLogFrameTicks != frameTicks)
            {
                _lastStreamingPressureLogFrameTicks = frameTicks;
                Debug.Out(
                    $"[Lights3D] Deferring shadow/capture work due to texture streaming pressure. active={XRTexture2D.ActiveProgressiveUploadCount}, queued={XRTexture2D.QueuedProgressiveUploadCount}, bytesScheduledThisFrame={XRTexture2D.ProgressiveUploadBytesScheduledThisFrame}");
            }

            return true;
        }

        internal void RecordLightProbeResourceRefresh(bool structuralRefresh, TimeSpan elapsed)
        {
            if (!LightProbeBatchCaptureActive)
                return;

            if (structuralRefresh)
            {
                Interlocked.Add(ref _lightProbeBatchStructuralRefreshTicks, elapsed.Ticks);
                Interlocked.Increment(ref _lightProbeBatchStructuralRefreshCount);
            }
            else
            {
                Interlocked.Add(ref _lightProbeBatchContentRefreshTicks, elapsed.Ticks);
                Interlocked.Increment(ref _lightProbeBatchContentRefreshCount);
            }
        }

        private void ResetLightProbeBatchDiagnostics()
        {
            Interlocked.Exchange(ref _lightProbeBatchStructuralRefreshTicks, 0);
            Interlocked.Exchange(ref _lightProbeBatchStructuralRefreshCount, 0);
            Interlocked.Exchange(ref _lightProbeBatchContentRefreshTicks, 0);
            Interlocked.Exchange(ref _lightProbeBatchContentRefreshCount, 0);
        }

        private void EnqueueCaptureWorkItem(CaptureWorkItem item)
        {
            _captureWorkQueue.Enqueue(item);
            Interlocked.Increment(ref _captureWorkQueueDepth);
        }

        private void NoteCaptureWorkItemDequeued()
            => Interlocked.Decrement(ref _captureWorkQueueDepth);

        #endregion

        #region Capture Work Items

        public enum ECaptureWorkType : byte
        {
            /// <summary>Render a single cubemap face (collect + swap + render).</summary>
            CubemapFace,
            /// <summary>Finalize a cubemap capture cycle (mip gen, octa encode, IBL).</summary>
            CaptureFinalize,
            /// <summary>Full non-progressive capture (all faces + finalize in one call).</summary>
            FullCapture,
        }

        public readonly record struct CaptureWorkItem(
            SceneCaptureComponentBase Component,
            ECaptureWorkType WorkType,
            int FaceIndex = -1);

        #endregion
    }
}

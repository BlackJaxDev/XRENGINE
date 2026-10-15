using XREngine.Rendering;
using XREngine.Rendering.Commands;
using XREngine.Rendering.Shadows;

namespace XREngine.Components.Lights
{
    public partial class DirectionalLightComponent
    {
        /// <summary>
        /// The plan, the collection latch and the publication state of the primary shadow view. The light owns it for its
        /// whole lifetime and never replaces it.
        /// </summary>
        private readonly DirectionalPrimaryShadowPublicationState _primaryShadowPublication = new();

        /// <summary>
        /// Removes the primary atlas plan and the collection latch. The atlas planning calls this for each directional
        /// light at the start of each collect pass, so a plan or a latch never lives longer than its pass.
        /// </summary>
        internal void ClearPrimaryAtlasPlan()
            => _primaryShadowPublication.ClearPlan();

        /// <summary>
        /// Records the primary decision of the atlas planning for the current collect pass. The gate of
        /// <see cref="CollectVisibleItems"/> then uses the same decision.
        /// </summary>
        /// <param name="need">The first planning term that makes the primary shadow view necessary.</param>
        /// <param name="shadowOnlyCasters">
        /// Whether the scene can have shadow-casting renderables that no main view collected. The gate uses this term
        /// only when no planning term applies.
        /// </param>
        /// <param name="planFrameId">The shadow atlas frame of the render plan that this pass builds.</param>
        internal void RecordPrimaryAtlasPlan(EDirectionalPrimaryShadowNeed need, bool shadowOnlyCasters, ulong planFrameId)
            => _primaryShadowPublication.RecordPlan(need, shadowOnlyCasters, planFrameId);

        /// <summary>
        /// Converts the primary decision of the atlas planning to the first term that makes the primary shadow view
        /// necessary.
        /// </summary>
        /// <param name="enableCascadedShadows">Whether the light has cascaded shadows enabled.</param>
        /// <param name="cascadesActive">Whether cascaded shadows are enabled and a cascade source has published cascades.</param>
        /// <param name="needsPrimaryViewport">Whether the viewport test found a consumer of the primary shadow map.</param>
        /// <param name="noRelevantViewport">Whether the viewport test found no viewport with a camera.</param>
        /// <param name="pendingShadowCapture">Whether scene capture work, which reads the primary shadow map, is pending.</param>
        internal static EDirectionalPrimaryShadowNeed ResolvePlannedPrimaryAtlasNeed(
            bool enableCascadedShadows,
            bool cascadesActive,
            bool needsPrimaryViewport,
            bool noRelevantViewport,
            bool pendingShadowCapture)
        {
            if (!cascadesActive)
                return enableCascadedShadows
                    ? EDirectionalPrimaryShadowNeed.NoPublishedCascades
                    : EDirectionalPrimaryShadowNeed.CascadesDisabled;

            if (needsPrimaryViewport)
                return noRelevantViewport
                    ? EDirectionalPrimaryShadowNeed.NoRelevantViewport
                    : EDirectionalPrimaryShadowNeed.PrimaryViewport;

            return pendingShadowCapture
                ? EDirectionalPrimaryShadowNeed.PendingShadowCapture
                : EDirectionalPrimaryShadowNeed.None;
        }

        /// <summary>
        /// Makes the publication of the primary shadow view stale and removes the collection latch. Readers then
        /// decline the old package until the next gated or forced collection publishes a new one.
        /// </summary>
        internal void InvalidatePrimaryShadowPublication()
            => _primaryShadowPublication.Invalidate();

        /// <summary>
        /// Publishes the gated collection of the current collect pass. The view swaps and publishes only when the gate
        /// collected it in this pass and the updating frame package is still the latched one. The view also keeps a
        /// publication that a render-thread path made from the same gated collection before this swap. In all other
        /// cases the publication becomes stale, and the view swaps out a declined collection that stays in its updating
        /// buffer. No reader consumes a stale package, and a later gated collection then starts from an empty buffer.
        /// Thus the swap never publishes an uncollected, empty updating buffer as current, and a current publication
        /// never contains the casters of a declined collection.
        /// </summary>
        private void SwapPrimaryShadowViewport()
        {
            if (!TryGetPrimaryShadowViewportForProcessing(out XRViewport primaryViewport))
            {
                _primaryShadowPublication.Invalidate();
                return;
            }

            RenderCommandCollection commands = primaryViewport.RenderPipelineInstance.MeshRenderCommands;
            if (_primaryShadowPublication.TryTakeLatch(commands, out long generation, out ulong planFrameId))
            {
                SwapPrimaryShadowViewportBuffers(primaryViewport, commands);
                _primaryShadowPublication.TryMarkPublished(commands, generation, planFrameId, beforePassSwap: false);
                return;
            }

            bool hasUnpublishedCollection = DirectionalPrimaryShadowPublicationState.HasUnpublishedCollection(commands);
            if (_primaryShadowPublication.TryKeepPublicationAtPassSwap(commands) && !hasUnpublishedCollection)
                return;

            _primaryShadowPublication.MarkStale();
            if (hasUnpublishedCollection)
                SwapPrimaryShadowViewportBuffers(primaryViewport, commands);
        }

        /// <summary>
        /// Publishes a collection of the primary shadow view outside the gated swap, for a render-thread path that needs
        /// the view now. This method does not read or change the plan.
        /// </summary>
        /// <remarks>
        /// <para>
        /// When the collect thread latched a gated collection that waits for the swap, this method publishes that
        /// collection and collects nothing. A second collection into the same updating buffer would break the latch, so
        /// the swap of the pass would decline and the next frame would collect on the render thread again. The gated
        /// collection is the newest one, and the swap of its pass keeps the publication.
        /// </para>
        /// <para>
        /// Without a latch, this method collects into the shared updating buffer and swaps. A gated collection that
        /// starts before the swap can add to the same buffer, so the swap can publish a later package generation. That
        /// package holds this complete collection and part of the gated one, all from the current frame, so the caller
        /// can render it. The publication then stays stale, so no later frame reuses the package.
        /// </para>
        /// </remarks>
        /// <param name="viewport">The primary shadow view.</param>
        /// <param name="planFrameId">The shadow atlas frame of the plan that a new collection serves.</param>
        /// <returns>
        /// True when the swap put a collection of the current frame into the rendering package. False when no collection
        /// prepared a frame package or no swap ran. The publication is then stale.
        /// </returns>
        private bool CollectAndSwapPrimaryShadowViewportNow(XRViewport viewport, ulong planFrameId)
        {
            RenderCommandCollection commands = viewport.RenderPipelineInstance.MeshRenderCommands;
            if (_primaryShadowPublication.TryTakeLatch(commands, out long latchedGeneration, out ulong latchedPlanFrameId))
            {
                if (!SwapPrimaryShadowViewportBuffers(viewport, commands))
                {
                    _primaryShadowPublication.MarkStale();
                    return false;
                }

                _primaryShadowPublication.TryMarkPublished(commands, latchedGeneration, latchedPlanFrameId, beforePassSwap: true);
                return true;
            }

            long renderingGeneration = commands.RenderingBackendReadyPackage.PackageGeneration;
            viewport.CollectVisible(false);
            DirectionalShadowConsumptionTelemetry.Count(EDirectionalShadowConsumptionCounter.PrimaryCollections);
            if (!DirectionalPrimaryShadowPublicationState.TryReadPreparedGeneration(commands, renderingGeneration, out long generation))
            {
                // Swap out what the collection added, so that a later collection does not add to it.
                _primaryShadowPublication.MarkStale();
                if (DirectionalPrimaryShadowPublicationState.HasUnpublishedCollection(commands))
                    SwapPrimaryShadowViewportBuffers(viewport, commands);
                return false;
            }

            if (!SwapPrimaryShadowViewportBuffers(viewport, commands))
            {
                _primaryShadowPublication.MarkStale();
                return false;
            }

            _primaryShadowPublication.TryMarkPublished(commands, generation, planFrameId, beforePassSwap: false);
            return true;
        }

        /// <summary>
        /// Swaps the primary shadow view and records the swap. Returns false when the view did not swap, for example
        /// because its pipeline work is suspended.
        /// </summary>
        private static bool SwapPrimaryShadowViewportBuffers(XRViewport viewport, RenderCommandCollection commands)
        {
            BackendReadyFramePackage previousRendering = commands.RenderingBackendReadyPackage;
            long swapStart = DirectionalShadowConsumptionTelemetry.Timestamp();
            viewport.SwapBuffers();
            if (ReferenceEquals(previousRendering, commands.RenderingBackendReadyPackage))
                return false;

            DirectionalShadowConsumptionTelemetry.RecordPrimarySwap(swapStart, commands.GetRenderingMeshCommandCount());
            return true;
        }

        /// <summary>
        /// Collects and swaps only the cascade views, for a cascade render that needs them now. This method does not
        /// collect the primary shadow view, and it does not read or change the primary plan, latch or publication.
        /// </summary>
        private void CollectAndSwapCascadeSourcesNow()
        {
            if (!IsActiveInHierarchy || !CastsShadows)
                return;

            ValidateCookedWebGpuShadowConfiguration();
            CollectCascadeSourceVisibleItems(ShadowRequestSource.Desktop);
            CollectCascadeSourceVisibleItems(ShadowRequestSource.Hmd);
            SwapCascadeSourceBuffers(ShadowRequestSource.Desktop);
            SwapCascadeSourceBuffers(ShadowRequestSource.Hmd);
        }

        /// <summary>
        /// Returns the shadow atlas frame of the published render plan of this light's world, or zero when the light has
        /// no render world. The render thread executes the published plan, and the swap that publishes the next plan does
        /// not run during the render, so on the render thread this is the frame of the plan that is rendering.
        /// </summary>
        private ulong ResolvePublishedShadowAtlasPlanFrameId()
            => World.GetRenderWorld()?.Lights.ShadowAtlas.PublishedRenderPlan.FrameId ?? 0UL;
    }
}

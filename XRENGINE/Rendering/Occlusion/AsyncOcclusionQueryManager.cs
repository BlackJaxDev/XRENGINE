using System;
using System.Collections.Generic;
using XREngine.Data.Rendering;
using XREngine.Rendering.OpenGL;
using XREngine.Rendering.Vulkan;

namespace XREngine.Rendering.Occlusion
{
    /// <summary>
    /// Backend-agnostic pool + resolver for async hardware occlusion queries.
    ///
    /// This intentionally does NOT decide what to query or how to render query geometry.
    /// It only manages <see cref="XRRenderQuery"/> lifetimes and resolves results without blocking.
    /// </summary>
    public sealed class AsyncOcclusionQueryManager
    {
        private readonly object _lock = new();
        private readonly Queue<XRRenderQuery> _pool = new();

        public XRRenderQuery Acquire(EQueryTarget target)
        {
            XRRenderQuery query;
            bool isNew;
            lock (_lock)
            {
                isNew = _pool.Count == 0;
                query = isNew ? new XRRenderQuery() : _pool.Dequeue();
            }

            // Do NOT set CurrentQuery here — that field tracks whether a GL/VK query
            // is actively recording (between Begin and End). Setting it prematurely
            // causes GLRenderQuery.BeginQuery to call EndQuery on a non-active query,
            // triggering GL_INVALID_OPERATION.
            query.CurrentQuery = null;

            // Only generate API objects for brand-new queries; pooled ones are already generated.
            if (isNew)
                query.Generate();

            return query;
        }

        public void Release(XRRenderQuery query)
        {
            if (query is null)
                return;

            query.CurrentQuery = null;
            lock (_lock)
                _pool.Enqueue(query);
        }

        /// <summary>
        /// Attempts to resolve an occlusion query result without waiting.
        /// </summary>
        /// <returns>True when a result was available and read; otherwise false.</returns>
        public bool TryGetAnySamplesPassed(XRRenderQuery query, out bool anySamplesPassed)
        {
            anySamplesPassed = true;
            if (query is null)
                return false;

            // Prefer conservative availability checks to avoid implicit waits.
            if (AbstractRenderer.Current is OpenGLRenderer gl)
                return TryGetAnySamplesPassed(gl, query, out anySamplesPassed);

            if (AbstractRenderer.Current is VulkanRenderer vk)
                return TryGetAnySamplesPassed(vk, query, out anySamplesPassed);

            return false;
        }

        private static bool TryGetAnySamplesPassed(OpenGLRenderer renderer, XRRenderQuery query, out bool anySamplesPassed)
        {
            anySamplesPassed = true;
            GLRenderQuery? glQuery = renderer.GenericToAPI<GLRenderQuery>(query);
            if (glQuery is null)
                return false;

            long available = glQuery.GetQueryObject(EGetQueryObject.QueryResultAvailable);
            if (available == 0)
                return false;

            long result = glQuery.GetQueryObject(EGetQueryObject.QueryResult);
            anySamplesPassed = result != 0;
            return true;
        }

        private static bool TryGetAnySamplesPassed(VulkanRenderer renderer, XRRenderQuery query, out bool anySamplesPassed)
        {
            anySamplesPassed = true;
            VulkanRenderer.VkRenderQuery? vkQuery = renderer.GenericToAPI<VulkanRenderer.VkRenderQuery>(query);
            if (vkQuery is null)
                return false;

            if (!vkQuery.TryGetResultAvailable(out bool available) || !available)
                return false;

            if (!vkQuery.TryGetResult(out ulong result, wait: false))
                return false;

            anySamplesPassed = result != 0;
            return true;
        }
    }
}

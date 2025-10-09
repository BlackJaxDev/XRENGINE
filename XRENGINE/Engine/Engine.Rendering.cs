using System.Collections.Concurrent;
using XREngine.Rendering;
using XREngine.Rendering.Physics.Physx;
using XREngine.Scene;

namespace XREngine
{
    public static partial class Engine
    {
        public static partial class Rendering
        {
            //TODO: create objects for only relevant windows that house the viewports that this object is visible in

            /// <summary>
            /// Called when a new render object is created.
            /// Tells all current windows to create an API-specific object for this object.
            /// </summary>
            /// <param name="obj"></param>
            /// <returns></returns>
            public static AbstractRenderAPIObject?[] CreateObjectsForAllWindows(GenericRenderObject obj)
            {
                lock (Windows)
                    return [.. Windows.Select(window => window.Renderer.GetOrCreateAPIRenderObject(obj))];
            }

            /// <summary>
            /// Called when a new window is created.
            /// Tells all current render objects to create an API-specific object for this window.
            /// </summary>
            /// <param name="renderer"></param>
            /// <returns></returns>
            public static ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> CreateObjectsForNewRenderer(AbstractRenderer renderer)
            {
                ConcurrentDictionary<GenericRenderObject, AbstractRenderAPIObject> roDic = [];
                lock (GenericRenderObject.RenderObjectCache)
                {
                    foreach (var pair in GenericRenderObject.RenderObjectCache)
                        foreach (var obj in pair.Value)
                        {
                            AbstractRenderAPIObject? apiRO = renderer.GetOrCreateAPIRenderObject(obj);
                            if (apiRO is null)
                                continue;

                            roDic.TryAdd(obj, apiRO);
                            obj.AddWrapper(apiRO);
                        }
                }
                
                return roDic;
            }

            public static void DestroyObjectsForRenderer(AbstractRenderer renderer)
            {
                foreach (var pair in GenericRenderObject.RenderObjectCache)
                    foreach (var obj in pair.Value)
                        if (renderer.TryGetAPIRenderObject(obj, out var apiRO) && apiRO is not null)
                            obj.RemoveWrapper(apiRO);
            }

            public static AbstractPhysicsScene NewPhysicsScene()
                => new PhysxScene();

            public static VisualScene3D NewVisualScene()
                => new();

            public static RenderPipeline NewRenderPipeline()
                => new DefaultRenderPipeline();
        }
    }
}

using Silk.NET.Core.Native;

namespace XREngine.Rendering
{
    public abstract unsafe partial class AbstractRenderer<TAPI> where TAPI : NativeAPI
    {
        /// <summary>
        /// Base class for objects allocated by render apis.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="renderer"></param>
        public abstract class AbstractRenderObject<T>(T renderer) : AbstractRenderAPIObject(renderer.XRWindow) where T : AbstractRenderer<TAPI>
        {
            public T Renderer = renderer;
            protected TAPI Api => Renderer.Api;
        }
    }
}

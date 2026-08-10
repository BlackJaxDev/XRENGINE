using System;
using XREngine.Data.Colors;
using XREngine.Data.Geometry;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

namespace XREngine.Rendering.Vulkan
{
    public partial class VulkanRenderer
    {
        public override void ApplyRenderParameters(RenderingParameters parameters)
        {
            if (parameters is not null)
                BackendObjectContext.MeshServices.ApplyRenderParameters(parameters);
        }

        public override void SetEngineUniforms(XRRenderProgram program, XRCamera camera)
            => BackendObjectContext.MeshServices.SetEngineUniforms(program, camera);

        public override void SetMaterialUniforms(XRMaterial material, XRRenderProgram program)
            => BackendObjectContext.MeshServices.SetMaterialUniforms(
                material,
                program,
                GetOrCreateAPIRenderObject(program, generateNow: false) as VkRenderProgram,
                LayeredShadowUniformState.CaptureFromCurrentRenderingState());

        /// <summary>
        /// Sets the color mask for rendering, specifying which color channels are writable.
        /// </summary>
        /// <param name="red">Indicates whether the red color channel is writable.</param>
        /// <param name="green">Indicates whether the green color channel is writable.</param>
        /// <param name="blue">Indicates whether the blue color channel is writable.</param>
        /// <param name="alpha">Indicates whether the alpha color channel is writable.</param>
        public override void ColorMask(bool red, bool green, bool blue, bool alpha)
        {
            ActiveState.SetColorMask(red, green, blue, alpha);
        }

        public override void ClearColor(ColorF4 color)
        {
            ActiveState.SetClearColor(color);
        }

        public override void CropRenderArea(BoundingRectangle region)
        {
            ActiveState.SetScissor(region);
        }

        public override void SetRenderArea(BoundingRectangle region)
        {
            ActiveState.SetViewport(region);
        }

        public override void ClearRenderArea()
        {
            ActiveState.ClearViewport();
        }

        public override bool SetIndexedViewportScissors(
            ReadOnlySpan<BoundingRectangle> viewports,
            ReadOnlySpan<BoundingRectangle> scissors)
        {
            int count = Math.Min(viewports.Length, scissors.Length);
            if (count <= 0 ||
                !RuntimeEngine.Rendering.State.SupportsOpenGLViewportScissorArray ||
                count > RuntimeEngine.Rendering.State.MaxOpenGLViewports)
                return false;

            ActiveState.SetIndexedViewportScissors(viewports[..count], scissors[..count]);
            return true;
        }

        public override void ClearIndexedViewportScissors(int count)
        {
            if (count <= 0)
                return;

            ActiveState.ClearIndexedViewportScissors();
        }
    }
}

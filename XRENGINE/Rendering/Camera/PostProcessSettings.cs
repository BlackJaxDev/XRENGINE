using XREngine.Data.Core;

namespace XREngine.Rendering
{
    /// <summary>
    /// Abstract base class for all post-processing effect settings.
    /// Derived classes must implement <see cref="SetUniforms"/> to upload
    /// their parameters to shader programs.
    /// </summary>
    public abstract class PostProcessSettings : XRBase
    {
        /// <summary>
        /// Uploads the settings values as uniforms to the given render program.
        /// </summary>
        /// <param name="program">The render program to set uniforms on.</param>
        public abstract void SetUniforms(XRRenderProgram program);
    }
}

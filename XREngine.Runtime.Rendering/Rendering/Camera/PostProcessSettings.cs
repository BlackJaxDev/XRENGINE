using XREngine.Data.Core;
using System.Threading;

namespace XREngine.Rendering
{
    /// <summary>
    /// Abstract base class for all post-processing effect settings.
    /// Derived classes must implement <see cref="SetUniforms"/> to upload
    /// their parameters to shader programs.
    /// </summary>
    public abstract class PostProcessSettings : XRBase
    {
        private long _bindingGeneration = 1;

        /// <summary>
        /// Gets the non-zero monotonic generation of values published by this
        /// settings object.
        /// </summary>
        public ulong BindingGeneration
            => unchecked((ulong)Interlocked.Read(ref _bindingGeneration));

        /// <summary>
        /// Uploads the settings values as uniforms to the given render program.
        /// </summary>
        /// <param name="program">The render program to set uniforms on.</param>
        public abstract void SetUniforms(XRRenderProgram program);

        protected override void OnPropertyChanged<T>(
            string? propertyName,
            T previousValue,
            T currentValue)
        {
            base.OnPropertyChanged(propertyName, previousValue, currentValue);
            if (Interlocked.Increment(ref _bindingGeneration) == 0)
                Interlocked.CompareExchange(ref _bindingGeneration, 1, 0);
        }
    }
}

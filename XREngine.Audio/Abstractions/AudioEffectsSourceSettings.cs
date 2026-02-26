using System.Numerics;

namespace XREngine.Audio
{
    /// <summary>
    /// Per-source settings passed to <see cref="IAudioEffectsProcessor.AddSource"/>.
    /// </summary>
    public sealed class AudioEffectsSourceSettings
    {
        /// <summary>Initial world-space position of the source.</summary>
        public Vector3 Position { get; set; }

        /// <summary>Initial forward direction of the source (for directivity).</summary>
        public Vector3 Forward { get; set; } = -Vector3.UnitZ;
    }
}

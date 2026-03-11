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

        /// <summary>Number of channels the source submits to the effects processor.</summary>
        public int InputChannels { get; set; } = 1;

        /// <summary>Amount of Steam Audio spatialization to apply. 0 keeps the original stereo image, 1 is fully spatialized.</summary>
        public float SpatialBlend { get; set; } = 1.0f;
    }
}

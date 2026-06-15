namespace XREngine
{
    /// <summary>
    /// NVIDIA DLSS frame generation request mode.
    /// </summary>
    public enum ENvidiaDlssFrameGenerationMode
    {
        /// <summary>Frame generation is disabled.</summary>
        Off = 0,
        /// <summary>Request 1x frame generation.</summary>
        OneX = 1,
        /// <summary>Request 2x frame generation.</summary>
        TwoX = 2,
        /// <summary>Request 3x frame generation.</summary>
        ThreeX = 3
    }
}

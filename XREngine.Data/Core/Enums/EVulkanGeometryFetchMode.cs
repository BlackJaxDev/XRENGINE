namespace XREngine
{
    /// <summary>
    /// Controls optional Vulkan geometry-fetch strategy selection.
    /// </summary>
    public enum EVulkanGeometryFetchMode
    {
        /// <summary>
        /// Use atlas-backed geometry fetch path (default, production-safe).
        /// </summary>
        Atlas = 0,

        /// <summary>
        /// Experimental buffer-device-address style fetch prototype.
        /// Must remain opt-in until validated.
        /// </summary>
        BufferDeviceAddressPrototype = 1,
    }
}

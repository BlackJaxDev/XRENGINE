namespace XREngine.Rendering
{
    /// <summary>
    /// What happens to a data buffer's CPU (client-side) copy once its GPU copy is
    /// current on every backend.
    /// </summary>
    public enum EXRBufferClientCopyPolicy
    {
        /// <summary>
        /// Mesh-owned buffers follow <c>MeshBufferClientCopyPolicy</c> in the engine
        /// rendering settings; other buffers retain their copy.
        /// </summary>
        Default,

        /// <summary>The copy stays in private process memory.</summary>
        Retain,

        /// <summary>
        /// The copy moves to a copy-on-write mapping of a session spill file once
        /// uploaded. The buffer keeps a valid pointer and its metadata; reads page
        /// in from the file cache and writes copy the touched pages. Private memory
        /// falls by the buffer size.
        /// </summary>
        ReleaseAfterUpload,
    }
}

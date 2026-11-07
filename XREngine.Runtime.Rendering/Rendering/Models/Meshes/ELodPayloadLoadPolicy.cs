namespace XREngine.Rendering.Models
{
    /// <summary>Selects which LOD payloads a load of a cooked <see cref="SubMesh"/> reads.</summary>
    public enum ELodPayloadLoadPolicy
    {
        /// <summary>The load reads every LOD payload, so all LODs are resident as before the payload split. This is the default.</summary>
        All = 0,

        /// <summary>
        /// The load reads only the inline (coarsest) LOD. The other LODs stay not resident until a residency backend loads them.
        /// </summary>
        ProxyFirst = 1,
    }
}

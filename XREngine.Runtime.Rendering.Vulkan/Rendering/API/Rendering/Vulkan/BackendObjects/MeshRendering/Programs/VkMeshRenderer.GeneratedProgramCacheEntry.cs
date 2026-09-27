namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
    private sealed class GeneratedProgramCacheEntry
    {
        public required string Identity { get; init; }
        /// <summary>
        /// Identity without the material's shader revision, source signature and
        /// uber-variant hash. Entries sharing a group render the same material,
        /// axes, stages and generated vertex source, so a newer entry supersedes
        /// every older one in its group.
        /// </summary>
        public required string OwnerGroup { get; init; }
        /// <summary>
        /// Material the program was generated for. Derived pass variants (shadow
        /// caster, depth-normal, outline) are destroyed whenever their source
        /// material's shader state changes, and a program for a destroyed material
        /// can never be drawn again.
        /// </summary>
        public required XRMaterial Material { get; init; }
        /// <summary>
        /// Set once this entry has become the active program and its superseded
        /// group members were destroyed. Asynchronous linking can defer the first
        /// successful activation past the frame that created the entry.
        /// </summary>
        public bool SupersededEntriesEvicted;
        public required XRRenderProgram Data { get; init; }
        public required VkRenderProgram Program { get; init; }
    }
}

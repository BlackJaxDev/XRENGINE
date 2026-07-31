namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Compiled write plan for one material revision and reflected auto-uniform
/// block. <see cref="StaticBytes"/> contains only material-owned values; the
/// small dynamic member list is patched for each draw after the bytes are
/// copied into its stable frame-slot range.
/// </summary>
internal sealed class AutoUniformMaterialWritePlan(
    AutoUniformBlockInfo block,
    ulong programLinkGeneration,
    ulong materialLayoutVersion,
    ulong materialValueVersion,
    ulong runtimeUniformNameSignature,
    byte[] staticBytes,
    AutoUniformMember[] dynamicMembers)
{
    internal AutoUniformBlockInfo Block { get; } = block;
    internal ulong ProgramLinkGeneration { get; } = programLinkGeneration;
    internal ulong MaterialLayoutVersion { get; } = materialLayoutVersion;
    internal ulong MaterialValueVersion { get; } = materialValueVersion;
    internal ulong RuntimeUniformNameSignature { get; } = runtimeUniformNameSignature;
    internal byte[] StaticBytes { get; } = staticBytes;
    internal AutoUniformMember[] DynamicMembers { get; } = dynamicMembers;
}

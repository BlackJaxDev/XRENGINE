namespace XREngine.Rendering;

public partial class XRMesh
{
    /// <summary>Requests a new build when topology supersedes an in-flight ticket.</summary>
    private sealed class IndexBufferBuildInvalidatedException()
        : Exception("The mesh topology changed while its index buffer was being built.");
}

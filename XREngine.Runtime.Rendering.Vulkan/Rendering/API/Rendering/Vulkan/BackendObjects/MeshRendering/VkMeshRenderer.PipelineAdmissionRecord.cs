namespace XREngine.Rendering.Vulkan;

internal unsafe partial class VkMeshRenderer
{
	/// <summary>
	/// The result of one frame-loop pipeline admission of this renderer in one pass context: the material and the program
	/// of the admitted draw, and whether its graphics pipelines were ready, still compiling, or failed.
	/// </summary>
	/// <param name="Material">The material of the admitted draw.</param>
	/// <param name="Program">The program of the admitted draw.</param>
	/// <param name="LinkGeneration">The link generation of <paramref name="Program"/> at the admission.</param>
	/// <param name="State">The readiness of the graphics pipelines of the draw.</param>
	private readonly record struct PipelineAdmissionRecord(
		XRMaterial Material,
		VkRenderProgram? Program,
		ulong LinkGeneration,
		ERenderDrawReadiness State);
}

namespace XREngine.Rendering.Vulkan;

internal sealed partial class VulkanResourceRuntime
{
    internal bool TryBindSceneDatabaseDeviceAddressUniforms(
        XRRenderProgram program,
        XRDataBuffer drawMetadataBuffer,
        XRDataBuffer? instanceTransformBuffer,
        bool useInstanceTransformBuffer,
        string consumer)
        => BackendObjectContext is { } context &&
           TryBindSceneDatabaseDeviceAddressUniforms(context, WrapperLookup, program, drawMetadataBuffer, instanceTransformBuffer, useInstanceTransformBuffer, consumer);

    /// <summary>Publishes scene-database device addresses through stable resource authorities.</summary>
    internal bool TryBindSceneDatabaseDeviceAddressUniforms(
        VulkanBackendObjectContext context,
        VulkanWrapperLookupPort wrapperLookup,
        XRRenderProgram program,
        XRDataBuffer drawMetadataBuffer,
        XRDataBuffer? instanceTransformBuffer,
        bool useInstanceTransformBuffer,
        string consumer)
    {
        if (!program.HasUniform("XRE_DrawMetadataBufferAddress"))
            return true;

        if (!context.Supports(EVulkanDeviceCapability.BufferDeviceAddress))
        {
            Debug.RenderingWarningEvery(
                $"RenderDispatch.SceneDatabaseBda.Unsupported.{consumer}",
                TimeSpan.FromSeconds(2),
                "[RenderDispatch] Scene-database buffer-device-address shader '{0}' is active, but bufferDeviceAddress is unavailable.",
                consumer);
            return false;
        }

        program.Uniform("XRE_DrawMetadataCount", drawMetadataBuffer.ElementCount);
        if (!TryBindBuffer(context, wrapperLookup, program, drawMetadataBuffer, "XRE_DrawMetadataBufferAddress", consumer))
            return false;

        if (!useInstanceTransformBuffer || instanceTransformBuffer is null)
        {
            program.Uniform("XRE_TransformBufferAddress", new XREngine.Data.Vectors.UVector2(0u, 0u));
            program.Uniform("XRE_TransformFloatCount", 0u);
            return true;
        }

        program.Uniform("XRE_TransformFloatCount", instanceTransformBuffer.Length / (uint)sizeof(float));
        return TryBindBuffer(context, wrapperLookup, program, instanceTransformBuffer, "XRE_TransformBufferAddress", consumer);
    }

    private bool TryBindBuffer(VulkanBackendObjectContext context, VulkanWrapperLookupPort wrapperLookup, XRRenderProgram program, XRDataBuffer buffer, string uniformName, string consumer)
    {
        if (wrapperLookup.GetOrCreate(buffer, generateNow: true) is not VkDataBuffer apiBuffer)
        {
            Record(context, buffer, 0ul, consumer, false, "wrapper-unavailable");
            WarnUnavailable(buffer, consumer, "wrapper-unavailable");
            return false;
        }

        apiBuffer.Generate();
        if (!apiBuffer.TryGetDeviceAddress(out ulong address) || address == 0ul)
        {
            apiBuffer.PushData();
            apiBuffer.TryGetDeviceAddress(out address);
        }

        bool consumed = address != 0ul;
        Record(context, buffer, address, consumer, consumed, consumed ? "resolved" : "address-unresolved");
        if (!consumed)
        {
            WarnUnavailable(buffer, consumer, "address-unresolved");
            return false;
        }

        program.Uniform(
            uniformName,
            new XREngine.Data.Vectors.UVector2((uint)address, (uint)(address >> 32)));
        return true;
    }

    private void Record(VulkanBackendObjectContext context, XRDataBuffer buffer, ulong address, string consumer, bool consumed, string reason)
        => Buffers.RecordDeviceAddressConsumer(context, buffer, address, consumer, consumed, reason);

    private static void WarnUnavailable(XRDataBuffer buffer, string consumer, string reason)
        => Debug.RenderingWarningEvery(
            $"RenderDispatch.SceneDatabaseBda.Unresolved.{consumer}.{buffer.AttributeName}.{reason}",
            TimeSpan.FromSeconds(2),
            "[RenderDispatch] Scene-database buffer-device-address consumer '{0}' cannot resolve buffer '{1}' ({2}); skipping this Vulkan prototype draw bucket instead of falling back silently.",
            consumer,
            buffer.AttributeName,
            reason);
}

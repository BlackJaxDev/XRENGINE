using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan.DeviceBootstrap;

/// <summary>
/// Immutable EXT mesh-shader negotiation evidence retained after logical-device
/// creation. Physical-device exposure and logical-device enablement are kept
/// separate so a strategy downgrade has an actionable cause.
/// </summary>
internal readonly unsafe record struct VulkanMeshShaderCapabilitySnapshot(
    bool ExtensionAdvertised,
    bool ExtensionRequested,
    bool ExtensionEnabled,
    bool TaskShaderAdvertised,
    bool MeshShaderAdvertised,
    bool MeshShaderQueriesAdvertised,
    bool TaskShaderEnabled,
    bool MeshShaderEnabled,
    bool MeshShaderQueriesEnabled,
    bool CommandTableLoaded,
    uint NegotiatedApiVersion,
    PhysicalDeviceMeshShaderPropertiesEXT Properties)
{
    // The shipped EXT mesh shader writes gl_Position plus fourteen per-vertex
    // locations. Locations are sparse: the highest occupied location is 27,
    // so MaxMeshOutputComponents must cover 28 locations (112 components),
    // while the actual per-vertex storage is only 60 scalar components
    // (fourteen locations plus gl_Position).
    private const uint PortableTaskPayloadBytes = 32u;
    private const uint PortableMeshWorkGroupInvocations = 32u;
    private const uint PortableMeshOutputVertices = 64u;
    private const uint PortableMeshOutputPrimitives = 124u;
    private const uint PortableMeshOutputLocationSpaceComponents = 112u;
    private const uint PortableMeshEffectivePerVertexOutputScalars = 60u;

    private bool HasCompatibleShaderArtifactTarget
        // Current shaderc/glslang requires Vulkan 1.3 / SPIR-V 1.6 for the
        // shipped GL_EXT_mesh_shader sources. Never advertise the path on an
        // OpenXR-negotiated lower API while those artifacts retain that target.
        => NegotiatedApiVersion >= Vk.Version13;

    private uint MaxTaskWorkGroupCountX
    {
        get
        {
            PhysicalDeviceMeshShaderPropertiesEXT properties = Properties;
            return properties.MaxTaskWorkGroupCount[0];
        }
    }

    private uint MaxTaskWorkGroupSizeX
    {
        get
        {
            PhysicalDeviceMeshShaderPropertiesEXT properties = Properties;
            return properties.MaxTaskWorkGroupSize[0];
        }
    }

    private uint MaxMeshWorkGroupCountX
    {
        get
        {
            PhysicalDeviceMeshShaderPropertiesEXT properties = Properties;
            return properties.MaxMeshWorkGroupCount[0];
        }
    }

    private uint MaxMeshWorkGroupSizeX
    {
        get
        {
            PhysicalDeviceMeshShaderPropertiesEXT properties = Properties;
            return properties.MaxMeshWorkGroupSize[0];
        }
    }

    private uint RequiredMeshOutputMemoryBytes
    {
        get
        {
            uint roundedVertexCount = RoundUpToGranularity(
                PortableMeshOutputVertices,
                Properties.MeshOutputPerVertexGranularity);
            return checked(roundedVertexCount * PortableMeshEffectivePerVertexOutputScalars * sizeof(uint));
        }
    }

    private static uint RoundUpToGranularity(uint value, uint granularity)
    {
        if (granularity == 0u)
            return uint.MaxValue;

        return checked(((value + granularity - 1u) / granularity) * granularity);
    }

    internal bool HasPortableMeshletProfile
        // The task shader launches one invocation and transfers a 32-byte
        // payload. The mesh shader launches 32 invocations and emits the
        // portable cooked profile (64 vertices / 124 primitives). Preferred
        // workgroup counts are performance hints only; they never decide
        // whether the shader can execute correctly.
        => HasCompatibleShaderArtifactTarget &&
           Properties.MaxTaskWorkGroupTotalCount >= 1u &&
           MaxTaskWorkGroupCountX >= 1u &&
           Properties.MaxTaskWorkGroupInvocations >= 1u &&
           MaxTaskWorkGroupSizeX >= 1u &&
           Properties.MaxTaskPayloadSize >= PortableTaskPayloadBytes &&
           Properties.MaxTaskPayloadAndSharedMemorySize >= PortableTaskPayloadBytes &&
           Properties.MaxMeshWorkGroupTotalCount >= 1u &&
           MaxMeshWorkGroupCountX >= 1u &&
           Properties.MaxMeshWorkGroupInvocations >= PortableMeshWorkGroupInvocations &&
           MaxMeshWorkGroupSizeX >= PortableMeshWorkGroupInvocations &&
           Properties.MaxMeshPayloadAndSharedMemorySize >= PortableTaskPayloadBytes &&
           Properties.MaxMeshOutputComponents >= PortableMeshOutputLocationSpaceComponents &&
           Properties.MaxMeshOutputVertices >= PortableMeshOutputVertices &&
           Properties.MaxMeshOutputPrimitives >= PortableMeshOutputPrimitives &&
           Properties.MeshOutputPerVertexGranularity != 0u &&
           Properties.MaxMeshOutputMemorySize >= RequiredMeshOutputMemoryBytes &&
           Properties.MaxMeshPayloadAndOutputMemorySize >= PortableTaskPayloadBytes + RequiredMeshOutputMemoryBytes;

    internal string GetDispatchFailureReason()
    {
        if (!ExtensionAdvertised)
            return "VK_EXT_mesh_shader is not advertised by the selected physical device.";
        if (!ExtensionRequested)
            return "VK_EXT_mesh_shader is advertised but not requested by the renderer device-extension policy.";
        if (!ExtensionEnabled)
            return "VK_EXT_mesh_shader was requested but was not enabled on the logical device.";
        if (!TaskShaderAdvertised || !MeshShaderAdvertised)
            return $"VK_EXT_mesh_shader is enabled but physical features are incomplete (taskShader={TaskShaderAdvertised}, meshShader={MeshShaderAdvertised}).";
        if (!TaskShaderEnabled || !MeshShaderEnabled)
            return $"VK_EXT_mesh_shader features were advertised but not enabled (taskShader={TaskShaderEnabled}, meshShader={MeshShaderEnabled}).";
        if (!CommandTableLoaded)
            return "VK_EXT_mesh_shader features were enabled but the EXT command table failed to load.";
        if (!HasCompatibleShaderArtifactTarget)
            return $"The negotiated Vulkan API version 0x{NegotiatedApiVersion:X8} is below Vulkan 1.3 required by the shipped task/mesh SPIR-V 1.6 artifacts.";
        if (!HasPortableMeshletProfile)
            return $"VK_EXT_mesh_shader device limits cannot satisfy the portable task=1/payload=32-byte/mesh=32/64-vertex/124-primitive profile (taskTotal={Properties.MaxTaskWorkGroupTotalCount}, taskCountX={MaxTaskWorkGroupCountX}, taskInvocations={Properties.MaxTaskWorkGroupInvocations}, taskSizeX={MaxTaskWorkGroupSizeX}, preferredTaskInvocations={Properties.MaxPreferredTaskWorkGroupInvocations}, taskPayload={Properties.MaxTaskPayloadSize}, taskPayloadShared={Properties.MaxTaskPayloadAndSharedMemorySize}, meshTotal={Properties.MaxMeshWorkGroupTotalCount}, meshCountX={MaxMeshWorkGroupCountX}, meshInvocations={Properties.MaxMeshWorkGroupInvocations}, meshSizeX={MaxMeshWorkGroupSizeX}, preferredMeshInvocations={Properties.MaxPreferredMeshWorkGroupInvocations}, meshPayloadShared={Properties.MaxMeshPayloadAndSharedMemorySize}, outputComponents={Properties.MaxMeshOutputComponents}, requiredLocationComponents={PortableMeshOutputLocationSpaceComponents}, effectiveVertexScalars={PortableMeshEffectivePerVertexOutputScalars}, outputVertexGranularity={Properties.MeshOutputPerVertexGranularity}, requiredOutputMemory={RequiredMeshOutputMemoryBytes}, outputVertices={Properties.MaxMeshOutputVertices}, outputPrimitives={Properties.MaxMeshOutputPrimitives}, meshOutputMemory={Properties.MaxMeshOutputMemorySize}, meshPayloadOutputMemory={Properties.MaxMeshPayloadAndOutputMemorySize}).";

        return "VK_EXT_mesh_shader indirect-count dispatch is available.";
    }

    /// <summary>Returns a compact, stable bootstrap record for capture telemetry.</summary>
    internal string CreateCompactLadder()
        => $"extension(advertised={ExtensionAdvertised},requested={ExtensionRequested},enabled={ExtensionEnabled});" +
           $"features(taskAdvertised={TaskShaderAdvertised},meshAdvertised={MeshShaderAdvertised},taskEnabled={TaskShaderEnabled},meshEnabled={MeshShaderEnabled},queriesAdvertised={MeshShaderQueriesAdvertised},queriesEnabled={MeshShaderQueriesEnabled});" +
           $"commands(loaded={CommandTableLoaded});" +
           $"shaderTarget(negotiatedApi=0x{NegotiatedApiVersion:X8},requiredApi=0x{Vk.Version13:X8},compatible={HasCompatibleShaderArtifactTarget});" +
           $"limits(taskTotal={Properties.MaxTaskWorkGroupTotalCount},taskCountX={MaxTaskWorkGroupCountX},taskInvocations={Properties.MaxTaskWorkGroupInvocations},taskSizeX={MaxTaskWorkGroupSizeX},preferredTaskInvocations={Properties.MaxPreferredTaskWorkGroupInvocations},taskPayload={Properties.MaxTaskPayloadSize},taskPayloadShared={Properties.MaxTaskPayloadAndSharedMemorySize},meshTotal={Properties.MaxMeshWorkGroupTotalCount},meshCountX={MaxMeshWorkGroupCountX},meshInvocations={Properties.MaxMeshWorkGroupInvocations},meshSizeX={MaxMeshWorkGroupSizeX},preferredMeshInvocations={Properties.MaxPreferredMeshWorkGroupInvocations},meshPayloadShared={Properties.MaxMeshPayloadAndSharedMemorySize},outputComponents={Properties.MaxMeshOutputComponents},requiredLocationComponents={PortableMeshOutputLocationSpaceComponents},effectiveVertexScalars={PortableMeshEffectivePerVertexOutputScalars},outputVertexGranularity={Properties.MeshOutputPerVertexGranularity},requiredOutputMemory={RequiredMeshOutputMemoryBytes},outputVertices={Properties.MaxMeshOutputVertices},outputPrimitives={Properties.MaxMeshOutputPrimitives},meshOutputMemory={Properties.MaxMeshOutputMemorySize},meshPayloadOutputMemory={Properties.MaxMeshPayloadAndOutputMemorySize});" +
           $"portableProfile={HasPortableMeshletProfile}";

    /// <summary>Returns the first failed readiness rung together with its expected/actual evidence.</summary>
    internal string GetFailedRung()
    {
        if (!ExtensionAdvertised)
            return "extension-advertised: expected=true actual=false";
        if (!ExtensionRequested)
            return "extension-requested: expected=true actual=false";
        if (!ExtensionEnabled)
            return "extension-enabled: expected=true actual=false";
        if (!TaskShaderAdvertised)
            return "task-feature-advertised: expected=true actual=false";
        if (!MeshShaderAdvertised)
            return "mesh-feature-advertised: expected=true actual=false";
        if (!TaskShaderEnabled)
            return "task-feature-enabled: expected=true actual=false";
        if (!MeshShaderEnabled)
            return "mesh-feature-enabled: expected=true actual=false";
        if (!CommandTableLoaded)
            return "ext-command-table: expected=loaded actual=missing";
        if (!HasCompatibleShaderArtifactTarget)
            return $"shader-artifact-target: expectedApi>=0x{Vk.Version13:X8} actualApi=0x{NegotiatedApiVersion:X8}";
        if (!HasPortableMeshletProfile)
            return $"portable-profile: expected=task1/payload32/mesh32/vertices64/primitives124/locationComponents{PortableMeshOutputLocationSpaceComponents}/effectiveVertexScalars{PortableMeshEffectivePerVertexOutputScalars}/outputBytes{RequiredMeshOutputMemoryBytes} actual=insufficient-device-limits";

        return "ready";
    }
}

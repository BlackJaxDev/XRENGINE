using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace XREngine.Editor.Publishing;

/// <summary>Names desktop-only references before an authored game enters a browser publish.</summary>
internal static class BrowserGameAssemblyAudit
{
    private static readonly string[] DesktopAssemblies =
    [
        "XREngine.Runtime.Bootstrap", "XREngine.Runtime.ModelAssetPipeline", "XREngine.Runtime.Physics.PhysX",
        "XREngine.Runtime.Platform.Desktop", "XREngine.Runtime.VR", "XREngine.Runtime.Rendering.OpenGL",
        "XREngine.Runtime.Rendering.Vulkan", "XREngine.Audio.OpenAL", "XREngine.Audio.NAudio", "XREngine.Editor",
        "OpenVR.NET", "NAudio", "System.Drawing.Common", "PresentationCore", "PresentationFramework", "WindowsBase"
    ];

    internal static void Validate(string assemblyPath)
    {
        using FileStream stream = File.OpenRead(assemblyPath);
        using PEReader image = new(stream);
        if (!image.HasMetadata)
            throw new InvalidDataException($"Browser game assembly '{Path.GetFileName(assemblyPath)}' has no managed metadata.");
        MetadataReader metadata = image.GetMetadataReader();
        List<string> failures = [];
        foreach (AssemblyReferenceHandle handle in metadata.AssemblyReferences)
        {
            string name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            if (DesktopAssemblies.Any(blocked => name == blocked || name.StartsWith(blocked + ".", StringComparison.Ordinal)))
                failures.Add($"assembly '{name}'");
        }
        foreach (MemberReferenceHandle handle in metadata.MemberReferences)
        {
            MemberReference member = metadata.GetMemberReference(handle);
            if (member.Parent.Kind != HandleKind.TypeReference)
                continue;
            TypeReference type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            string owner = metadata.GetString(type.Namespace) + "." + metadata.GetString(type.Name);
            string name = metadata.GetString(member.Name);
            if (owner is "System.IO.File" or "System.IO.Directory" or "System.IO.FileStream"
                or "System.Diagnostics.Process" or "System.Net.Sockets.Socket"
                or "System.Threading.Thread" or "System.Reflection.Emit.AssemblyBuilder"
                || owner.StartsWith("Microsoft.Win32.Registry", StringComparison.Ordinal))
                failures.Add($"member '{owner}.{name}'");
        }
        foreach (MethodDefinitionHandle handle in metadata.MethodDefinitions)
        {
            MethodDefinition method = metadata.GetMethodDefinition(handle);
            if ((method.Attributes & System.Reflection.MethodAttributes.PinvokeImpl) != 0)
            {
                TypeDefinition owner = metadata.GetTypeDefinition(method.GetDeclaringType());
                failures.Add($"native import '{metadata.GetString(owner.Namespace)}.{metadata.GetString(owner.Name)}.{metadata.GetString(method.Name)}'");
            }
        }
        if (failures.Count != 0)
            throw new NotSupportedException($"Browser game '{Path.GetFileName(assemblyPath)}' requires desktop-only APIs: {string.Join("; ", failures.Distinct(StringComparer.Ordinal))}. Move platform behavior into the desktop host project.");
    }
}

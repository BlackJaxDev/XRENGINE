using System.Xml.Linq;
using NUnit.Framework;

namespace XREngine.UnitTests.Rendering;

/// <summary>Enforces dependency direction for explicitly composed native subsystem modules.</summary>
[TestFixture]
public sealed class NativeSubsystemDependencyBoundaryTests
{
    private static readonly string[] PortableProjects =
    [
        "XREngine.Data", "XREngine.Extensions", "XREngine.Audio", "XREngine.Input",
        "XREngine.Animation", "XREngine.Modeling", "XREngine.Fbx", "XREngine.Gltf",
        "XREngine.Runtime.Core", "XREngine.Runtime.Rendering",
        "XREngine.Runtime.AnimationIntegration", "XREngine.Runtime.AudioIntegration",
        "XREngine.Runtime.InputIntegration", "XREngine.Runtime.ModelingIntegration",
    ];

    private static readonly HashSet<string> NativeLeaves = new(StringComparer.Ordinal)
    {
        "XREngine.Runtime.Physics.Jolt", "XREngine.Runtime.Physics.PhysX",
        "XREngine.Runtime.Physics.Jitter", "XREngine.Runtime.Physics.Authoring",
        "XREngine.Runtime.ModelAssetPipeline",
        "XREngine.Audio.OpenAL", "XREngine.Audio.SteamAudio", "XREngine.Audio.NAudio",
        "XREngine.Audio.OVRLipSync",
        "XREngine.Audio.Audio2Face", "XREngine.Runtime.MeshProcessing.Meshoptimizer",
        "XREngine.Runtime.Media.FFmpeg", "XREngine.Runtime.Imaging.Magick",
        "XREngine.Runtime.Platform.Desktop", "XREngine.Input.Silk", "XREngine.Input.XInput",
        "XREngine.Runtime.XR.OpenVR", "XREngine.Runtime.XR.OpenXR",
        "XREngine.Runtime.Rendering.ImGui", "XREngine.Runtime.UI.Ultralight",
        "XREngine.Runtime.UI.Rive", "XREngine.Runtime.UI.Skia", "XREngine.Runtime.Text.FreeType",
        "XREngine.Runtime.IO.DirectStorage", "XREngine.Runtime.Net.Sockets", "XREngine.Runtime.Net.Osc",
        "XREngine.Runtime.Diagnostics.Desktop", "XREngine.Runtime.Rendering.OpenGL",
        "XREngine.Runtime.Rendering.Vulkan", "XREngine.Runtime.Rendering.WebGPU",
    };

    [Test]
    public void PortableProjects_DoNotReferenceNativeLeaves()
    {
        string root = FindRepositoryRoot();
        Assert.Multiple(() =>
        {
            foreach (string project in PortableProjects)
                foreach (string reference in ReadProjectReferences(root, project))
                    Assert.That(NativeLeaves, Does.Not.Contain(reference),
                        $"Portable project '{project}' references native leaf '{reference}'.");
        });
    }

    [Test]
    public void NativeLeaves_DoNotReferenceOtherNativeLeavesOrApplications()
    {
        string root = FindRepositoryRoot();
        Assert.Multiple(() =>
        {
            foreach (string project in NativeLeaves)
                foreach (string reference in ReadProjectReferences(root, project))
                {
                    Assert.That(NativeLeaves, Does.Not.Contain(reference),
                        $"Native leaf '{project}' references another leaf '{reference}'.");
                    Assert.That(reference, Is.Not.AnyOf("XREngine.Runtime.Bootstrap", "XREngine.Editor",
                        "XREngine.Server", "XREngine.VRClient", "XREngine.UnitTests"),
                        $"Native leaf '{project}' references application '{reference}'.");
                }
        });
    }

    [Test]
    public void DefaultComposition_DoesNotInstallExperimentalPhysicsOrCookOnlyAuthoring()
    {
        string root = FindRepositoryRoot();
        foreach (string project in new[] { "XREngine.Runtime.Bootstrap", "XREngine.Server", "XREngine.VRClient" })
        {
            string[] references = ReadProjectReferences(root, project);
            Assert.That(references, Does.Not.Contain("XREngine.Runtime.Physics.Jitter"));
            Assert.That(references, Does.Not.Contain("XREngine.Runtime.Physics.Authoring"));
        }
    }

    [Test]
    public void ApplicationReferences_UseOnlyTheirComposedModules()
    {
        string root = FindRepositoryRoot();
        HashSet<string> bootstrapModules = new(StringComparer.Ordinal)
        {
            "XREngine.Runtime.Physics.Jolt", "XREngine.Runtime.Physics.PhysX",
            "XREngine.Audio.OpenAL", "XREngine.Audio.SteamAudio", "XREngine.Audio.NAudio",
            "XREngine.Audio.OVRLipSync", "XREngine.Audio.Audio2Face",
            "XREngine.Input.XInput", "XREngine.Runtime.Platform.Desktop",
            "XREngine.Runtime.XR.OpenVR", "XREngine.Runtime.XR.OpenXR",
            "XREngine.Runtime.Imaging.Magick", "XREngine.Runtime.Media.FFmpeg",
            "XREngine.Runtime.Text.FreeType", "XREngine.Runtime.UI.Skia",
            "XREngine.Runtime.UI.Rive", "XREngine.Runtime.UI.Ultralight",
            "XREngine.Runtime.IO.DirectStorage", "XREngine.Runtime.Diagnostics.Desktop",
            "XREngine.Runtime.Net.Sockets", "XREngine.Runtime.Net.Osc",
            "XREngine.Runtime.MeshProcessing.Meshoptimizer",
            "XREngine.Runtime.Rendering.OpenGL", "XREngine.Runtime.Rendering.Vulkan",
        };
        Dictionary<string, HashSet<string>> installed = new(StringComparer.Ordinal)
        {
            ["XREngine.Runtime.Bootstrap"] = bootstrapModules,
            ["XREngine.Editor"] = new(bootstrapModules, StringComparer.Ordinal)
            {
                "XREngine.Runtime.Physics.Authoring", "XREngine.Runtime.Rendering.ImGui",
                "XREngine.Runtime.ModelAssetPipeline",
            },
            ["XREngine.Server"] = new(bootstrapModules, StringComparer.Ordinal),
            ["XREngine.VRClient"] = new(bootstrapModules, StringComparer.Ordinal),
            ["XREngine.RenderBench"] = new(StringComparer.Ordinal)
            {
                "XREngine.Runtime.Rendering.Vulkan", "XREngine.Runtime.Imaging.Magick",
                "XREngine.Runtime.MeshProcessing.Meshoptimizer",
            },
        };
        Assert.Multiple(() =>
        {
            foreach ((string application, HashSet<string> modules) in installed)
                foreach (string reference in ReadProjectReferences(root, application).Where(NativeLeaves.Contains))
                    Assert.That(modules, Does.Contain(reference),
                        $"Application '{application}' references uncomposed module '{reference}'.");
        });
    }

    [Test]
    public void PortablePublicContracts_DoNotExposeNativeModuleOrVendorTypes()
    {
        HashSet<string> nativeAssemblies = new(NativeLeaves, StringComparer.Ordinal)
        {
            "OpenVR.NET", "MagicPhysX", "JoltPhysicsSharp", "ImGui.NET", "FFmpeg.AutoGen",
            "SkiaSharp", "RiveSharp", "SharpFont", "UltralightNet", "Silk.NET.Windowing.Common",
            "Silk.NET.Input.Common", "Silk.NET.OpenXR", "Silk.NET.OpenAL", "Silk.NET.DirectStorage",
            "Magick.NET-Q16-AnyCPU", "Magick.NET.Core",
        };
        foreach (string project in PortableProjects)
        {
            System.Reflection.Assembly assembly = System.Reflection.Assembly.Load(project);
            foreach (Type type in assembly.GetExportedTypes())
            {
                AssertPortableType(type.BaseType, nativeAssemblies, type.FullName!);
                foreach (Type implemented in type.GetInterfaces())
                    AssertPortableType(implemented, nativeAssemblies, type.FullName!);
                const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static |
                    System.Reflection.BindingFlags.DeclaredOnly;
                foreach (System.Reflection.FieldInfo field in type.GetFields(flags))
                    AssertPortableType(field.FieldType, nativeAssemblies, $"{type.FullName}.{field.Name}");
                foreach (System.Reflection.PropertyInfo property in type.GetProperties(flags))
                    AssertPortableType(property.PropertyType, nativeAssemblies, $"{type.FullName}.{property.Name}");
                foreach (System.Reflection.EventInfo eventInfo in type.GetEvents(flags))
                    AssertPortableType(eventInfo.EventHandlerType, nativeAssemblies, $"{type.FullName}.{eventInfo.Name}");
                foreach (System.Reflection.MethodBase method in type.GetMethods(flags)
                    .Cast<System.Reflection.MethodBase>().Concat(type.GetConstructors(flags)))
                {
                    if (method is System.Reflection.MethodInfo methodInfo)
                        AssertPortableType(methodInfo.ReturnType, nativeAssemblies, $"{type.FullName}.{method.Name}");
                    foreach (System.Reflection.ParameterInfo parameter in method.GetParameters())
                        AssertPortableType(parameter.ParameterType, nativeAssemblies, $"{type.FullName}.{method.Name}");
                }
            }
        }
    }

    private static void AssertPortableType(Type? type, IReadOnlySet<string> nativeAssemblies, string member)
    {
        if (type is null || type.IsGenericParameter)
            return;
        if (type.HasElementType)
        {
            AssertPortableType(type.GetElementType(), nativeAssemblies, member);
            return;
        }
        string? assembly = type.Assembly.GetName().Name;
        Assert.That(assembly is null || !nativeAssemblies.Contains(assembly),
            $"Portable contract '{member}' exposes '{type}' from native module '{assembly}'.");
        if (type.IsGenericType)
            foreach (Type argument in type.GetGenericArguments())
                AssertPortableType(argument, nativeAssemblies, member);
    }

    private static string[] ReadProjectReferences(string root, string project)
        => XDocument.Load(Path.Combine(root, project, $"{project}.csproj"))
            .Descendants("ProjectReference")
            .Select(static element => (string?)element.Attribute("Include"))
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(static include => Path.GetFileNameWithoutExtension(include!))
            .ToArray();

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;
        throw new DirectoryNotFoundException("The native dependency checks require the repository workspace.");
    }
}

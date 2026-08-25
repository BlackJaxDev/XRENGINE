using System.Reflection;
using System.Xml.Linq;
using NUnit.Framework;
using Shouldly;
using XREngine.Components.Animation;
using XREngine.Data.Components;
using XREngine.Runtime.Bootstrap;

namespace XREngine.UnitTests.Rendering;

[TestFixture]
public sealed class RuntimeModularizationPhase5DependencyBoundaryTests
{
    private static readonly Dictionary<string, string[]> ApprovedAdapterReferences = new(StringComparer.Ordinal)
    {
        ["XREngine.Runtime.AnimationIntegration"] =
        [
            "OscCore",
            "XREngine.Animation",
            "XREngine.Data",
            "XREngine.Runtime.Core",
            "XREngine.Runtime.Rendering",
        ],
        ["XREngine.Runtime.AudioIntegration"] =
        [
            "XREngine.Audio",
            "XREngine.Data",
            "XREngine.Runtime.Core",
            "XREngine.Runtime.Rendering",
        ],
        ["XREngine.Runtime.InputIntegration"] =
        [
            "XREngine.Data",
            "XREngine.Extensions",
            "XREngine.Input",
            "XREngine.Runtime.Core",
            "XREngine.Runtime.Rendering",
        ],
        ["XREngine.Runtime.ModelingBridge"] =
        [
            "XREngine.Animation",
            "XREngine.Data",
            "XREngine.Fbx",
            "XREngine.Gltf",
            "XREngine.Modeling",
            "XREngine.Runtime.Rendering",
        ],
    };

    private static readonly HashSet<string> ForbiddenPublicApiAssemblies = new(StringComparer.Ordinal)
    {
        "XREngine",
        "XREngine.Editor",
        "XREngine.Server",
        "XREngine.VRClient",
        "XREngine.UnitTests",
        "XREngine.Runtime.Rendering.OpenGL",
        "XREngine.Runtime.Rendering.Vulkan",
    };

    [Test]
    public void AdapterProjects_ReferenceOnlyTheirApprovedLowerGraph()
    {
        string root = ResolveWorkspaceRoot();

        foreach ((string projectName, string[] approvedReferences) in ApprovedAdapterReferences)
        {
            string[] references = ReadProjectReferences(Path.Combine(root, projectName, $"{projectName}.csproj"));
            references.ShouldBe(approvedReferences);

            foreach (string otherAdapter in ApprovedAdapterReferences.Keys)
                if (!string.Equals(projectName, otherAdapter, StringComparison.Ordinal))
                    references.ShouldNotContain(otherAdapter);
        }
    }

    [Test]
    public void FeatureAndFormatLibraries_DoNotReferenceRuntimeOrApplications()
    {
        string root = ResolveWorkspaceRoot();
        string[] lowerProjects =
        [
            "XREngine.Animation",
            "XREngine.Audio",
            "XREngine.Input",
            "XREngine.Modeling",
            "XREngine.Fbx",
            "XREngine.Gltf",
        ];

        foreach (string projectName in lowerProjects)
        {
            string[] references = ReadProjectReferences(Path.Combine(root, projectName, $"{projectName}.csproj"));
            references.ShouldAllBe(reference =>
                !reference.StartsWith("XREngine.Runtime.", StringComparison.Ordinal) &&
                reference != "XREngine" && reference != "XREngine.Editor" && reference != "XREngine.Server" &&
                reference != "XREngine.VRClient" && reference != "XREngine.UnitTests");
        }
    }

    [Test]
    public void AdapterPublicApis_DoNotExposeFacadeApplicationsBackendsOrOtherAdapters()
    {
        foreach (string adapterName in ApprovedAdapterReferences.Keys)
        {
            Assembly adapter = Assembly.Load(adapterName);
            HashSet<string> forbidden = new(ForbiddenPublicApiAssemblies, StringComparer.Ordinal);
            foreach (string otherAdapter in ApprovedAdapterReferences.Keys)
                if (!string.Equals(adapterName, otherAdapter, StringComparison.Ordinal))
                    forbidden.Add(otherAdapter);

            foreach (Type exportedType in adapter.GetExportedTypes())
            {
                AssertTypeBoundary(exportedType.BaseType, adapterName, forbidden, exportedType.FullName!);
                foreach (Type interfaceType in exportedType.GetInterfaces())
                    AssertTypeBoundary(interfaceType, adapterName, forbidden, exportedType.FullName!);

                const BindingFlags publicDeclared = BindingFlags.Public | BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (FieldInfo field in exportedType.GetFields(publicDeclared))
                    AssertTypeBoundary(field.FieldType, adapterName, forbidden, $"{exportedType.FullName}.{field.Name}");
                foreach (PropertyInfo property in exportedType.GetProperties(publicDeclared))
                    AssertTypeBoundary(property.PropertyType, adapterName, forbidden, $"{exportedType.FullName}.{property.Name}");
                foreach (EventInfo eventInfo in exportedType.GetEvents(publicDeclared))
                    AssertTypeBoundary(eventInfo.EventHandlerType, adapterName, forbidden, $"{exportedType.FullName}.{eventInfo.Name}");
                foreach (MethodBase method in exportedType.GetMethods(publicDeclared).Cast<MethodBase>()
                             .Concat(exportedType.GetConstructors(publicDeclared)))
                {
                    if (method is MethodInfo methodInfo)
                        AssertTypeBoundary(methodInfo.ReturnType, adapterName, forbidden, $"{exportedType.FullName}.{method.Name}");
                    foreach (ParameterInfo parameter in method.GetParameters())
                        AssertTypeBoundary(parameter.ParameterType, adapterName, forbidden, $"{exportedType.FullName}.{method.Name}");
                }
            }
        }
    }

    [Test]
    public void SharedArKitContract_BelongsToDataAndAudioDoesNotReferenceAnimationAdapter()
    {
        typeof(ARKitBlendshapeNames).Assembly.GetName().Name.ShouldBe("XREngine.Data");

        string root = ResolveWorkspaceRoot();
        string audioProject = File.ReadAllText(Path.Combine(
            root,
            "XREngine.Runtime.AudioIntegration",
            "XREngine.Runtime.AudioIntegration.csproj"));
        audioProject.ShouldNotContain("XREngine.Runtime.AnimationIntegration");
    }

    [Test]
    public void BootstrapOwnsHostCompositionAndAllAdapterAotRoots()
    {
        string root = ResolveWorkspaceRoot();
        string bootstrapRoot = Path.Combine(root, "XREngine.Runtime.Bootstrap");
        string hostRoot = Path.Combine(bootstrapRoot, "SubsystemHost");
        string bootstrapProject = File.ReadAllText(Path.Combine(bootstrapRoot, "XREngine.Runtime.Bootstrap.csproj"));
        string facadeProject = File.ReadAllText(Path.Combine(root, "XRENGINE", "XREngine.csproj"));
        string adapterBootstrap = File.ReadAllText(Path.Combine(hostRoot, "RuntimeAdapterBootstrap.cs"));

        string[] hostFiles =
        [
            "Engine.RuntimeAnimationHostServices.cs",
            "Engine.RuntimeAudioIntegrationServices.cs",
            "Engine.RuntimeInputServices.cs",
            "Engine.RuntimeModelImportServices.cs",
            "Engine.RuntimeVrInputServices.cs",
            "Engine.RuntimeVrLifecycleServices.cs",
            "Engine.RuntimeVrStateServices.cs",
        ];
        foreach (string hostFile in hostFiles)
        {
            File.Exists(Path.Combine(hostRoot, hostFile)).ShouldBeTrue();
            File.Exists(Path.Combine(root, "XRENGINE", "Engine", hostFile)).ShouldBeFalse();
        }

        adapterBootstrap.ShouldContain("RuntimeAdapterProfile profile");
        adapterBootstrap.ShouldContain("UninstallEngineHostServices()");
        adapterBootstrap.ShouldContain("DisposeWithoutLock()");
        foreach (string adapterName in ApprovedAdapterReferences.Keys)
            bootstrapProject.ShouldContain($"..\\{adapterName}\\**\\*.cs");
        facadeProject.ShouldNotContain("GenerateAotFactoryRegistrations");
    }

    [Test]
    public void FeatureNativeCargo_IsOwnedBelowTheFacade()
    {
        string root = ResolveWorkspaceRoot();
        string facadeProject = File.ReadAllText(Path.Combine(root, "XRENGINE", "XREngine.csproj"));
        string audioAdapterProject = File.ReadAllText(Path.Combine(
            root, "XREngine.Runtime.AudioIntegration", "XREngine.Runtime.AudioIntegration.csproj"));
        string inputProject = File.ReadAllText(Path.Combine(root, "XREngine.Input", "XREngine.Input.csproj"));
        string audioProject = File.ReadAllText(Path.Combine(root, "XREngine.Audio", "XREngine.Audio.csproj"));
        string renderingProject = File.ReadAllText(Path.Combine(
            root, "XREngine.Runtime.Rendering", "XREngine.Runtime.Rendering.csproj"));

        facadeProject.ShouldNotContain("OVRLipSync.dll");
        facadeProject.ShouldNotContain("openvr_api.dll");
        facadeProject.ShouldNotContain("Silk.NET.OpenAL.Soft.Native");
        facadeProject.ShouldNotContain("SharpFont.Dependencies");
        audioAdapterProject.ShouldContain("OVRLipSync.dll");
        inputProject.ShouldContain("openvr_api.dll");
        inputProject.ShouldContain("ActionManifest.json");
        audioProject.ShouldContain("Silk.NET.OpenAL.Soft.Native");
        renderingProject.ShouldContain("SharpFont.Dependencies");
        renderingProject.ShouldContain("ExcludeAssets=\"build\"");
    }

    [Test]
    public void AudioListenerWorldAttachment_IsOwnedByAudioIntegration()
    {
        string root = ResolveWorkspaceRoot();
        string worldSource = File.ReadAllText(Path.Combine(root, "XRENGINE", "Rendering", "XRWorldInstance.cs"));
        string registrySource = File.ReadAllText(Path.Combine(
            root,
            "XREngine.Runtime.AudioIntegration",
            "RuntimeAudioListenerWorldRegistry.cs"));

        worldSource.ShouldNotContain("IRuntimeAudioListenerWorld");
        worldSource.ShouldNotContain("ListenerContext");
        worldSource.ShouldNotContain("ApplyAudioSettings");
        registrySource.ShouldContain("ConditionalWeakTable<object, ListenerAttachment>");
        registrySource.ShouldContain("GetListenerCount");
    }

    [Test]
    [NonParallelizable]
    public void AdapterBootstrapLease_RestoresInstalledCapabilities()
    {
        IRuntimeAnimationHostServices previousAnimation = RuntimeAnimationHostServices.Current;
        IRuntimeAudioIntegrationServices previousAudio = RuntimeAudioIntegrationServices.Current;
        IRuntimeModelImportServices previousModeling = RuntimeModelImportServices.Current;

        IDisposable lease = RuntimeAdapterBootstrap.InstallEngineHostServices(
            RuntimeAdapterProfile.Animation | RuntimeAdapterProfile.Audio | RuntimeAdapterProfile.Modeling);
        try
        {
            RuntimeAnimationHostServices.Current.GetType().Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Bootstrap");
            RuntimeAudioIntegrationServices.Current.GetType().Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Bootstrap");
            RuntimeModelImportServices.Current.GetType().Assembly.GetName().Name.ShouldBe("XREngine.Runtime.Bootstrap");
        }
        finally
        {
            lease.Dispose();
        }

        RuntimeAnimationHostServices.Current.ShouldBeSameAs(previousAnimation);
        RuntimeAudioIntegrationServices.Current.ShouldBeSameAs(previousAudio);
        RuntimeModelImportServices.Current.ShouldBeSameAs(previousModeling);
    }

    private static void AssertTypeBoundary(
        Type? type,
        string adapterName,
        IReadOnlySet<string> forbiddenAssemblies,
        string apiMember)
    {
        if (type is null || type.IsGenericParameter)
            return;

        if (type.HasElementType)
        {
            AssertTypeBoundary(type.GetElementType(), adapterName, forbiddenAssemblies, apiMember);
            return;
        }

        string? assemblyName = type.Assembly.GetName().Name;
        Assert.That(
            assemblyName is null || !forbiddenAssemblies.Contains(assemblyName),
            $"{adapterName} public API '{apiMember}' exposes forbidden type '{type}' from '{assemblyName}'.");

        if (type.IsGenericType)
            foreach (Type argument in type.GetGenericArguments())
                AssertTypeBoundary(argument, adapterName, forbiddenAssemblies, apiMember);
    }

    private static string[] ReadProjectReferences(string projectPath)
        => XDocument.Load(projectPath)
            .Descendants("ProjectReference")
            .Select(element => (string?)element.Attribute("Include"))
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(static include => Path.GetFileNameWithoutExtension(include!))
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static string ResolveWorkspaceRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XRENGINE.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"Could not find workspace root from '{AppContext.BaseDirectory}'.");
    }
}

using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using RollingBall;
using XREngine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Execution;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.Diagnostics.Native;
using XREngine.Runtime.Platform.Desktop;
using XREngine.Scene.Physics;

NativeDebugBackendRegistration.EnsureRegistered();
if (args.Length is not (3 or 4))
    throw new ArgumentException("Usage: RollingBallPublisher <canonical-project.xrproj> <editor.dll> <validation-root> [rollingball|rendering-parity|ui-parity]");

string fixture = args.Length == 4 ? args[3] : "rollingball";
string worldName = fixture switch
{
    "rollingball" => "RollingBallWorld",
    "rendering-parity" => "RenderingParityWorld",
    "ui-parity" => "BrowserUiParityWorld",
    _ => throw new ArgumentException($"Unsupported publisher fixture: {fixture}"),
};

string canonicalProjectFile = Path.GetFullPath(args[0]);
string editorDll = Path.GetFullPath(args[1]);
string validationRoot = Path.GetFullPath(args[2]);
if (!File.Exists(canonicalProjectFile) || !File.Exists(editorDll))
    throw new FileNotFoundException("The canonical project and compiled Editor DLL are required.");

CanonicalProjectSnapshot snapshot = CanonicalProjectSnapshot.Copy(canonicalProjectFile,
    Path.Combine(validationRoot, "project"));
try
{
    ThreadedWorkerBackend.EnsureRegistered();
    AssetFileSystemServices.Current = new LocalAssetFileSystem();
    HostAssetFileOutputServices.Current = new DesktopHostAssetFileOutput();
    ShaderSourceFileBackendServices.Current = new LocalShaderSourceFileBackend();
    RuntimePlatformPaths.Current = new LocalPlatformPaths(validationRoot);
    using IDisposable services = RuntimeAssetBootstrap.InstallEngineAssetServices();
    // Saved sky materials and pipelines need managed shader access during loading.
    // No native renderer or physics backend is needed to cook the object graph.
    using RendererBackendCatalog renderers = new();
    using IDisposable rendering = RuntimeCallerThreadRenderingBootstrap.Install(
        renderers, new PhysicsBackendCatalog(), static _ => new DefaultRenderPipeline());
    if (fixture == "rollingball")
        RollingBallRuntimeRegistration.Register();

    string projectFile = snapshot.ProjectFile;
    XRProject project = AssetManager.Deserializer.Deserialize<XRProject>(File.ReadAllText(projectFile));
    project.FilePath = projectFile;
    if (!Engine.LoadProject(project))
        throw new InvalidOperationException($"Canonical {fixture} project load failed.");
    string startupFile = Path.Combine(project.AssetsDirectory!, "startup.asset");
    Engine.GameSettings = AssetManager.Deserializer.Deserialize<GameStartupSettings>(File.ReadAllText(startupFile));

    BuildSettings settings = new()
    {
        Configuration = EBuildConfiguration.Release,
        Platform = EBuildPlatform.BrowserWebGPU,
        OutputSubfolder = "browser-game",
        CleanOutputDirectory = true,
        CookContent = true,
        BuildManagedAssemblies = true,
        BuildLauncherExecutable = true,
        PublishLauncherAsNativeAot = false,
        ValidateLauncherAotCompatibility = false,
        LauncherDefineConstants = string.Empty,
        SaveSettingsBeforeBuild = false,
    };
    string output = Path.Combine(project.BuildDirectory!, settings.OutputSubfolder);
    Console.WriteLine($"PRODUCTION_PUBLISH_START output={output}");
    Assembly editor = Assembly.LoadFrom(editorDll);
    Type builder = editor.GetType("XREngine.Editor.ProjectBuilder")
        ?? throw new MissingMemberException("Production Editor ProjectBuilder type is missing.");
    MethodInfo build = builder.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
        .Single(method => method.Name == "BuildCurrentProjectSynchronously" &&
            method.GetParameters().Length == 2 && method.GetParameters()[0].ParameterType == typeof(BuildSettings));
    Action<JobProgress> progress = item => Console.WriteLine($"PRODUCTION_STEP_COMPLETE {item.Value:P0} {item.Payload}");
    try
    {
        build.Invoke(null, [settings, progress]);
    }
    catch (TargetInvocationException error) when (error.InnerException is not null)
    {
        ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        throw;
    }

    string descriptorFile = Path.Combine(output, "browser-publish.json");
    string manifestFile = Path.Combine(output, "content", "manifest.json");
    if (!File.Exists(Path.Combine(output, "index.html")) || !File.Exists(descriptorFile) || !File.Exists(manifestFile))
        throw new InvalidDataException("The Editor did not activate a complete browser game bundle.");
    using JsonDocument descriptor = JsonDocument.Parse(File.ReadAllBytes(descriptorFile));
    JsonElement root = descriptor.RootElement;
    if (root.GetProperty("schema").GetInt32() != 2 ||
        root.GetProperty("format").GetString() != "xrengine-engine-launch" ||
        root.GetProperty("manifest").GetString() != "./content/manifest.json")
        throw new InvalidDataException("The Editor activated an unsupported browser game descriptor.");
    using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(manifestFile));
    JsonElement catalog = manifest.RootElement;
    if (catalog.GetProperty("startupWorld").GetString() != $"/game/Worlds/{worldName}.asset")
        throw new InvalidDataException($"The published startup world is not the canonical {fixture} world.");
    int assets = catalog.GetProperty("assets").GetArrayLength();
    int shaders = catalog.GetProperty("shaderArtifacts").GetArrayLength();
    int passes = catalog.GetProperty("pipelineArtifacts").GetArrayLength();
    if (assets < 32 || shaders < 15 || passes < 9)
        throw new InvalidDataException($"The published game content is incomplete: {assets} assets, {shaders} shaders, {passes} pipeline passes.");
    if (fixture == "rendering-parity")
    {
        bool sky = catalog.GetProperty("materialVariants").EnumerateArray().Any(entry =>
            entry.GetProperty("semantic").GetString() == "SkyboxEquirectangular" &&
            entry.GetProperty("semanticVersion").GetInt32() == 1 &&
            entry.GetProperty("pass").GetString() == "background" &&
            entry.GetProperty("vertexProfile").GetString() == "fullscreen-sky-v1" &&
            entry.GetProperty("outputProfile").GetString() == "linear-hdr-v1");
        bool mapped = catalog.GetProperty("materialVariants").EnumerateArray().Any(entry =>
            entry.GetProperty("semantic").GetString() == "StandardLitTexture" &&
            entry.GetProperty("pass").GetString() == "opaque-forward" &&
            entry.GetProperty("vertexProfile").GetString() == "position-normal-tangent-uv-v1");
        bool skinning = catalog.GetProperty("computeArtifacts").EnumerateArray().Any(entry =>
            entry.GetProperty("kernel").GetString() == "packed-skinning");
        if (!sky || !mapped || !skinning)
            throw new InvalidDataException("The RenderingParity bundle requires authored sky, mapped surface, and packed deformation artifacts.");
    }

    if (fixture == "ui-parity")
    {
        foreach ((string semantic, int version) in new[] { ("UIQuadBatched", 2), ("UIQuadBatchedTexture", 2),
            ("UITextBatchedBitmap", 2), ("UICanvasSurface", 1) })
            if (!catalog.GetProperty("materialVariants").EnumerateArray().Any(entry =>
                entry.GetProperty("semantic").GetString() == semantic &&
                entry.GetProperty("semanticVersion").GetInt32() == version))
                throw new InvalidDataException($"The UI bundle requires {semantic} v{version}.");
        if (!catalog.GetProperty("assets").EnumerateArray().Any(entry =>
            entry.GetProperty("path").GetString() == "/engine/Fonts/Roboto/Roboto-Regular.cooked.asset"))
            throw new InvalidDataException("The UI bundle requires its authored bitmap font.");
    }

    Console.WriteLine($"PRODUCTION_PUBLISH_PASS output={output} assets={assets} shaders={shaders} pipelinePasses={passes}");
}
finally
{
    snapshot.VerifyUnchanged();
}

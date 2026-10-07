using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using RollingBall;
using XREngine;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;

if (args.Length != 3)
    throw new ArgumentException("Usage: RollingBallPublisher <canonical-project.xrproj> <editor.dll> <validation-root>");

string canonicalProjectFile = Path.GetFullPath(args[0]);
string editorDll = Path.GetFullPath(args[1]);
string validationRoot = Path.GetFullPath(args[2]);
if (!File.Exists(canonicalProjectFile) || !File.Exists(editorDll))
    throw new FileNotFoundException("The canonical project and compiled Editor DLL are required.");

CanonicalProjectSnapshot snapshot = CanonicalProjectSnapshot.Copy(canonicalProjectFile,
    Path.Combine(validationRoot, "project"));
try
{
    using IDisposable services = RuntimeAssetBootstrap.InstallEngineAssetServices();
    AssetFileSystemServices.Current = new LocalAssetFileSystem();
    ShaderSourceFileBackendServices.Current = new LocalShaderSourceFileBackend();
    RuntimePlatformPaths.Current = new LocalPlatformPaths(validationRoot);
    RollingBallRuntimeRegistration.Register();

    string projectFile = snapshot.ProjectFile;
    XRProject project = AssetManager.Deserializer.Deserialize<XRProject>(File.ReadAllText(projectFile));
    project.FilePath = projectFile;
    if (!Engine.LoadProject(project))
        throw new InvalidOperationException("Canonical RollingBall project load failed.");
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
    if (catalog.GetProperty("startupWorld").GetString() != "/game/Worlds/RollingBallWorld.asset")
        throw new InvalidDataException("The published startup world is not the canonical RollingBall world.");
    int assets = catalog.GetProperty("assets").GetArrayLength();
    int shaders = catalog.GetProperty("shaderArtifacts").GetArrayLength();
    int passes = catalog.GetProperty("pipelineArtifacts").GetArrayLength();
    if (assets < 32 || shaders < 15 || passes < 9)
        throw new InvalidDataException($"The published game content is incomplete: {assets} assets, {shaders} shaders, {passes} pipeline passes.");

    Console.WriteLine($"PRODUCTION_PUBLISH_PASS output={output} assets={assets} shaders={shaders} pipelinePasses={passes}");
}
finally
{
    snapshot.VerifyUnchanged();
}

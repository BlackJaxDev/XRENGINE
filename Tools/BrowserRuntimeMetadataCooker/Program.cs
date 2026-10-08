using MemoryPack;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using System.Reflection;
using XREngine;
using XREngine.Core.Files;
using XREngine.Execution;
using XREngine.Publishing;
using XREngine.Rendering;
using XREngine.Runtime.Bootstrap;
using XREngine.Runtime.Diagnostics.Native;
using XREngine.Scene;

NativeDebugBackendRegistration.EnsureRegistered();
ThreadedWorkerBackend.EnsureRegistered();
if (args.Length == 3 && args[0] == "--scan")
{
    string binaryDirectory = Path.GetFullPath(args[1]);
    string frameworkDirectory = Path.Combine(Path.GetFullPath(args[2]), "_framework");
    HashSet<string> shipped = [.. Directory.EnumerateFiles(frameworkDirectory, "*.wasm")
        .Select(static path => Path.GetFileNameWithoutExtension(path))
        .Select(static stem => stem[..stem.LastIndexOf('.')])];
    using BrowserMetadataLoadContext browserContext = new(binaryDirectory, shipped);
    Assembly[] closure = [.. shipped.Where(static name => name.StartsWith("XREngine.", StringComparison.Ordinal))
        .OrderBy(static name => name, StringComparer.Ordinal)
        .Select(browserContext.LoadPublishedAssembly)];
    using IDisposable browserRegistrations = RuntimeAssetBootstrap.InstallEngineAssetServices();
    AotRuntimeMetadata browserMetadata = AotRuntimeMetadataBuilder.BuildBrowser(
        closure, Assembly.GetExecutingAssembly(), [typeof(FontGlyphSet)]);
    Console.WriteLine($"Scanned browser closure: {closure.Length} assemblies, " +
        $"{browserMetadata.KnownTypeAssemblyQualifiedNames.Length} types, " +
        $"{browserMetadata.TransformTypes.Length} transforms, " +
        $"{browserMetadata.TypeRedirects.Length} redirects, " +
        $"{browserMetadata.WorldObjectReplications.Length} replication entries, " +
        $"{browserMetadata.PublishedRuntimeAssetTypeNames.Length} registered asset types.");
    return;
}
if (args.Length == 2 && args[0] == "--verify")
{
    string fixtureDirectory = Path.GetFullPath(args[1]);
    byte[] bytes = File.ReadAllBytes(Path.Combine(fixtureDirectory, AotRuntimeMetadataStore.MetadataFileName));
    string fingerprint = Convert.ToHexStringLower(SHA256.HashData(bytes));
    AotRuntimeMetadataStore.InstallVerifiedBrowserMetadata(bytes, fingerprint);
    XRRuntimeEnvironment.ConfigureBuildKind(EXRRuntimeBuildKind.Published);
    RuntimeHelpers.RunClassConstructor(typeof(XRWorldObjectBase).TypeHandle);
    using IDisposable assetRegistrations = RuntimeAssetBootstrap.InstallEngineAssetServices();
    byte[] worldBytes = File.ReadAllBytes(Path.Combine(fixtureDirectory, "EngineSmokeWorld.bin"));
    XRWorld verifiedWorld = CookedAssetReader.LoadAsset(worldBytes, typeof(XRWorld)) as XRWorld
        ?? throw new InvalidDataException("Published browser smoke world did not hydrate to XRWorld.");
    Console.WriteLine($"Verified published browser smoke world '{verifiedWorld.Name}' and metadata {fingerprint}.");
    return;
}
if (args.Length != 2)
    throw new ArgumentException("Usage: BrowserRuntimeMetadataCooker <world-yaml> <output-directory> | --verify <fixture-directory> | --scan <browser-binaries> <site-directory>");

string worldSource = Path.GetFullPath(args[0]);
string outputDirectory = Path.GetFullPath(args[1]);
Directory.CreateDirectory(outputDirectory);

using IDisposable registrations = RuntimeAssetBootstrap.InstallEngineAssetServices();
using StringReader reader = new(File.ReadAllText(worldSource));
XRWorld world = AssetManager.Deserializer.Deserialize<XRWorld>(reader)
    ?? throw new InvalidDataException("Browser fixture world did not deserialize to XRWorld.");
byte[] worldPayload = CookedBinarySerializer.ExecuteWithMemoryPackSuppressed(
    () => CookedBinarySerializer.Serialize(world));
CookedAssetBlob worldBlob = new(typeof(XRWorld).AssemblyQualifiedName!, CookedAssetFormat.BinaryV2, worldPayload);
File.WriteAllBytes(Path.Combine(outputDirectory, "EngineSmokeWorld.bin"), CookedAssetEnvelope.Serialize(worldBlob));

AotRuntimeMetadata metadata = AotRuntimeMetadataBuilder.Build(
    [typeof(AotRuntimeMetadata).Assembly, typeof(XRWorld).Assembly,
        typeof(XRMesh).Assembly, typeof(RuntimeAssetBootstrap).Assembly], [typeof(FontGlyphSet)],
    restrictPublishedAssetTypesToSelectedAssemblies: true);
File.WriteAllBytes(Path.Combine(outputDirectory, AotRuntimeMetadataStore.MetadataFileName),
    MemoryPackSerializer.Serialize(metadata));
Console.WriteLine($"Cooked browser smoke world and {metadata.KnownTypeAssemblyQualifiedNames.Length} published types.");

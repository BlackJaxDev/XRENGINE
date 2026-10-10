import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";

// Stage one native package for the genuine Editor browser publisher. The
// publisher remains responsible for native identity and dependency admission.
const [nativeArgument, shadersArgument, projectArgument, ...extra] = process.argv.slice(2);
if (!nativeArgument || !shadersArgument || !projectArgument || extra.length !== 0)
    throw new Error("Usage: node prepare-network-kinematic-project.mjs <native-package> <cooked-shaders> <new-project>");

const nativeRoot = path.resolve(nativeArgument);
const shaderRoot = path.resolve(shadersArgument);
const projectRoot = path.resolve(projectArgument);
if (fs.existsSync(projectRoot))
    throw new Error("The network fixture project must start empty.");
if (fs.readdirSync(nativeRoot).sort().join("\n") !== "World.asset\nworld-package.json")
    throw new Error("The native fixture package must contain only its declared world and manifest.");
function readBoundedRegularFile(file, limit) {
    const stat = fs.lstatSync(file);
    if (!stat.isFile() || stat.size < 1 || stat.size > limit)
        throw new Error("A native fixture input is not a bounded regular file.");
    return fs.readFileSync(file);
}
const manifestBytes = readBoundedRegularFile(path.join(nativeRoot, "world-package.json"), 1024 * 1024);
const native = JSON.parse(manifestBytes);
if (native.worldEntryPoint !== "World.asset" || native.gameBootstrapId !== "world-v1"
    || native.files?.length !== 1 || native.files[0].relativePath !== "World.asset")
    throw new Error("The network fixture requires one self-contained World.asset native package.");
const worldBytes = readBoundedRegularFile(path.join(nativeRoot, "World.asset"), 4 * 1024 * 1024);
const worldHash = crypto.createHash("sha256").update(worldBytes).digest("hex");
if (worldBytes.length !== native.files[0].length || worldHash !== native.files[0].sha256?.toLowerCase())
    throw new Error("The native world bytes do not match their declared package entry.");
if (!fs.statSync(path.join(shaderRoot, "manifest.json")).isFile())
    throw new Error("The canonical cooked shader manifest is missing.");

fs.mkdirSync(path.join(projectRoot, "Assets", "Shaders"), { recursive: true });
fs.mkdirSync(path.join(projectRoot, "Native"));
fs.mkdirSync(path.join(projectRoot, "Config"));
fs.writeFileSync(path.join(projectRoot, "Native", "world-package.json"), manifestBytes);
fs.writeFileSync(path.join(projectRoot, "Native", "World.asset"), worldBytes);
fs.writeFileSync(path.join(projectRoot, "Assets", "World.asset"), worldBytes);
fs.cpSync(shaderRoot, path.join(projectRoot, "Assets", "Shaders", "WebGPU"), {
    recursive: true, errorOnExist: true,
    filter: source => {
        if (fs.lstatSync(source).isSymbolicLink())
            throw new Error("Cooked shader inputs must not contain symbolic links.");
        return true;
    },
});
fs.writeFileSync(path.join(projectRoot, "NetworkKinematic.xrproj"), `__type: XREngine.XRProject
ProjectName: NetworkKinematic
ProjectVersion: 1.0.0
EngineVersion: 1.0.0
ID: 67aa1f8f-fc77-4b4b-8fdf-14d3ec3f77de
Name: Network Kinematic
StartupScenePath: World.asset
BrowserShaderArtifactManifestPath: Assets/Shaders/WebGPU/manifest.json
BrowserSharedWorldPackageManifestPath: Native/world-package.json
`);
fs.writeFileSync(path.join(projectRoot, "Assets", "startup.asset"), `__type: XREngine.GameStartupSettings
Name: Network Kinematic Startup
ID: 1b5a7d73-e633-4a36-9275-8f02c2cf5f54
LogOutputToFile: true
NetworkingType: Local
TargetUpdatesPerSecond: 60
TargetFramesPerSecond: 60
FixedFramesPerSecond: 60
RunVRInPlace: false
DefaultUserSettings:
  VSync: Off
  GlobalIlluminationMode: None
  AntiAliasingModeOverride:
    HasOverride: true
    Value: None
  GPURenderDispatchOverride:
    HasOverride: true
    Value: false
BuildSettings:
  Configuration: Release
  Platform: BrowserWebGPU
  OutputSubfolder: BrowserWebGPU
  CleanOutputDirectory: true
  CookContent: true
  BuildManagedAssemblies: true
`);
console.log(JSON.stringify({ staged: true, worldSha256: worldHash, worldBytes: worldBytes.length }));

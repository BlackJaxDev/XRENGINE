import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";
import { validateSharedWorldPackage } from "../../XREngine.Browser/wwwroot/engine-world-package.js";

const [nativeArgument, siteArgument, ...extra] = process.argv.slice(2);
if (!nativeArgument || !siteArgument || extra.length !== 0)
    throw new Error("Usage: node verify-network-kinematic-publication.mjs <native-package|--published-only> <published-site>");
const nativeRoot = nativeArgument === "--published-only" ? null : fs.realpathSync(nativeArgument);
const contentRoot = fs.realpathSync(path.join(siteArgument, "content"));
function read(root, relative, limit) {
    const file = path.join(root, relative);
    const stat = fs.lstatSync(file);
    if (!stat.isFile() || stat.size < 1 || stat.size > limit || fs.realpathSync(file) !== file)
        throw new Error("A package input is not a bounded, unlinked regular file.");
    return fs.readFileSync(file);
}
const native = nativeRoot === null ? null : JSON.parse(read(nativeRoot, "world-package.json", 1024 * 1024));
const shared = JSON.parse(read(contentRoot, "world-package.json", 1024 * 1024));
const catalog = JSON.parse(read(contentRoot, "manifest.json", 1024 * 1024));
// Reuse the shipping browser's canonical length-prefixed hash and profile
// validation. Expanded packages intentionally have a new admission hash.
const files = await validateSharedWorldPackage(shared);
if (shared.worldEntryPoint !== "World.asset" || catalog.worldPackage !== "world-package.json"
    || catalog.startupWorld !== "/game/World.asset" || shared.metadata.browserStartupWorld !== catalog.startupWorld)
    throw new Error("The expanded package has an unexpected world binding.");
const worldBytes = read(contentRoot, shared.worldEntryPoint, 4 * 1024 * 1024);
const worldHash = crypto.createHash("sha256").update(worldBytes).digest("hex");
if (native !== null) {
    for (const field of ["schemaVersion", "packageId", "worldEntryPoint", "gameBootstrapId", "buildVersion"])
        if (native[field] !== shared[field]) throw new Error(`Native package field changed: ${field}`);
    for (const field of ["worldId", "revisionId", "assetSchemaVersion", "requiredBuildVersion"])
        if (native.asset?.[field] !== shared.asset[field]) throw new Error(`Native world field changed: ${field}`);
    for (const [key, value] of Object.entries(native.metadata ?? {}))
        if (shared.metadata[key] !== value) throw new Error("Native package metadata changed.");
    const ordered = value => Object.entries(value ?? {}).sort(([a], [b]) => a < b ? -1 : a > b ? 1 : 0);
    if (JSON.stringify(ordered(native.asset?.metadata)) !== JSON.stringify(ordered(shared.asset.metadata)))
        throw new Error("Native world metadata changed.");
    if (native.files?.length !== 1 || native.files[0].relativePath !== native.worldEntryPoint
        || shared.asset.contentHash === native.asset.contentHash)
        throw new Error("The expanded package did not preserve its native entry and new admission identity.");
    const nativeBytes = read(nativeRoot, native.worldEntryPoint, 4 * 1024 * 1024);
    if (!nativeBytes.equals(worldBytes) || native.files[0].length !== nativeBytes.length
        || native.files[0].sha256.toLowerCase() !== worldHash)
        throw new Error("The retained world differs from the verified native input bytes.");
}
for (const [relative, entry] of files) {
    const bytes = read(contentRoot, relative, 4 * 1024 * 1024);
    if (bytes.length !== entry.length || crypto.createHash("sha256").update(bytes).digest("hex") !== entry.sha256)
        throw new Error(`Published package file mismatch: ${relative}`);
}
const pending = [""];
let actualFiles = 0;
while (pending.length) {
    const directory = pending.pop();
    for (const entry of fs.readdirSync(path.join(contentRoot, directory), { withFileTypes: true })) {
        const relative = directory ? `${directory}/${entry.name}` : entry.name;
        if (entry.isSymbolicLink()) throw new Error("Published content contains a link.");
        if (entry.isDirectory()) {
            if (relative !== "payload") throw new Error("Published content contains an unexpected directory.");
            pending.push(relative);
        } else if (!entry.isFile() || (relative !== "world-package.json" && !files.has(relative))
            || ++actualFiles > files.size + 1) {
            throw new Error("Published content contains an undeclared file.");
        }
    }
}
if (actualFiles !== files.size + 1) throw new Error("Published content inventory is incomplete.");
console.log(JSON.stringify({ verified: true, nativeInputCompared: native !== null, worldSha256: worldHash, worldBytes: worldBytes.length,
    packageHash: shared.asset.contentHash, declaredFiles: files.size, totalBytes: shared.totalBytes }));

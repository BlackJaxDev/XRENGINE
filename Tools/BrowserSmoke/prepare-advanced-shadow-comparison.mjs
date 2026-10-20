import fs from "node:fs";
import path from "node:path";
import crypto from "node:crypto";

const worldHash = text => crypto.createHash("sha256").update(text).digest("hex");
const expectedWorldHashes = {
    on: "8f537639aa8260d935fda46c82a3ca4d4a983efbb37bf9c00fb17f8738e96f9b",
    off: "b3dee24dfce5d5515c69cef8df62738547ae128aba74a673a1ad058f6e712484",
};

function assertValidationDirectoryChain(repo, target) {
    const validationRoot = path.join(repo, "Build", "_AgentValidation");
    const relative = path.relative(validationRoot, target);
    if (!relative || relative === ".." || relative.startsWith(`..${path.sep}`) || path.isAbsolute(relative))
        throw new Error("The shadow comparison path must be below Build/_AgentValidation.");
    const repoReal = fs.realpathSync.native(repo);
    let current = repo;
    let expected = repoReal;
    const segments = path.relative(repo, target).split(path.sep);
    for (const segment of segments) {
        current = path.join(current, segment);
        expected = path.join(expected, segment);
        let stat;
        try {
            stat = fs.lstatSync(current);
        } catch (error) {
            if (error.code === "ENOENT") continue;
            throw error;
        }
        if (stat.isSymbolicLink() || !stat.isDirectory())
            throw new Error(`The shadow comparison path has a linked or non-directory parent: ${current}`);
        const real = fs.realpathSync.native(current);
        const equal = process.platform === "win32"
            ? real.toLowerCase() === expected.toLowerCase() : real === expected;
        if (!equal)
            throw new Error(`The shadow comparison path redirects outside its expected directory: ${current}`);
    }
}

function sharedSourceSnapshot(project) {
    const files = [];
    function addFile(file, relative) {
        if (!fs.lstatSync(file).isFile())
            throw new Error(`The staged source must be a regular file: ${relative}`);
        files.push({ path: relative, sha256: worldHash(fs.readFileSync(file)) });
    }
    function collect(directory, relative) {
        if (!fs.lstatSync(directory).isDirectory())
            throw new Error(`The staged source must be a directory: ${relative}`);
        for (const item of fs.readdirSync(directory, { withFileTypes: true })) {
            const name = `${relative}/${item.name}`;
            if (name === "Assets/Worlds/AdvancedRenderingParityWorld.asset") continue;
            const file = path.join(directory, item.name);
            if (item.isDirectory()) collect(file, name);
            else if (item.isFile()) addFile(file, name);
            else throw new Error(`Unexpected shadow comparison source entry: ${name}`);
        }
    }
    addFile(path.join(project, "AdvancedRenderingParity.xrproj"), "AdvancedRenderingParity.xrproj");
    collect(path.join(project, "Assets"), "Assets");
    collect(path.join(project, "Config"), "Config");
    files.sort((a, b) => a.path < b.path ? -1 : a.path > b.path ? 1 : 0);
    return { files, sha256: worldHash(files.map(file => `${file.path} ${file.sha256}\n`).join("")) };
}

if (process.argv[2] === "--receipt") {
    const [repoArgument, outputArgument, ...extra] = process.argv.slice(3);
    if (!repoArgument || !outputArgument || extra.length !== 0)
        throw new Error("Usage: node prepare-advanced-shadow-comparison.mjs --receipt <repo-root> <staged-output-directory>");
    const repo = path.resolve(repoArgument);
    const output = path.resolve(outputArgument);
    assertValidationDirectoryChain(repo, output);
    const snapshotPath = path.join(output, "staged-inputs.json");
    if (!fs.lstatSync(snapshotPath).isFile())
        throw new Error("The staged input snapshot must be a regular file.");
    const snapshot = JSON.parse(fs.readFileSync(snapshotPath, "utf8"));
    if (snapshot.schema !== 1 || !Array.isArray(snapshot.files) || !/^[0-9a-f]{64}$/.test(snapshot.sharedSourceSha256))
        throw new Error("The staged input snapshot is invalid.");
    const receipts = [];
    for (const state of ["on", "off"]) {
        const project = path.join(output, state, "project");
        const site = path.join(project, "Build", "browser-game");
        assertValidationDirectoryChain(repo, site);
        const currentInputs = sharedSourceSnapshot(project);
        fs.writeFileSync(path.join(output, `postpublish-inputs-${state}.json`), `${JSON.stringify({
            schema: 1, sharedSourceSha256: currentInputs.sha256, files: currentInputs.files,
        }, null, 2)}\n`, { flag: "wx" });
        if (currentInputs.sha256 !== snapshot.sharedSourceSha256
            || JSON.stringify(currentInputs.files) !== JSON.stringify(snapshot.files))
            throw new Error(`The staged shadow ${state} source inputs changed during publishing.`);
        const stagedWorld = path.join(project, "Assets", "Worlds", "AdvancedRenderingParityWorld.asset");
        if (!fs.lstatSync(stagedWorld).isFile())
            throw new Error(`The staged shadow ${state} world must be a regular file.`);
        if (worldHash(fs.readFileSync(stagedWorld)) !== expectedWorldHashes[state])
            throw new Error(`The staged shadow ${state} world changed during publishing.`);
        const manifest = JSON.parse(fs.readFileSync(path.join(site, "content", "manifest.json"), "utf8"));
        if (manifest.startupWorld !== "/game/Worlds/AdvancedRenderingParityWorld.asset")
            throw new Error(`The shadow ${state} bundle has the wrong startup world.`);
        if (manifest.startupSettings !== "/game/startup.asset")
            throw new Error(`The shadow ${state} bundle has the wrong startup settings.`);
        const matches = manifest.assets?.filter(asset => asset.path === manifest.startupWorld);
        const worldAsset = matches?.[0];
        if (matches?.length !== 1 || worldAsset.encoding !== "cooked-binary"
            || !/^[0-9a-f]{64}$/.test(worldAsset.hash) || worldAsset.url !== `payload/${worldAsset.hash}.bin`)
            throw new Error(`The shadow ${state} bundle has no verified startup world asset.`);
        const payload = fs.readFileSync(path.join(site, "content", worldAsset.url));
        if (worldHash(payload) !== worldAsset.hash)
            throw new Error(`The shadow ${state} world payload does not match the published manifest.`);
        const startupAssets = manifest.assets.filter(asset => asset.path === "/game/startup.asset");
        const startupAsset = startupAssets[0];
        if (startupAssets.length !== 1 || startupAsset.encoding !== "cooked-binary"
            || !/^[0-9a-f]{64}$/.test(startupAsset.hash)
            || startupAsset.url !== `payload/${startupAsset.hash}.bin`)
            throw new Error(`The shadow ${state} bundle has no verified startup settings asset.`);
        const startupPayload = fs.readFileSync(path.join(site, "content", startupAsset.url));
        if (startupPayload.length !== startupAsset.bytes || worldHash(startupPayload) !== startupAsset.hash)
            throw new Error(`The shadow ${state} startup settings payload does not match the published manifest.`);
        receipts.push({ site, manifest, startupAsset, value: {
            schema: 1,
            state,
            sourceWorldSha256: expectedWorldHashes[state],
            publishedWorldSha256: worldAsset.hash,
            canonicalCatalogManifestSha256: worldHash(fs.readFileSync(path.join(project, "Assets", "Shaders", "WebGPU", "manifest.json"))),
            sharedSourceSha256: snapshot.sharedSourceSha256,
        } });
    }
    if (receipts[0].value.canonicalCatalogManifestSha256 !== receipts[1].value.canonicalCatalogManifestSha256
        || receipts[0].value.sharedSourceSha256 !== receipts[1].value.sharedSourceSha256)
        throw new Error("The shadow comparison projects have different shared sources or shader catalogs.");
    const verifiedPath = path.join(output, "startup-verification.json");
    if (!fs.lstatSync(verifiedPath).isFile())
        throw new Error("The startup semantic verification must be a regular file.");
    const verified = JSON.parse(fs.readFileSync(verifiedPath, "utf8"));
    if (verified.schema !== 1 || verified.onStartupSha256 !== receipts[0].startupAsset.hash
        || verified.offStartupSha256 !== receipts[1].startupAsset.hash
        || !/^[0-9a-f]{64}$/.test(verified.normalizedStartupSha256)
        || JSON.stringify(verified.normalizedFields) !== JSON.stringify([
            "GameStartupSettings.ID", "BuildSettings.ID", "DefaultUserSettings.ID",
        ]))
        throw new Error("The startup semantic verification does not bind both published payloads.");
    const startupMetadata = asset => ({ ...asset, hash: null, url: null });
    if (JSON.stringify(startupMetadata(receipts[0].startupAsset)) !== JSON.stringify(startupMetadata(receipts[1].startupAsset)))
        throw new Error("The startup settings asset metadata differs outside its verified payload hash.");
    const withoutVariablePayloads = manifest => ({
        ...manifest,
        assets: manifest.assets.filter(asset => asset.path !== manifest.startupWorld && asset.path !== manifest.startupSettings),
    });
    if (JSON.stringify(withoutVariablePayloads(receipts[0].manifest)) !== JSON.stringify(withoutVariablePayloads(receipts[1].manifest)))
        throw new Error("The shadow comparison bundles differ outside their verified startup payloads.");
    if (receipts[0].value.publishedWorldSha256 === receipts[1].value.publishedWorldSha256)
        throw new Error("The shadow ON and OFF published worlds have the same payload.");
    for (const receipt of receipts) {
        receipt.value.publishedStartupSettingsSha256 = receipt.startupAsset.hash;
        receipt.value.verifiedStartupSemanticSha256 = verified.normalizedStartupSha256;
    }
    for (const { site, value } of receipts) {
        assertValidationDirectoryChain(repo, site);
        const receipt = path.join(site, "shadow-comparison.json");
        try {
            fs.lstatSync(receipt);
            throw new Error(`The shadow comparison receipt already exists: ${receipt}`);
        } catch (error) {
            if (error.code !== "ENOENT") throw error;
        }
        fs.writeFileSync(receipt, `${JSON.stringify(value, null, 2)}\n`, { flag: "wx" });
    }
    console.log(JSON.stringify(receipts.map(({ value }) => value)));
    process.exit(0);
}

const [repoArgument, cookedArgument, outputArgument, ...extra] = process.argv.slice(2);
if (!repoArgument || !cookedArgument || !outputArgument || extra.length !== 0)
    throw new Error("Usage: node prepare-advanced-shadow-comparison.mjs <repo-root> <cooked-catalog> <new-output-directory>");

const repo = path.resolve(repoArgument);
const cooked = path.resolve(cookedArgument);
const output = path.resolve(outputArgument);
assertValidationDirectoryChain(repo, output);
if (fs.existsSync(output))
    throw new Error("The shadow comparison output must start empty.");
if (!fs.statSync(path.join(cooked, "manifest.json")).isFile())
    throw new Error("The canonical cooked shader manifest is missing.");

const source = path.join(repo, "Samples", "AdvancedRenderingParity");
const worldPath = path.join(source, "Assets", "Worlds", "AdvancedRenderingParityWorld.asset");
const original = fs.readFileSync(worldPath, "utf8");
// Read a Windows checkout without changing the saved source or the fixture hash.
let world = original.replaceAll("\r\n", "\n");
if (worldHash(world) !== "543d7336e6f20c9f8f9c20834c2e22ffcb16ea0fec48272de5380c0c79a12b16")
    throw new Error("The saved Advanced world changed; review the shadow fixture before staging it.");

function replaceOnce(before, after) {
    if (world.split(before).length !== 2)
        throw new Error(`Expected one Advanced world source field: ${before.trim()}`);
    world = world.replace(before, after);
}

if (world.split("        CastsShadows: false").length !== 3)
    throw new Error("Expected exactly two lights with shadows disabled.");
world = world.replaceAll("        CastsShadows: false", "        CastsShadows: true\n        UseShadowAtlas: false\n        EnableContactShadows: false");
replaceOnce("        CascadeCount: 4", "        EnableCascadedShadows: false\n        CascadeCount: 4");
world = world.replaceAll(/        ShadowMapResolutionWidth: \d+/g, "        ShadowMapResolutionWidth: 256")
    .replaceAll(/        ShadowMapResolutionHeight: \d+/g, "        ShadowMapResolutionHeight: 256");
replaceOnce("        ShadowRenderMode: InstancedLayered", "        ShadowRenderMode: Sequential");
world = world.replaceAll(/        (?:Samples|FilterSamples|BlockerSamples): 4/g, match => match.replace(": 4", ": 8"));
replaceOnce("        SoftShadowMode: FixedPoisson", "        SoftShadowMode: ContactHardeningPcss");
replaceOnce("        Model:\n", "        Model: &shadow_fixture_model\n");
replaceOnce("        Scale: 1 1 1\n        CascadedShadowDistance:", "        Scale: 8 8 8\n        CascadedShadowDistance:");

const node = (kind, translation, suffix) => `    - Components:\n      - __type: XREngine.Components.Scene.Mesh.ModelComponent\n        Model: *shadow_fixture_model\n        UseSkinnedMotionVectors: true\n        MeshLayer: -1\n        ID: aaa10000-0000-4000-8000-000000000${suffix}1\n      Transform:\n        ID: aaa10000-0000-4000-8000-000000000${suffix}2\n        Translation: ${translation}\n        Scale: 0.35 0.35 0.35\n      ID: aaa10000-0000-4000-8000-000000000${suffix}3\n      Name: ${kind} Shadow Occluder\n`;
replaceOnce("  ID: c6d487aa-4b46-4ab0-8967-c331e3e73e6e\n",
    node("Directional", "-0.8775 0 1", "10") + node("Point", "0.23916667 0.5 1", "20")
    + "  ID: c6d487aa-4b46-4ab0-8967-c331e3e73e6e\n");

const worlds = {
    on: { text: world, sha256: expectedWorldHashes.on },
    off: { text: world.replaceAll("        CastsShadows: true", "        CastsShadows: false"),
        sha256: expectedWorldHashes.off },
};
for (const [state, fixture] of Object.entries(worlds)) {
    if (worldHash(fixture.text) !== fixture.sha256)
        throw new Error(`Unexpected ${state} shadow fixture hash: ${worldHash(fixture.text)}`);
}

for (const [state, fixture] of Object.entries(worlds)) {
    const project = path.join(output, state, "project");
    assertValidationDirectoryChain(repo, project);
    fs.mkdirSync(path.join(project, "Assets", "Shaders"), { recursive: true });
    fs.mkdirSync(path.join(project, "Config"));
    fs.copyFileSync(path.join(source, "AdvancedRenderingParity.xrproj"), path.join(project, "AdvancedRenderingParity.xrproj"));
    for (const folder of ["Worlds", "Textures", "Scripts"])
        fs.cpSync(path.join(source, "Assets", folder), path.join(project, "Assets", folder), { recursive: true });
    fs.copyFileSync(path.join(source, "Assets", "startup.asset"), path.join(project, "Assets", "startup.asset"));
    fs.cpSync(cooked, path.join(project, "Assets", "Shaders", "WebGPU"), { recursive: true, errorOnExist: true });
    assertValidationDirectoryChain(repo, path.join(project, "Assets", "Worlds"));
    const stagedWorld = path.join(project, "Assets", "Worlds", "AdvancedRenderingParityWorld.asset");
    if (!fs.lstatSync(stagedWorld).isFile())
        throw new Error("The staged shadow world must be a regular file.");
    fs.writeFileSync(stagedWorld, fixture.text);
    console.log(`${state} ${fixture.sha256} ${path.join(project, "AdvancedRenderingParity.xrproj")}`);
}
const onInputs = sharedSourceSnapshot(path.join(output, "on", "project"));
const offInputs = sharedSourceSnapshot(path.join(output, "off", "project"));
if (onInputs.sha256 !== offInputs.sha256 || JSON.stringify(onInputs.files) !== JSON.stringify(offInputs.files))
    throw new Error("The shadow comparison projects have different staged source inputs.");
fs.writeFileSync(path.join(output, "staged-inputs.json"), `${JSON.stringify({
    schema: 1, sharedSourceSha256: onInputs.sha256, files: onInputs.files,
}, null, 2)}\n`, { flag: "wx" });

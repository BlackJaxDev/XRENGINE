import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';

// Creates an engine-assets recipe for the static smoke world using the exact
// just-cooked schema-3 shader catalog; BrowserContentCooker verifies every hash.
const [baseRecipePath, shaderDirectory, fixtureDirectory, outputDirectory] = process.argv.slice(2);
assert(baseRecipePath && shaderDirectory && fixtureDirectory && outputDirectory,
    'Usage: node prepare-engine-effects-assets.mjs <base-recipe> <cooked-shaders> <cooked-world-fixture> <output-directory>');
const recipeRoot = await fs.realpath(path.dirname(baseRecipePath));
const shaderRoot = await fs.realpath(shaderDirectory);
const fixtureRoot = await fs.realpath(fixtureDirectory);
const contained = async (root, relative) => {
    assert(typeof relative === 'string' && /^[A-Za-z0-9_./-]+$/.test(relative) &&
        !path.isAbsolute(relative) && !relative.split('/').includes('..'),
        `Invalid recipe source path: ${relative}`);
    const file = await fs.realpath(path.join(root,relative));
    assert(file.startsWith(root+path.sep), `Source escapes its owned root: ${relative}`);
    assert((await fs.stat(file)).isFile(), `Source is not a file: ${relative}`);
    return file;
};
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
const base = JSON.parse(await fs.readFile(await contained(recipeRoot,path.basename(baseRecipePath)),'utf8'));
const catalog = JSON.parse(await fs.readFile(await contained(shaderRoot,'manifest.json'),'utf8'));
assert.equal(base.format,'xrengine-assets');
assert.equal(catalog.schemaVersion,3);
assert.equal(catalog.backend,'WebGPU');
assert(Array.isArray(catalog.artifacts) && catalog.artifacts.length >= 15);
assert(Array.isArray(catalog.pipelineArtifacts) && catalog.pipelineArtifacts.length === 9);
await fs.mkdir(outputDirectory,{recursive:true});
const assets=new Map();
for(const asset of base.assets) {
    const root = ['EngineSmokeWorld.bin', 'AotRuntimeMetadata.bin'].includes(asset.source)
        ? fixtureRoot : recipeRoot;
    const source = await contained(root,asset.source);
    await fs.copyFile(source,path.join(outputDirectory,asset.source));
    assert(!assets.has(asset.path), `Duplicate base asset ${asset.path}`);
    assets.set(asset.path,asset);
}
const shaderArtifacts=[];
const assetType='XREngine.Core.Files.TextFile, XREngine.Data';
const names=new Set(), identities=new Set();
for(const entry of catalog.artifacts) {
    assert(typeof entry.name==='string' && /^engine-[a-z0-9-]+$/.test(entry.name) &&
        /^[0-9a-f]{64}$/.test(entry.sha256) &&
        entry.descriptor===`${entry.sha256}.shader.json` &&
        !names.has(entry.name) && !identities.has(entry.sha256), 'Duplicate or invalid cooked shader identity');
    names.add(entry.name); identities.add(entry.sha256);
    const descriptorBytes=await fs.readFile(await contained(shaderRoot,entry.descriptor));
    assert.equal(digest(descriptorBytes),entry.sha256,'Cooked descriptor bytes changed after manifest publication');
    const descriptor = JSON.parse(descriptorBytes);
    assert.equal(descriptor.name,entry.name,'Cooked shader name differs from manifest');
    assert.equal(descriptor.target,'WebGPUWgsl','Cooked shader target differs from browser profile');
    const sourceName=descriptor.source.url;
    assert(/^[0-9a-f]{64}\.wgsl$/.test(sourceName) &&
        sourceName===`${descriptor.source.sha256}.wgsl`,'Cooked source identity is invalid');
    const sourceBytes=await fs.readFile(await contained(shaderRoot,sourceName));
    assert.equal(digest(sourceBytes),descriptor.source.sha256,'Cooked WGSL bytes changed after descriptor publication');
    assert.equal(sourceBytes.length,descriptor.source.byteLength,'Cooked WGSL byte length changed');
    const descriptorName=entry.sha256+'.json';
    const sourceAssetName=sourceName;
    await fs.writeFile(path.join(outputDirectory,descriptorName),descriptorBytes);
    await fs.writeFile(path.join(outputDirectory,sourceAssetName),sourceBytes);
    const descriptorPath=`/engine/Shaders/Cooked/${descriptorName}`;
    const sourcePath=`/engine/Shaders/Cooked/${sourceAssetName}`;
    shaderArtifacts.push({identity:entry.sha256,descriptor:descriptorPath,source:sourcePath});
    assert(!assets.has(descriptorPath),`Duplicate descriptor asset ${descriptorPath}`);
    assets.set(descriptorPath,{path:descriptorPath,type:assetType,encoding:'utf8-text',source:descriptorName,dependencies:[]});
    if(!assets.has(sourcePath))
        assets.set(sourcePath,{path:sourcePath,type:assetType,encoding:'utf8-text',source:sourceAssetName,dependencies:[]});
}
const recipe={...base,shaderArtifacts,materialVariants:catalog.materialVariants ?? [],
    pipelineArtifacts:catalog.pipelineArtifacts,computeArtifacts:catalog.computeArtifacts ?? [],assets:[...assets.values()]};
await fs.writeFile(path.join(outputDirectory,'engine-effects-assets.recipe.json'),JSON.stringify(recipe,null,2)+'\n');
console.log(JSON.stringify({shaders:shaderArtifacts.length,passes:recipe.pipelineArtifacts.length,
    assets:recipe.assets.length,recipe:path.join(outputDirectory,'engine-effects-assets.recipe.json')}));

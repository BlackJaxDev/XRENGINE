using XREngine.Components.Scene.Mesh;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Editor;

public partial class EditorFlyingCameraPawnComponent
{
    /// <summary>Captures the presented scene pick without transferring ownership to another window.</summary>
    internal bool TryCaptureNativeTextureDrop(XRWindow window, out XRMaterial material, out Func<bool> admission)
    {
        material = null!;
        admission = null!;
        if (window.IsDisposed || !window.IsFocused || !CanApplyCurrentWorldPickHover() ||
            !ReferenceEquals(Viewport?.Window, window) || !TryGetLastMeshHit(out var hit) ||
            hit.Component is not ModelComponent component || component.Model is not { } model ||
            !component.TryGetSourceSubMesh(hit.Mesh, out var subMesh) ||
            hit.Mesh.CurrentLODRenderer is not { } renderer ||
            hit.Mesh.MaterialOverride is not null || renderer.Material is not { } capturedMaterial ||
            renderer.Mesh is not { } geometry ||
            !subMesh.LODs.Any(lod => ReferenceEquals(lod.Mesh, geometry) && ReferenceEquals(lod.Material, capturedMaterial)) ||
            !HasAuthoredTextureDropSource(capturedMaterial, Engine.Assets.GameAssetsPath) ||
            component.World is not RuntimeWorld world || world.IsDisposing)
            return false;

        var owner = _activeWorldPickOwner;
        int ownerRevision = Volatile.Read(ref _activeWorldPickOwnerRevision);
        int inputRevision = Volatile.Read(ref _activeWorldPickInputRevision);
        if (!ReferenceEquals(owner.Window, window) || !ReferenceEquals(owner.World, world)) return false;
        var node = component.SceneNode;
        var transform = node.Transform;
        var document = world.TargetWorld;
        var source = capturedMaterial.SourceAsset;
        string? sourcePath = source.FilePath;
        var pawnViewport = Viewport;
        var materialOverride = hit.Mesh.MaterialOverride;
        var rendererMaterial = renderer.Material;
        var lodSet = subMesh.LODs;
        var lod = lodSet.First(value => ReferenceEquals(value.Mesh, geometry) && ReferenceEquals(value.Material, capturedMaterial));
        ulong lodMaterialIntent = lod.MaterialWriteIntent;
        var identities = new XRObjectBase[] { this, component, node, transform, model, subMesh, renderer, geometry, capturedMaterial, source }
            .Select(ObjectConstructionIdentity.Capture).ToArray();
        bool Current() => !window.IsDisposed && window.IsFocused &&
            ReferenceEquals(Viewport, pawnViewport) && ReferenceEquals(pawnViewport?.Window, window) &&
            window.Viewports.Contains(pawnViewport!) &&
            ReferenceEquals(pawnViewport?.AssociatedPlayer?.ControlledPawnComponent, this) && CanApplyCurrentWorldPickHover() &&
            IsCurrentWorldPickOwner(ownerRevision, owner) &&
            Volatile.Read(ref _activeWorldPickInputRevision) == inputRevision &&
            TryGetLastMeshHit(out var currentHit) && currentHit.Equals(hit) &&
            identities.All(identity => identity.IsCurrent && !identity.Target.IsDestroyed && !identity.Target.IsDestroyQueued) &&
            !world.IsDisposing && ReferenceEquals(world.TargetWorld, document) &&
            ReferenceEquals(component.SceneNode, node) && node.Components.Contains(component) &&
            ReferenceEquals(node.Transform, transform) && ReferenceEquals(node.World, world) &&
            ReferenceEquals(component.World, world) && ReferenceEquals(component.Model, model) &&
            component.TryGetSourceSubMesh(hit.Mesh, out var currentSubMesh) && ReferenceEquals(currentSubMesh, subMesh) &&
            ReferenceEquals(hit.Mesh.CurrentLODRenderer, renderer) &&
            ReferenceEquals(renderer.Mesh, geometry) &&
            ReferenceEquals(subMesh.LODs, lodSet) && lodSet.Contains(lod) && lod.MaterialWriteIntent == lodMaterialIntent &&
            ReferenceEquals(lod.Mesh, geometry) && ReferenceEquals(lod.Material, capturedMaterial) &&
            ReferenceEquals(hit.Mesh.MaterialOverride, materialOverride) && ReferenceEquals(renderer.Material, rendererMaterial) &&
            ReferenceEquals(capturedMaterial.SourceAsset, source) && string.Equals(source.FilePath, sourcePath, StringComparison.Ordinal) &&
            HasAuthoredTextureDropSource(capturedMaterial, Engine.Assets.GameAssetsPath);
        if (!Current()) return false;
        material = capturedMaterial;
        admission = Current;
        return true;
    }

    /// <summary>Texture drops edit only a material serialized in an existing project asset document.</summary>
    internal static bool HasAuthoredTextureDropSource(XRMaterial material, string? assetsRoot)
    {
        var source = material.SourceAsset;
        if (material.IsDestroyed || material.IsDestroyQueued || source.IsDestroyed || source.IsDestroyQueued ||
            !ReferenceEquals(source.SourceAsset, source) ||
            (!ReferenceEquals(source, material) && !source.EmbeddedAssets.Contains(material)) ||
            string.IsNullOrWhiteSpace(assetsRoot) || string.IsNullOrWhiteSpace(source.FilePath)) return false;
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(assetsRoot));
            string path = Path.GetFullPath(source.FilePath);
            string relative = Path.GetRelativePath(root, path);
            if (!string.Equals(Path.GetExtension(path), ".asset", StringComparison.OrdinalIgnoreCase) ||
                Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                !File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                return false;
            // Aliased directories cannot establish a canonical project owner.
            for (var directory = new DirectoryInfo(Path.GetDirectoryName(path)!); directory is not null; directory = directory.Parent)
            {
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if (string.Equals(Path.TrimEndingDirectorySeparator(directory.FullName), root, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

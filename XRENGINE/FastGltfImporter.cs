using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using XREngine.Rendering;
using XREngine.Rendering.Models;
using XREngine.Rendering.Models.Materials;
using XREngine.Scene;

namespace XREngine
{
    internal static class FastGltfImporter
    {
        private const string FastGltfLibraryName = "fastgltf";
        private static bool? _isAvailable;

        public static bool IsAvailable
        {
            get
            {
                if (_isAvailable.HasValue)
                    return _isAvailable.Value;

                if (NativeLibrary.TryLoad(FastGltfLibraryName, out IntPtr handle))
                {
                    NativeLibrary.Free(handle);
                    _isAvailable = true;
                }
                else
                {
                    _isAvailable = false;
                }

                return _isAvailable.Value;
            }
        }

        public static bool IsGltfFile(string filePath)
        {
            string ext = Path.GetExtension(filePath);
            return ext.Equals(".gltf", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".glb", StringComparison.OrdinalIgnoreCase);
        }

        public static bool ShouldUseFastGltf(string filePath)
            => IsGltfFile(filePath) && (Engine.EditorPreferences?.PreferFastGltfForGltf ?? false);

        public static bool TryImport(
            string filePath,
            ModelImportOptions options,
            out SceneNode? rootNode,
            out IReadOnlyCollection<XRMaterial> materials,
            out IReadOnlyCollection<XRMesh> meshes)
        {
            rootNode = null;
            materials = Array.Empty<XRMaterial>();
            meshes = Array.Empty<XRMesh>();

            if (!IsAvailable)
            {
                Debug.Out($"[FastGltfImporter] fastgltf native library not available; falling back to Assimp for '{filePath}'.");
                return false;
            }

            Debug.LogWarning("fastgltf backend is not yet wired into the engine; falling back to Assimp for glTF import.");
            return false;
        }
    }
}

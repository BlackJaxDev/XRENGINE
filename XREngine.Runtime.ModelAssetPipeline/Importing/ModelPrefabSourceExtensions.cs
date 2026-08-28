namespace XREngine.Rendering.Models;

/// <summary>Model source extensions claimed by the ModelAssetPipeline prefab importer.</summary>
public static class ModelPrefabSourceExtensions
{
    public static IReadOnlyList<string> All { get; } =
    [
        "3d", "3ds", "3mf", "ac", "acc", "amj", "ase", "ask", "b3d", "bvh",
        "csm", "cob", "dae", "dxf", "enff", "fbx", "gltf", "glb", "hmb", "ifc",
        "iqm", "irr", "irrmesh", "lwo", "lws", "lxo", "m3d", "md2", "md3",
        "md5anim", "md5camera", "md5mesh", "mdc", "mdl", "mesh.xml", "mot", "ms3d",
        "ndo", "nff", "obj", "off", "ogex", "ply", "pmx", "prj", "q3o", "q3s",
        "raw", "scn", "sib", "smd", "stl", "stp", "step", "ter", "uc", "usd",
        "usda", "usdc", "usdz", "vta", "x", "x3d", "xgl", "zgl",
    ];
}

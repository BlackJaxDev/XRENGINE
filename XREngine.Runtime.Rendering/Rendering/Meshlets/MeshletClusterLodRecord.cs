using System.Runtime.InteropServices;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The hierarchy record of one meshlet of a cluster hierarchy payload (8 bytes), in meshlet order.
/// </summary>
/// <param name="Group">The group that holds the meshlet (an index into the payload's group table).</param>
/// <param name="RefinedGroup">
/// The finer group that the cook simplified to make this meshlet, or -1 for a meshlet of the
/// original geometry (level 0).
/// </param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshletClusterLodRecord(int Group, int RefinedGroup);

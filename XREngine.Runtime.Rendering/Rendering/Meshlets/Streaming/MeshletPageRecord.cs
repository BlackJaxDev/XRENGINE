using System.Runtime.InteropServices;

namespace XREngine.Rendering.Meshlets;

/// <summary>
/// The resident record of one geometry page (16 bytes). The page holds the geometry-local clusters
/// [<see cref="FirstCluster"/>, <see cref="FirstCluster"/> + <see cref="ClusterCount"/>).
/// Before the page can become resident, every page in its dependency range must be resident.
/// </summary>
/// <param name="FirstCluster">The geometry-local index of the first cluster of the page.</param>
/// <param name="ClusterCount">The clusters of the page.</param>
/// <param name="FirstDependency">The first entry of the page's dependencies in the dependency table.</param>
/// <param name="DependencyCount">
/// The dependencies of the page: the geometry-local pages that hold a parent group of one of its
/// groups. Zero for a root page.
/// </param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct MeshletPageRecord(
    uint FirstCluster,
    uint ClusterCount,
    uint FirstDependency,
    uint DependencyCount);

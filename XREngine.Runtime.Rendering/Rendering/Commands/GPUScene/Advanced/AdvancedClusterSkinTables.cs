using System.Runtime.CompilerServices;
using XREngine.Rendering.Meshlets;

namespace XREngine.Rendering.Commands;

/// <summary>
/// The skinning tables that the Advanced task stage reads to bound the posed clusters and groups of
/// a skinned cluster hierarchy draw. They are built once per <see cref="MeshletPayload"/> and cached,
/// so the per-frame layout and pack calls do not allocate.
/// </summary>
/// <remarks>
/// <para>
/// A cluster's bones come from the cook (<see cref="MeshletPayload.ClusterSkins"/> and
/// <see cref="MeshletPayload.ClusterBones"/>). A group's bones are the union of the bones of its
/// clusters and of each cluster's refined group, built from the full-detail level up. A group's
/// list therefore holds the list of every group below it. With the cooked group spheres, which
/// grow from child to parent, the task stage's posed group box then holds the box of each child
/// group, so the projected error never falls from child to parent and the cut stays consistent.
/// </para>
/// <para>
/// Word layout after the cluster and group records of the triangle-word stream (offsets are
/// relative to the first cluster record): one cluster skin record per meshlet (bone list offset,
/// bone count), then the bone words (0-based utilized-bone indices; the palette row is the index
/// plus one). The cook's cluster bone list comes first, so a cluster's list offset is the start of
/// the bone words plus its <see cref="MeshletClusterSkinRecord.FirstBone"/>. The group lists follow
/// it. Group records keep their bone list offset and count in their two reserved words.
/// </para>
/// </remarks>
internal sealed class AdvancedClusterSkinTables
{
    private static readonly ConditionalWeakTable<MeshletPayload, AdvancedClusterSkinTables> s_tables = new();

    /// <summary>Words of one cluster skin record: bone list offset, bone count.</summary>
    public const int ClusterSkinRecordWords = 2;

    private AdvancedClusterSkinTables(uint[] groupBoneOffsets, uint[] groupBoneCounts, uint[] groupBones)
    {
        GroupBoneFirst = groupBoneOffsets;
        GroupBoneCounts = groupBoneCounts;
        GroupBones = groupBones;
    }

    /// <summary>The first entry of each group's list in <see cref="GroupBones"/>.</summary>
    public uint[] GroupBoneFirst { get; }

    /// <summary>The bone count of each group's list.</summary>
    public uint[] GroupBoneCounts { get; }

    /// <summary>The concatenated bone lists of the groups (0-based utilized-bone indices).</summary>
    public uint[] GroupBones { get; }

    /// <summary>
    /// Returns the cached tables of a skinned hierarchy payload, or null for a payload without
    /// skinning tables.
    /// </summary>
    public static AdvancedClusterSkinTables? For(MeshletPayload payload)
        => payload.HasClusterHierarchy && payload.HasClusterSkinning
            ? s_tables.GetValue(payload, Build)
            : null;

    /// <summary>The words that the skin tables add after the group records.</summary>
    public static int SkinWordCount(MeshletPayload payload, AdvancedClusterSkinTables tables)
        => checked(
            payload.ClusterSkins.Length * ClusterSkinRecordWords +
            payload.ClusterBones.Length +
            tables.GroupBones.Length);

    private static AdvancedClusterSkinTables Build(MeshletPayload payload)
    {
        int groupCount = payload.LodGroups.Length;
        int clusterCount = payload.ClusterLods.Length;
        int boneLimit = 0;
        foreach (ushort bone in payload.ClusterBones)
            boneLimit = Math.Max(boneLimit, bone + 1);

        // Clusters in order of their group's depth: a refined group is one level below, so its
        // list is complete before any cluster made from it adds that list to its own group.
        int[] order = new int[clusterCount];
        for (int i = 0; i < clusterCount; i++)
            order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int depthA = GroupDepth(payload, payload.ClusterLods[a].Group);
            int depthB = GroupDepth(payload, payload.ClusterLods[b].Group);
            return depthA != depthB ? depthA.CompareTo(depthB) : a.CompareTo(b);
        });

        List<uint>[] lists = new List<uint>[groupCount];
        bool[][] present = new bool[groupCount][];
        for (int g = 0; g < groupCount; g++)
        {
            lists[g] = [];
            present[g] = new bool[Math.Max(boneLimit, 1)];
        }

        foreach (int cluster in order)
        {
            MeshletClusterLodRecord record = payload.ClusterLods[cluster];
            if ((uint)record.Group >= (uint)groupCount)
                continue;

            MeshletClusterSkinRecord skin = payload.ClusterSkins[cluster];
            for (int i = 0; i < skin.BoneCount; i++)
                AddBone(lists[record.Group], present[record.Group], payload.ClusterBones[(int)skin.FirstBone + i]);

            if ((uint)record.RefinedGroup < (uint)groupCount && record.RefinedGroup != record.Group)
                foreach (uint bone in lists[record.RefinedGroup])
                    AddBone(lists[record.Group], present[record.Group], bone);
        }

        uint[] first = new uint[groupCount];
        uint[] counts = new uint[groupCount];
        int total = 0;
        for (int g = 0; g < groupCount; g++)
            total = checked(total + lists[g].Count);

        uint[] bones = new uint[total];
        int cursor = 0;
        for (int g = 0; g < groupCount; g++)
        {
            first[g] = (uint)cursor;
            counts[g] = (uint)lists[g].Count;
            lists[g].Sort();
            lists[g].CopyTo(bones, cursor);
            cursor += lists[g].Count;
        }

        return new AdvancedClusterSkinTables(first, counts, bones);
    }

    private static int GroupDepth(MeshletPayload payload, int group)
        => (uint)group < (uint)payload.LodGroups.Length ? payload.LodGroups[group].Depth : int.MaxValue;

    private static void AddBone(List<uint> list, bool[] present, uint bone)
    {
        if (bone >= (uint)present.Length || present[bone])
            return;

        present[bone] = true;
        list.Add(bone);
    }
}

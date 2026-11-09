using System.Runtime.CompilerServices;

namespace XREngine.Rendering.Commands
{
    public sealed partial class GPURenderPassCollection
    {
        /// <summary>
        /// Number of material-table updates that a confirmed texture-readiness result
        /// stays valid when the material reports no texture change. Each material adds
        /// a stable offset to this period so that rechecks spread across updates.
        /// </summary>
        private const ulong MaterialTextureReadinessRecheckPeriod = 32ul;

        /// <summary>
        /// Number of material-table updates between removals of expired readiness
        /// entries. An expired entry can never match again, so removal only bounds
        /// the size of the cache.
        /// </summary>
        private const ulong MaterialTextureReadinessPruneInterval = 256ul;

        /// <summary>
        /// Identifies one material-table row source. One pass collection can resolve
        /// the same material id to different effective materials, for example an
        /// override material or a depth-normal variant, so the effective material is
        /// part of the key. Material equality is reference equality.
        /// </summary>
        private readonly struct MaterialTextureReadinessKey(uint materialId, XRMaterial material)
            : IEquatable<MaterialTextureReadinessKey>
        {
            public uint MaterialId { get; } = materialId;
            public XRMaterial Material { get; } = material;

            public bool Equals(MaterialTextureReadinessKey other)
                => MaterialId == other.MaterialId && ReferenceEquals(Material, other.Material);

            public override bool Equals(object? obj)
                => obj is MaterialTextureReadinessKey other && Equals(other);

            public override int GetHashCode()
                => HashCode.Combine(MaterialId, RuntimeHelpers.GetHashCode(Material));
        }

        /// <summary>
        /// The last confirmed state in which all textures of one material-table row
        /// were resident.
        /// </summary>
        private readonly struct MaterialTextureReadinessStamp(
            ulong bindingResourceVersion,
            ulong textureSourceVersion,
            ulong confirmedUpdate)
        {
            public ulong BindingResourceVersion { get; } = bindingResourceVersion;
            public ulong TextureSourceVersion { get; } = textureSourceVersion;
            public ulong ConfirmedUpdate { get; } = confirmedUpdate;
        }

        private readonly Dictionary<MaterialTextureReadinessKey, MaterialTextureReadinessStamp> _materialTextureReadiness = [];
        private ulong _materialTableUpdateIndex;

        private static ulong GetMaterialTextureReadinessPeriod(uint materialId)
            => MaterialTextureReadinessRecheckPeriod + materialId % MaterialTextureReadinessRecheckPeriod;

        /// <summary>
        /// Starts a material-table update and removes expired readiness entries at a
        /// fixed interval.
        /// </summary>
        private void BeginMaterialTextureReadinessUpdate()
        {
            _materialTableUpdateIndex++;
            if (_materialTableUpdateIndex % MaterialTextureReadinessPruneInterval != 0ul)
                return;

            foreach ((MaterialTextureReadinessKey key, MaterialTextureReadinessStamp stamp) in _materialTextureReadiness)
            {
                if (_materialTableUpdateIndex - stamp.ConfirmedUpdate >= GetMaterialTextureReadinessPeriod(key.MaterialId))
                    _materialTextureReadiness.Remove(key);
            }
        }

        /// <summary>
        /// Returns true when an earlier update confirmed that every texture of
        /// <paramref name="material"/> was resident, and the material has not changed
        /// its texture list or texture sources since. Residency checks lock the
        /// renderer object cache for each texture, so the material table skips them
        /// for confirmed rows. A periodic recheck catches texture objects that a
        /// renderer destroys without a material change.
        /// </summary>
        private bool IsMaterialTextureReadinessConfirmed(uint materialId, XRMaterial material)
        {
            if (!_materialTextureReadiness.TryGetValue(new MaterialTextureReadinessKey(materialId, material), out MaterialTextureReadinessStamp stamp))
                return false;

            return stamp.BindingResourceVersion == material.BindingResourceVersion &&
                stamp.TextureSourceVersion == material.TextureSourceVersion &&
                _materialTableUpdateIndex - stamp.ConfirmedUpdate < GetMaterialTextureReadinessPeriod(materialId);
        }

        /// <summary>
        /// Records the residency result of one material-table row. Only a fully
        /// resident row is kept; any other result makes the next update check again.
        /// </summary>
        private void RecordMaterialTextureReadiness(uint materialId, XRMaterial? material, bool ready)
        {
            if (material is null)
                return;

            MaterialTextureReadinessKey key = new(materialId, material);
            if (!ready)
            {
                _materialTextureReadiness.Remove(key);
                return;
            }

            _materialTextureReadiness[key] = new MaterialTextureReadinessStamp(
                material.BindingResourceVersion,
                material.TextureSourceVersion,
                _materialTableUpdateIndex);
        }
    }
}

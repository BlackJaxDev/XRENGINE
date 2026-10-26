using System.ComponentModel;
using System.Text.Json.Serialization;
using MemoryPack;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.Models;

public partial class SubMeshLOD
{
    [NonSerialized]
    private ulong _materialWriteIntent;

    /// <summary>Identifies material assignment attempts, including equal-value writes.</summary>
    [Browsable(false), YamlIgnore, JsonIgnore, MemoryPackIgnore]
    public ulong MaterialWriteIntent => _materialWriteIntent;

    private void AdvanceMaterialWriteIntent()
        => SetField(ref _materialWriteIntent, checked(_materialWriteIntent + 1), publishNotifications: false);

    /// <summary>Assigns a material only while the caller retains the exact LOD owner.</summary>
    public bool SetMaterialGuarded<TState>(XRMaterial? value, TState state, Action<TState> requireCurrent)
    {
        ArgumentNullException.ThrowIfNull(requireCurrent);
        AdvanceMaterialWriteIntent();
        var proof = (Owner: this, Revision: _materialWriteIntent, State: state, Require: requireCurrent);
        bool changed = SetFieldValidated(ref _material, value, proof,
            static (guard, original, current, _) =>
            {
                RequireMaterialWrite(guard);
                if (!ReferenceEquals(original, current))
                    throw new InvalidOperationException("The LOD material changed during assignment.");
            }, propertyName: nameof(Material));
        RequireMaterialWrite(proof);
        if (!ReferenceEquals(_material, value))
            throw new InvalidOperationException("The LOD material assignment was rejected.");
        return changed;
    }

    private static void RequireMaterialWrite<TState>((SubMeshLOD Owner, ulong Revision,
        TState State, Action<TState> Require) proof)
    {
        if (proof.Owner.MaterialWriteIntent != proof.Revision)
            throw new InvalidOperationException("A newer LOD material request replaced this assignment.");
        proof.Require(proof.State);
        if (proof.Owner.MaterialWriteIntent != proof.Revision)
            throw new InvalidOperationException("A newer LOD material request replaced this assignment.");
    }
}

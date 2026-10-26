using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Rendering;
using XREngine.Rendering.Models;

namespace XREngine.Editor.UI;

/// <summary>Changes the exact LOD materials of an explicitly owned model submesh.</summary>
internal static class ModelSubMeshMaterialAction
{
    private static readonly HashSet<object> Busy = new(ReferenceEqualityComparer.Instance);

    internal static bool Apply(Model model, SubMesh subMesh, XRMaterial material,
        Func<bool>? initialAdmission = null)
    {
        if (model.IsDestroyed || model.IsDestroyQueued || subMesh.IsDestroyed || subMesh.IsDestroyQueued ||
            material.IsDestroyed || material.IsDestroyQueued || !model.Meshes.Contains(subMesh)) return false;
        var meshes = model.Meshes;
        var lodSet = subMesh.LODs;
        SubMeshLOD[] lods = [.. lodSet];
        if (lods.Length == 0) return false;
        XRMaterial?[] before = lods.Select(lod => lod.Material).ToArray();
        if (before.All(value => ReferenceEquals(value, material))) return false;
        XRAsset modelDocument = model.SourceAsset, subMeshDocument = subMesh.SourceAsset;
        XRAsset[] documents = [.. new[] { modelDocument, subMeshDocument }
            .Distinct<XRAsset>(ReferenceEqualityComparer.Instance)];
        XRAsset[] assets = [model, subMesh, material, .. documents, .. before.OfType<XRAsset>()];
        ObjectConstructionIdentity[] identities = assets.Select(ObjectConstructionIdentity.Capture).ToArray();
        object[] owners = [model, subMesh, .. lods];
        bool blocked = false;
        bool admitting = true;
        bool Owns()
        {
            if (admitting && initialAdmission?.Invoke() == false) return false;
            for (int index = 0; index < assets.Length; index++)
                if (!identities[index].IsCurrent || assets[index].IsDestroyed || assets[index].IsDestroyQueued)
                    return false;
            return ReferenceEquals(model.SourceAsset, modelDocument) &&
                ReferenceEquals(subMesh.SourceAsset, subMeshDocument) &&
                ReferenceEquals(model.Meshes, meshes) && meshes.Contains(subMesh) &&
                ReferenceEquals(subMesh.LODs, lodSet) && lodSet.SequenceEqual(lods, ReferenceEqualityComparer.Instance);
        }
        bool Matches(bool after) => !blocked && Owns() && lods.Select((lod, index) =>
            ReferenceEquals(lod.Material, after ? material : before[index])).All(value => value);
        bool OwnsDocument(XRAsset document)
            => ReferenceEquals(document, modelDocument) && identities[0].IsCurrent &&
                !model.IsDestroyed && !model.IsDestroyQueued && ReferenceEquals(model.SourceAsset, modelDocument) ||
                ReferenceEquals(document, subMeshDocument) && identities[1].IsCurrent &&
                !subMesh.IsDestroyed && !subMesh.IsDestroyQueued && ReferenceEquals(subMesh.SourceAsset, subMeshDocument);
        void MarkDirty()
        {
            foreach (XRAsset document in documents)
                if (!document.IsDestroyed && !document.IsDestroyQueued &&
                    identities[Array.IndexOf(assets, document)].IsCurrent && OwnsDocument(document))
                    try { document.MarkDirty(); }
                    catch (Exception failure)
                    {
                        try { Debug.LogException(failure, "Could not mark the material owner dirty."); }
                        catch { }
                    }
        }
        void Reserve()
        {
            lock (Busy)
            {
                if (owners.Any(Busy.Contains)) throw new InvalidOperationException("The model material owner is being edited.");
                foreach (object owner in owners) Busy.Add(owner);
            }
        }
        void Release() { lock (Busy) foreach (object owner in owners) Busy.Remove(owner); }
        void Commit(bool forward)
        {
            if (!Matches(!forward)) throw new InvalidOperationException("The model material source changed.");
            bool[] phases = Enumerable.Repeat(!forward, lods.Length).ToArray();
            ulong[] revisions = lods.Select(lod => lod.MaterialWriteIntent).ToArray();
            for (int index = 0; index < lods.Length; index++)
                if (!ReferenceEquals(before[index], material) && revisions[index] > ulong.MaxValue - 2)
                    throw new InvalidOperationException("The LOD material revision cannot reserve compensation.");
            bool Current(int writing = -1)
            {
                if (!Owns()) return false;
                for (int index = 0; index < lods.Length; index++)
                    if (lods[index].MaterialWriteIntent != revisions[index] ||
                        !ReferenceEquals(lods[index].Material, phases[index] ? material : before[index]) &&
                        !(index == writing && ReferenceEquals(lods[index].Material, phases[index] ? before[index] : material)))
                        return false;
                return true;
            }
            void Write(int index, bool after)
            {
                if (!Current()) throw new InvalidOperationException("A LOD material peer changed.");
                revisions[index]++;
                lods[index].SetMaterialGuarded(after ? material : before[index], index,
                    writing => { if (!Current(writing)) throw new InvalidOperationException("The LOD material owner changed."); });
                phases[index] = after;
                if (!Current()) throw new InvalidOperationException("A LOD material callback changed the edit.");
            }
            int current = -1;
            try
            {
                for (int step = 0; step < lods.Length; step++)
                {
                    current = forward ? step : lods.Length - step - 1;
                    if (ReferenceEquals(before[current], material)) continue;
                    Write(current, forward);
                }
            }
            catch
            {
                if (current >= 0 && lods[current].MaterialWriteIntent == revisions[current] &&
                    ReferenceEquals(lods[current].Material, forward ? material : before[current])) phases[current] = forward;
                try
                {
                    for (int step = lods.Length - 1; step >= 0; step--)
                    {
                        int index = forward ? step : lods.Length - step - 1;
                        if (phases[index] != forward || ReferenceEquals(before[index], material)) continue;
                        if (!Current()) break;
                        Write(index, !forward);
                    }
                }
                catch { }
                if (!Current() || !Matches(!forward)) blocked = true;
                throw;
            }
        }
        void Replay(bool forward)
        {
            Reserve();
            try { Commit(forward); }
            finally { try { MarkDirty(); } finally { Release(); } }
        }
        if (!Owns() || lods.Where((lod, index) => !ReferenceEquals(before[index], material))
                .Any(lod => lod.MaterialWriteIntent > ulong.MaxValue - 2))
            return false;
        Reserve();
        try
        {
            using var interaction = Undo.BeginUserInteraction();
            using var scope = Undo.BeginChange("Drop Material");
            foreach (SubMeshLOD lod in lods) Undo.Track(lod);
            using var handle = Undo.BeginStructuralMutation("Drop Material", () => Replay(false), () => Replay(true));
            bool accepted = false;
            try
            {
                Commit(true);
                accepted = Matches(true);
                if (!accepted) throw new InvalidOperationException("The model material edit lost its owner.");
            }
            finally
            {
                if (accepted || Matches(false))
                    foreach (SubMeshLOD lod in lods) handle.RemoveOwnedProperty(lod, nameof(SubMeshLOD.Material));
                if (!accepted && Matches(false)) handle.RemoveRestoredStep();
                else
                    Undo.RecordStructuralChange("Drop Material preflight",
                        () => { if (!Matches(true)) throw new InvalidOperationException("The model material undo source changed."); },
                        () => { if (!Matches(true)) throw new InvalidOperationException("The model material redo source changed."); });
            }
            return accepted;
        }
        finally
        {
            admitting = false;
            try { MarkDirty(); } finally { Release(); }
        }
    }
}

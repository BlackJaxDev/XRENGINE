using XREngine.Core.Files;
using XREngine.Data.Core;
using XREngine.Rendering;

namespace XREngine.Editor.UI;

/// <summary>
/// Replaces one texture slot of a material as one structural Undo step. Apply, Undo, and Redo mark
/// the material's source document dirty. Each replay checks that the material, its texture list,
/// and the slot still hold the exact values that the edit recorded.
/// </summary>
internal static class MaterialTextureSlotAction
{
    private static readonly HashSet<object> Busy = new(ReferenceEqualityComparer.Instance);

    /// <summary>Applies the edit in its own Undo action. Returns false when nothing changed.</summary>
    internal static bool Apply(XRMaterial material, int slot, XRTexture texture, string description = "Assign Texture",
        Func<bool>? initialAdmission = null)
    {
        using var interaction = Undo.BeginUserInteraction();
        using var scope = Undo.BeginChange(description);
        return ApplyInScope(material, slot, texture, description, initialAdmission);
    }

    /// <summary>
    /// Applies the edit as one step of the caller's open user change scope. Returns false when
    /// nothing changed. Throws when the material or slot is not valid.
    /// </summary>
    internal static bool ApplyInScope(XRMaterial material, int slot, XRTexture texture, string description,
        Func<bool>? initialAdmission = null)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(texture);
        if (material.IsDestroyed || material.IsDestroyQueued || texture.IsDestroyed || texture.IsDestroyQueued)
            throw new InvalidOperationException("The material or texture is destroyed.");
        EventList<XRTexture?> textures = material.Textures;
        ulong listIntent = textures.MutationIntentRevision;
        ulong materialIntent = material.TextureListWriteIntent;
        if (slot < 0 || slot >= textures.Count)
            throw new ArgumentOutOfRangeException(nameof(slot), "The material has no texture slot at this index.");
        XRTexture? before = textures[slot];
        if (ReferenceEquals(before, texture))
            return false;
        XRAsset document = material.SourceAsset;
        XRAsset[] assets = before is null ? [material, texture, document] : [material, texture, document, before];
        ObjectConstructionIdentity[] identities = new ObjectConstructionIdentity[assets.Length];
        for (int index = 0; index < assets.Length; index++)
            identities[index] = ObjectConstructionIdentity.Capture(assets[index]);
        bool admitting = true;

        bool Owns()
        {
            if (admitting && initialAdmission?.Invoke() == false)
                return false;
            for (int index = 0; index < assets.Length; index++)
                if (!identities[index].IsCurrent || assets[index].IsDestroyed || assets[index].IsDestroyQueued)
                    return false;
            return material.TextureListWriteIntent == materialIntent && textures.MutationIntentRevision == listIntent &&
                ReferenceEquals(material.SourceAsset, document) && ReferenceEquals(material.Textures, textures) &&
                slot < textures.Count;
        }
        bool Matches(bool after) => Owns() && ReferenceEquals(textures[slot], after ? texture : before);
        void MarkDirty()
        {
            if (document.IsDestroyed || document.IsDestroyQueued || !identities[2].IsCurrent ||
                !ReferenceEquals(material.SourceAsset, document))
                return;
            try { document.MarkDirty(); }
            catch (Exception failure)
            {
                try { Debug.LogException(failure, "Could not mark the material document dirty."); }
                catch { }
            }
        }
        void Reserve()
        {
            lock (Busy)
                if (!Busy.Add(material))
                    throw new InvalidOperationException("The material texture slot is being edited.");
        }
        void Release() { lock (Busy) Busy.Remove(material); }
        void Write(bool forward)
        {
            if (!Matches(!forward))
                throw new InvalidOperationException("The material texture slot changed after the edit.");
            listIntent = checked(listIntent + 1);
            if (!material.SetTextureSlotGuarded<Func<bool>>(textures, slot, forward ? before : texture,
                forward ? texture : before, Owns, static owns =>
                {
                    if (!owns())
                        throw new InvalidOperationException("The material texture slot owner changed during the edit.");
                }))
                throw new InvalidOperationException("The material texture slot write was rejected.");
            if (!Matches(forward))
                throw new InvalidOperationException("A texture slot callback changed the edit.");
        }
        void Replay(bool forward)
        {
            Reserve();
            try { Write(forward); }
            finally { try { MarkDirty(); } finally { Release(); } }
        }

        if (!Owns())
            return false;
        Reserve();
        try
        {
            Write(true);
            admitting = false;
            using StructuralChangeHandle handle = Undo.BeginStructuralMutation(description,
                () => Replay(false), () => Replay(true));
            return true;
        }
        catch
        {
            admitting = false;
            if (Owns() && ReferenceEquals(textures[slot], texture))
                Write(false);
            throw;
        }
        finally
        {
            admitting = false;
            try { MarkDirty(); } finally { Release(); }
        }
    }
}

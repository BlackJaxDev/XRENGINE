using System.ComponentModel;
using System.Text.Json.Serialization;
using MemoryPack;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Rendering;

public abstract partial class XRMaterialBase
{
    [NonSerialized]
    private ulong _textureListWriteIntent;

    /// <summary>Changes for every texture-list property assignment, including equal requests.</summary>
    [Browsable(false), YamlIgnore, JsonIgnore, MemoryPackIgnore]
    public ulong TextureListWriteIntent => Volatile.Read(ref _textureListWriteIntent);

    private void AdvanceTextureListWriteIntent()
    {
        while (true)
        {
            ulong current = TextureListWriteIntent;
            if (current == ulong.MaxValue)
                throw new InvalidOperationException("The material texture-list intent cannot advance.");
            if (Interlocked.CompareExchange(ref _textureListWriteIntent, current + 1, current) == current)
                return;
        }
    }

    /// <summary>
    /// Changes an exact texture slot on its owner thread. Every callback must retain the
    /// material, list, texture lifetimes, and caller admission. Other slots are not rewritten.
    /// </summary>
    public bool SetTextureSlotGuarded<TState>(EventList<XRTexture?> expectedList, int index,
        XRTexture? expectedValue, XRTexture? value, TState state, Action<TState> requireCurrent)
    {
        ArgumentNullException.ThrowIfNull(expectedList);
        ArgumentNullException.ThrowIfNull(requireCurrent);
        var guard = (Material: this, List: expectedList, Intent: TextureListWriteIntent,
            Identity: ObjectConstructionIdentity.Capture(this), Before: expectedValue, After: value,
            BeforeIdentity: expectedValue is null ? default : ObjectConstructionIdentity.Capture(expectedValue),
            AfterIdentity: value is null ? default : ObjectConstructionIdentity.Capture(value),
            State: state, Require: requireCurrent, Thread: Environment.CurrentManagedThreadId);
        return expectedList.SetIndexValidated(index, expectedValue, value, guard, static current =>
        {
            current.Require(current.State);
            if (Environment.CurrentManagedThreadId != current.Thread || !current.Identity.IsCurrent ||
                current.Material.IsDestroyed || current.Material.IsDestroyQueued ||
                current.Material._materialTeardownRequested ||
                current.Material.TextureListWriteIntent != current.Intent ||
                !ReferenceEquals(current.Material.Textures, current.List) ||
                current.Before is { } before && (!current.BeforeIdentity.IsCurrent || before.IsDestroyed || before.IsDestroyQueued) ||
                current.After is { } after && (!current.AfterIdentity.IsCurrent || after.IsDestroyed || after.IsDestroyQueued))
                throw new InvalidOperationException("The material texture-slot owner changed.");
        });
    }
}

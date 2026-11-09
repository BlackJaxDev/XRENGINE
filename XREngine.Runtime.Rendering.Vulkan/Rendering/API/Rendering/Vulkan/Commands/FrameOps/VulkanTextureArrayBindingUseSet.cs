namespace XREngine.Rendering.Vulkan;

/// <summary>Keeps one counted use for each array referenced by an authoring owner.</summary>
internal sealed class VulkanTextureArrayBindingUseSet
{
    private readonly Dictionary<XRTexture2DArray, int> _uses = new(ReferenceEqualityComparer.Instance);

    internal bool Contains(XRTexture texture)
        => texture is XRTexture2DArray array && _uses.ContainsKey(array);

    internal void RetainFresh(XRTexture texture)
    {
        if (texture is XRTexture2DArray array)
            Retain(array, transfer: false);
    }

    internal void RetainTransfer(XRTexture texture)
    {
        if (texture is XRTexture2DArray array)
            Retain(array, transfer: true);
    }

    private void Retain(XRTexture2DArray texture, bool transfer)
    {
        if (_uses.TryGetValue(texture, out int count))
        {
            _uses[texture] = checked(count + 1);
            return;
        }

        if (transfer)
            texture.RetainBindingUse();
        else if (!texture.TryRetainActiveBindingUse())
            throw new ObjectDisposedException(nameof(XRTexture2DArray));

        try
        {
            _uses.Add(texture, 1);
        }
        catch
        {
            texture.ReleaseBindingUse();
            throw;
        }
    }

    internal void Release(XRTexture texture)
    {
        if (texture is not XRTexture2DArray array ||
            !_uses.TryGetValue(array, out int count))
            return;

        if (count > 1)
        {
            _uses[array] = count - 1;
            return;
        }

        _uses.Remove(array);
        array.ReleaseBindingUse();
    }

    internal void ReplaceSampler<TKey>(Dictionary<TKey, XRTexture> slots, TKey key, XRTexture texture, bool transfer)
        where TKey : notnull
    {
        if (slots.TryGetValue(key, out XRTexture? previous) && ReferenceEquals(previous, texture))
            return;

        if (transfer)
            RetainTransfer(texture);
        else
            RetainFresh(texture);
        try
        {
            slots[key] = texture;
        }
        catch
        {
            Release(texture);
            throw;
        }
        if (previous is not null)
            Release(previous);
    }

    internal void ReplaceImage(Dictionary<uint, ProgramImageBinding> slots, uint unit, ProgramImageBinding binding, bool transfer)
    {
        if (slots.TryGetValue(unit, out ProgramImageBinding previous) &&
            ReferenceEquals(previous.Texture, binding.Texture))
        {
            slots[unit] = binding;
            return;
        }

        if (transfer)
            RetainTransfer(binding.Texture);
        else
            RetainFresh(binding.Texture);
        try
        {
            slots[unit] = binding;
        }
        catch
        {
            Release(binding.Texture);
            throw;
        }
        if (previous.Texture is not null)
            Release(previous.Texture);
    }

    internal void ReleaseAll()
    {
        Exception? firstFailure = null;
        foreach (XRTexture2DArray texture in _uses.Keys)
        {
            try
            {
                texture.ReleaseBindingUse();
            }
            catch (Exception exception)
            {
                firstFailure ??= exception;
            }
        }
        _uses.Clear();
        if (firstFailure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(firstFailure).Throw();
    }
}

using System;
using System.Collections.Generic;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Keeps inactive uniform-buffer generations available while command buffers recorded against
/// them may still be submitted. Generations are keyed by their descriptor frame/draw-slot count
/// and logical payload capacity, so a previously used layout can be reactivated without another
/// Vulkan allocation.
/// </summary>
internal sealed class VulkanUniformBufferGenerationCache<T>
    where T : class
{
    private readonly Dictionary<string, List<Entry>> _generations = new(StringComparer.Ordinal);
    private int _generationCount;

    internal int GenerationCount => _generationCount;

    internal void Retain(string name, int slotCount, uint payloadCapacity, T generation)
    {
        if (!_generations.TryGetValue(name, out List<Entry>? entries))
        {
            entries = [];
            _generations.Add(name, entries);
        }

        entries.Add(new Entry(slotCount, payloadCapacity, generation));
        _generationCount++;
    }

    internal bool TryTake(string name, int slotCount, uint requiredPayloadCapacity, out T generation)
    {
        generation = null!;
        if (!_generations.TryGetValue(name, out List<Entry>? entries))
            return false;

        int bestIndex = -1;
        uint bestCapacity = uint.MaxValue;
        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            if (entry.SlotCount != slotCount || entry.PayloadCapacity < requiredPayloadCapacity)
                continue;

            if (bestIndex >= 0 && entry.PayloadCapacity >= bestCapacity)
                continue;

            bestIndex = i;
            bestCapacity = entry.PayloadCapacity;
        }

        if (bestIndex < 0)
            return false;

        generation = entries[bestIndex].Generation;
        entries.RemoveAt(bestIndex);
        _generationCount--;
        if (entries.Count == 0)
            _generations.Remove(name);
        return true;
    }

    internal bool TryTakeAny(string name, out T generation)
    {
        generation = null!;
        if (!_generations.TryGetValue(name, out List<Entry>? entries) || entries.Count == 0)
            return false;

        int index = entries.Count - 1;
        generation = entries[index].Generation;
        entries.RemoveAt(index);
        _generationCount--;
        if (entries.Count == 0)
            _generations.Remove(name);
        return true;
    }

    internal bool TryTakeAny(out T generation)
    {
        generation = null!;
        string? name = null;
        foreach (string candidate in _generations.Keys)
        {
            name = candidate;
            break;
        }

        return name is not null && TryTakeAny(name, out generation);
    }

    private readonly record struct Entry(int SlotCount, uint PayloadCapacity, T Generation);
}

using System.Runtime.CompilerServices;
using XREngine.Audio;

namespace XREngine.Runtime.Audio;

/// <summary>
/// Owns the runtime-only association between a world object and the audio listeners
/// attached to it. The weak world key prevents audio integration state from extending
/// a world's lifetime.
/// </summary>
public static class RuntimeAudioListenerWorldRegistry
{
    private sealed class ListenerAttachment
    {
        public List<ListenerContext> Listeners { get; } = [];
    }

    private static readonly ConditionalWeakTable<object, ListenerAttachment> Attachments = new();

    /// <summary>
    /// Registers a listener with a world when both runtime objects are available.
    /// </summary>
    public static void AddListener(object? world, ListenerContext? listener)
    {
        if (world is null || listener is null)
            return;

        lock (Attachments)
        {
            ListenerAttachment attachment = Attachments.GetValue(world, static _ => new());
            foreach (ListenerContext existingListener in attachment.Listeners)
            {
                if (ReferenceEquals(existingListener, listener))
                    return;
            }

            attachment.Listeners.Add(listener);
        }
    }

    /// <summary>
    /// Removes a listener from its world attachment. Empty attachments are removed
    /// promptly; remaining attachments are collected with their weak world keys.
    /// </summary>
    public static void RemoveListener(object? world, ListenerContext? listener)
    {
        if (world is null || listener is null)
            return;

        lock (Attachments)
        {
            if (!Attachments.TryGetValue(world, out ListenerAttachment? attachment))
                return;

            for (int i = attachment.Listeners.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(attachment.Listeners[i], listener))
                    attachment.Listeners.RemoveAt(i);
            }

            if (attachment.Listeners.Count == 0)
                Attachments.Remove(world);
        }
    }

    /// <summary>
    /// Returns a stable listener snapshot for the world.
    /// </summary>
    public static ListenerContext[] GetListeners(object? world)
    {
        if (world is null)
            return [];

        lock (Attachments)
            return Attachments.TryGetValue(world, out ListenerAttachment? attachment)
                ? [.. attachment.Listeners]
                : [];
    }

    /// <summary>
    /// Returns the number of audio listeners currently attached to a world.
    /// </summary>
    public static int GetListenerCount(object? world)
    {
        if (world is null)
            return 0;

        lock (Attachments)
            return Attachments.TryGetValue(world, out ListenerAttachment? attachment)
                ? attachment.Listeners.Count
                : 0;
    }
}

namespace XREngine.Editor;

/// <summary>
/// Explicit editor-host registry for editor world composition.  This is kept
/// separate from Core's world registry because editor policy is optional and
/// has a shorter, host-controlled lifetime.
/// </summary>
public static class EditorWorldIntegrationRegistry
{
    private static readonly object Sync = new();
    private static readonly Dictionary<RuntimeWorld, EditorWorldIntegration> Integrations =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>Gets the editor integration for a world, creating and attaching it when needed.</summary>
    public static EditorWorldIntegration GetOrAttach(RuntimeWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        lock (Sync)
        {
            if (Integrations.TryGetValue(world, out EditorWorldIntegration? integration))
            {
                integration.TryBindRenderer();
                return integration;
            }

            integration = new EditorWorldIntegration(world);
            Integrations.Add(world, integration);
            return integration;
        }
    }

    /// <summary>Returns the attached editor integration, if the editor host owns one.</summary>
    public static bool TryGet(RuntimeWorld world, out EditorWorldIntegration? integration)
    {
        ArgumentNullException.ThrowIfNull(world);
        lock (Sync)
            return Integrations.TryGetValue(world, out integration);
    }

    /// <summary>Detaches and disposes the editor policy for one world.</summary>
    public static bool Detach(RuntimeWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);
        EditorWorldIntegration? integration;
        lock (Sync)
        {
            if (!Integrations.TryGetValue(world, out integration))
                return false;
        }

        integration.Dispose();
        return true;
    }

    /// <summary>Clears all editor integrations for deterministic host shutdown and tests.</summary>
    public static void ResetForTests()
    {
        EditorWorldIntegration[] integrations;
        lock (Sync)
        {
            integrations = [.. Integrations.Values];
        }

        foreach (EditorWorldIntegration integration in integrations)
            if (integration.World.PlayState != RuntimeWorldPlayState.Stopped)
            {
                throw new InvalidOperationException(
                    "Editor world integrations cannot be reset while a world is in an active play or edit session.");
            }

        foreach (EditorWorldIntegration integration in integrations)
            integration.Dispose();
    }

    internal static void Remove(RuntimeWorld world, EditorWorldIntegration integration)
    {
        lock (Sync)
        {
            if (Integrations.TryGetValue(world, out EditorWorldIntegration? registered)
                && ReferenceEquals(registered, integration))
            {
                Integrations.Remove(world);
            }
        }
    }
}

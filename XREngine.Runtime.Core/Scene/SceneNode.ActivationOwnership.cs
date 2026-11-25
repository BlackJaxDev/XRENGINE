using XREngine.Components;
using XREngine.Scene.Prefabs;

namespace XREngine.Scene;

public sealed partial class SceneNode
{
    private long _activityIntent;
    private Action? _guardedLeafActivation;
    private long _guardedLeafActivationIntent;

    private long AdvanceActivityIntent()
    {
        while (true)
        {
            long previous = Volatile.Read(ref _activityIntent);
            if (previous == long.MaxValue)
                throw new InvalidOperationException("The node activity intent history is exhausted.");
            long next = previous + 1;
            if (Interlocked.CompareExchange(ref _activityIntent, next, previous) == previous)
                return next;
        }
    }

    /// <summary>Activates an exact leaf only while its caller retains ownership across callbacks.</summary>
    internal void ActivateLeafGuarded(Action requireOwner)
    {
        ArgumentNullException.ThrowIfNull(requireOwner);
        long intent = AdvanceActivityIntent();
        if (intent == long.MaxValue)
            throw new InvalidOperationException("The guarded activity intent requires room for a newer request.");
        Action? previousGuard = _guardedLeafActivation;
        long previousIntent = _guardedLeafActivationIntent;
        _guardedLeafActivation = requireOwner;
        _guardedLeafActivationIntent = intent;
        try
        {
            RequireGuardedLeafActivation(requireOwner, intent);
            if (_isActiveSelf || ArePropertyNotificationsSuppressed)
                throw new InvalidOperationException("Guarded leaf activation requires an inactive leaf with notifications enabled.");
            using HierarchyMutationLease structural = new(this);
            using var mutation = PhysicsChainWorld.BeginNodeActivationMutation(this);
            SetFieldValidated(ref _isActiveSelf, true, (Node: this, Guard: requireOwner, Intent: intent),
                static (state, original, current, _) =>
                {
                    state.Node.RequireGuardedLeafActivation(state.Guard, state.Intent);
                    if (original || current)
                        throw new InvalidOperationException("The guarded leaf activity changed before its write.");
                }, propertyName: nameof(IsActiveSelf));
            RequireGuardedLeafActivation(requireOwner, intent);
            if (!IsActiveInHierarchy)
                throw new InvalidOperationException("The guarded leaf did not become active in its hierarchy.");
        }
        finally
        {
            _guardedLeafActivation = previousGuard;
            _guardedLeafActivationIntent = previousIntent;
        }
    }

    private void RequireGuardedLeafActivation(Action requireOwner, long intent)
    {
        if (Volatile.Read(ref _activityIntent) != intent ||
            !ReferenceEquals(_guardedLeafActivation, requireOwner) || _guardedLeafActivationIntent != intent ||
            IsDestroyed || IsDestroyQueued || _transform is null || _transform.ChildCount != 0)
            throw new InvalidOperationException("The guarded leaf activation lost its exact activity intent.");
        requireOwner();
        if (Volatile.Read(ref _activityIntent) != intent)
            throw new InvalidOperationException("A newer activity request superseded the guarded leaf activation.");
    }

    private void ActivateGuardedLeafCore(Action requireOwner, long intent)
    {
        RequireGuardedLeafActivation(requireOwner, intent);
        if (!IsActiveInHierarchy)
            throw new InvalidOperationException("The guarded leaf cannot dispatch activation under an inactive owner.");
        using HierarchyMutationLease structural = new(this);
        using var mutation = PhysicsChainWorld.BeginNodeActivationMutation(this);
        RequireGuardedLeafActivation(requireOwner, intent);
        _transform!.NotifySceneNodeActivated();
        RequireGuardedLeafActivation(requireOwner, intent);
        foreach (XRComponent component in ComponentsInternal)
        {
            RequireGuardedLeafActivation(requireOwner, intent);
            if (component.IsActive)
                component.NotifyComponentActivated();
            RequireGuardedLeafActivation(requireOwner, intent);
        }
        if (Activated is { } activated)
        {
            // This cold operation must stop between handlers if one transfers ownership.
            foreach (Action<SceneNode> handler in activated.GetInvocationList())
            {
                RequireGuardedLeafActivation(requireOwner, intent);
                handler(this);
                RequireGuardedLeafActivation(requireOwner, intent);
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Numerics;

namespace XREngine.Rendering.UI;

/// <summary>
/// Editor-friendly navigation graph defining allowed directions, explicit/blocked edges,
/// wrapping behavior, spatial fallback, and hold-to-repeat settings for a UI menu scope.
/// </summary>
public sealed class UINavigationFlowMap
{
    private const int MaxSkipHops = 8;
    private const float SpatialDirectionMinCosine = 0.35f;

    /// <summary>
    /// Default interactable focused when this navigation scope becomes active.
    /// </summary>
    public UIInteractableComponent? InitialFocus { get; set; }

    /// <summary>
    /// Bitmask of directional inputs accepted within this flow map.
    /// </summary>
    public EUINavigationAllowedDirections AllowedDirections { get; set; } = EUINavigationAllowedDirections.Cardinal;

    /// <summary>
    /// When true, navigating past the first or last vertical item wraps around to the opposite end.
    /// </summary>
    public bool WrapVertical { get; set; }

    /// <summary>
    /// When true, navigating past the first or last horizontal item wraps around to the opposite end.
    /// </summary>
    public bool WrapHorizontal { get; set; }

    /// <summary>
    /// When true, directions without an explicit or blocked edge fall back to geometric search within the scope.
    /// </summary>
    public bool AllowSpatialFallback { get; set; } = true;

    /// <summary>
    /// Analog stick hysteresis and hold-to-repeat configuration for this flow map.
    /// </summary>
    public UINavigationRepeatSettings RepeatSettings { get; set; } = UINavigationRepeatSettings.Default;

    /// <summary>
    /// Serializable list of explicit, blocked, or wrap edges authored in code or a visual UI editor.
    /// </summary>
    public List<UINavigationEdge> Edges { get; set; } = [];

    /// <summary>
    /// Adds or replaces the edge leaving <paramref name="source"/> in <paramref name="direction"/>.
    /// </summary>
    public UINavigationEdge SetEdge(
        UIInteractableComponent source,
        EUINavigationDirection direction,
        UIInteractableComponent? target,
        EUINavigationRule rule = EUINavigationRule.Explicit,
        bool bidirectional = false)
    {
        for (int i = 0; i < Edges.Count; i++)
        {
            UINavigationEdge existing = Edges[i];
            if (ReferenceEquals(existing.Source, source) && existing.Direction == direction)
            {
                existing.Target = target;
                existing.Rule = rule;
                existing.Bidirectional = bidirectional;
                return existing;
            }
        }

        UINavigationEdge created = new()
        {
            Source = source,
            Direction = direction,
            Target = target,
            Rule = rule,
            Bidirectional = bidirectional,
        };
        Edges.Add(created);
        return created;
    }

    /// <summary>
    /// Explicitly forbids navigation from <paramref name="source"/> in <paramref name="direction"/>.
    /// </summary>
    public UINavigationEdge BlockDirection(UIInteractableComponent source, EUINavigationDirection direction)
        => SetEdge(source, direction, target: null, rule: EUINavigationRule.Blocked, bidirectional: false);

    /// <summary>
    /// Removes all authored edges and clears <see cref="InitialFocus"/> while preserving direction and repeat settings.
    /// </summary>
    public void Clear()
    {
        Edges.Clear();
        InitialFocus = null;
    }

    /// <summary>
    /// Removes any authored edge leaving <paramref name="source"/> in <paramref name="direction"/>.
    /// </summary>
    public bool RemoveEdge(UIInteractableComponent source, EUINavigationDirection direction)
    {
        for (int i = 0; i < Edges.Count; i++)
        {
            UINavigationEdge edge = Edges[i];
            if (ReferenceEquals(edge.Source, source) && edge.Direction == direction)
            {
                Edges.RemoveAt(i);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Connects a vertical sequence of interactables with bidirectional Up/Down edges and optional wraparound.
    /// </summary>
    public void ConnectVerticalChain(bool wrap, params UIInteractableComponent[] items)
    {
        if (items.Length == 0)
            return;

        InitialFocus ??= items[0];
        WrapVertical = wrap;

        for (int i = 0; i < items.Length - 1; i++)
            SetEdge(items[i], EUINavigationDirection.Down, items[i + 1], EUINavigationRule.Explicit, bidirectional: true);

        if (wrap && items.Length > 1)
        {
            SetEdge(items[^1], EUINavigationDirection.Down, items[0], EUINavigationRule.Explicit, bidirectional: false);
            SetEdge(items[0], EUINavigationDirection.Up, items[^1], EUINavigationRule.Explicit, bidirectional: false);
        }
    }

    /// <summary>
    /// Connects a horizontal sequence of interactables with bidirectional Left/Right edges and optional wraparound.
    /// </summary>
    public void ConnectHorizontalChain(bool wrap, params UIInteractableComponent[] items)
    {
        if (items.Length == 0)
            return;

        InitialFocus ??= items[0];
        WrapHorizontal = wrap;

        for (int i = 0; i < items.Length - 1; i++)
            SetEdge(items[i], EUINavigationDirection.Right, items[i + 1], EUINavigationRule.Explicit, bidirectional: true);

        if (wrap && items.Length > 1)
        {
            SetEdge(items[^1], EUINavigationDirection.Right, items[0], EUINavigationRule.Explicit, bidirectional: false);
            SetEdge(items[0], EUINavigationDirection.Left, items[^1], EUINavigationRule.Explicit, bidirectional: false);
        }
    }

    /// <summary>
    /// Resolves the initial eligible interactable for this flow map.
    /// </summary>
    public UIInteractableComponent? ResolveInitialFocus(IReadOnlyList<UIInteractableComponent>? scopeCandidates = null)
    {
        if (IsEligible(InitialFocus))
            return InitialFocus;

        for (int i = 0; i < Edges.Count; i++)
        {
            UINavigationEdge edge = Edges[i];
            if (IsEligible(edge.Source))
                return edge.Source;
            if (IsEligible(edge.Target))
                return edge.Target;
        }

        if (scopeCandidates is not null)
        {
            for (int i = 0; i < scopeCandidates.Count; i++)
                if (IsEligible(scopeCandidates[i]))
                    return scopeCandidates[i];
        }

        return null;
    }

    /// <summary>
    /// Resolves the next focusable interactable from <paramref name="current"/> along <paramref name="direction"/>.
    /// Returns null when the direction is disallowed, blocked, or has no valid destination.
    /// </summary>
    public UIInteractableComponent? ResolveNext(
        UIInteractableComponent? current,
        EUINavigationDirection direction,
        IReadOnlyList<UIInteractableComponent>? scopeCandidates = null)
    {
        if (!UINavigationDirectionHelper.IsAllowed(direction, AllowedDirections))
            return null;

        if (!IsEligible(current))
            return ResolveInitialFocus(scopeCandidates);

        UIInteractableComponent origin = current!;
        UIInteractableComponent cursor = origin;

        for (int hop = 0; hop < MaxSkipHops; hop++)
        {
            UINavigationLink link = ResolveLinkForNode(cursor, direction);
            switch (link.Rule)
            {
                case EUINavigationRule.Blocked:
                    return null;

                case EUINavigationRule.Explicit:
                    if (link.Target is null || ReferenceEquals(link.Target, origin))
                        return null;
                    if (IsEligible(link.Target))
                        return link.Target;
                    cursor = link.Target;
                    continue;

                case EUINavigationRule.Wrap:
                    if (link.Target is not null && !ReferenceEquals(link.Target, origin) && IsEligible(link.Target))
                        return link.Target;
                    return ResolveWrapCandidate(origin, direction, scopeCandidates);

                case EUINavigationRule.Auto:
                    if (link.Target is not null && !ReferenceEquals(link.Target, origin) && IsEligible(link.Target))
                        return link.Target;
                    if (AllowSpatialFallback)
                    {
                        UIInteractableComponent? spatial = ResolveSpatialCandidate(origin, direction, scopeCandidates);
                        if (spatial is not null)
                            return spatial;
                    }
                    if (ShouldWrapDirection(direction))
                        return ResolveWrapCandidate(origin, direction, scopeCandidates);
                    return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Returns true when <paramref name="candidate"/> can currently receive gamepad focus.
    /// </summary>
    public static bool IsEligible(UIInteractableComponent? candidate)
    {
        if (candidate is null || candidate.IsDestroyed || !candidate.IsActive || !candidate.NavigationFlow.AllowGamepadFocus)
            return false;

        SceneNode? node = candidate.SceneNode;
        if (node is null)
            return false;

        for (SceneNode? current = node; current is not null; current = current.Parent)
        {
            if (current.IsDestroyed || !current.IsActiveSelf)
                return false;
        }

        return candidate.Transform is not UIBoundableTransform boundable || boundable.IsVisibleInHierarchy;
    }

    private UINavigationLink ResolveLinkForNode(UIInteractableComponent source, EUINavigationDirection direction)
    {
        // Direct graph edge takes highest priority so a UI flow-map editor overrides per-node defaults cleanly.
        for (int i = 0; i < Edges.Count; i++)
        {
            UINavigationEdge edge = Edges[i];
            if (ReferenceEquals(edge.Source, source) && edge.Direction == direction)
                return new UINavigationLink { Rule = edge.Rule, Target = edge.Target };
        }

        // Reverse traversal for bidirectional explicit edges when no direct override exists.
        EUINavigationDirection opposite = UINavigationDirectionHelper.GetOpposite(direction);
        if (opposite != EUINavigationDirection.None)
        {
            for (int i = 0; i < Edges.Count; i++)
            {
                UINavigationEdge edge = Edges[i];
                if (edge.Bidirectional
                    && edge.Rule == EUINavigationRule.Explicit
                    && ReferenceEquals(edge.Target, source)
                    && edge.Direction == opposite
                    && edge.Source is not null)
                {
                    return UINavigationLink.Explicit(edge.Source);
                }
            }
        }

        return source.NavigationFlow.GetLink(direction);
    }

    private bool ShouldWrapDirection(EUINavigationDirection direction)
        => direction switch
        {
            EUINavigationDirection.Up or EUINavigationDirection.Down => WrapVertical,
            EUINavigationDirection.Left or EUINavigationDirection.Right => WrapHorizontal,
            _ => false,
        };

    private static UIInteractableComponent? ResolveSpatialCandidate(
        UIInteractableComponent origin,
        EUINavigationDirection direction,
        IReadOnlyList<UIInteractableComponent>? scopeCandidates)
    {
        if (scopeCandidates is null || scopeCandidates.Count == 0)
            return null;

        Vector2 dir = UINavigationDirectionHelper.ToCanvasVector(direction);
        if (dir == Vector2.Zero)
            return null;

        Vector2 originCenter = GetCenter(origin);
        UIInteractableComponent? best = null;
        float bestScore = float.PositiveInfinity;

        for (int i = 0; i < scopeCandidates.Count; i++)
        {
            UIInteractableComponent candidate = scopeCandidates[i];
            if (ReferenceEquals(candidate, origin) || !IsEligible(candidate))
                continue;

            Vector2 delta = GetCenter(candidate) - originCenter;
            float distSq = delta.LengthSquared();
            if (distSq <= 1.0e-5f)
                continue;

            float along = Vector2.Dot(delta, dir);
            if (along <= 1.0e-3f)
                continue;

            float dist = MathF.Sqrt(distSq);
            float cosine = along / dist;
            if (cosine < SpatialDirectionMinCosine)
                continue;

            float perp = MathF.Sqrt(MathF.Max(0.0f, distSq - along * along));
            // Penalize lateral misalignment more strongly than forward distance so columns and rows stay predictable.
            float score = along + perp * 2.5f;
            if (score < bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }

        return best;
    }

    private static UIInteractableComponent? ResolveWrapCandidate(
        UIInteractableComponent origin,
        EUINavigationDirection direction,
        IReadOnlyList<UIInteractableComponent>? scopeCandidates)
    {
        if (scopeCandidates is null || scopeCandidates.Count == 0)
            return null;

        Vector2 dir = UINavigationDirectionHelper.ToCanvasVector(direction);
        if (dir == Vector2.Zero)
            return null;

        Vector2 originCenter = GetCenter(origin);
        UIInteractableComponent? best = null;
        float bestProjection = float.PositiveInfinity;
        float bestPerp = float.PositiveInfinity;

        // Pick the eligible candidate farthest behind the travel direction (opposite edge along the axis).
        for (int i = 0; i < scopeCandidates.Count; i++)
        {
            UIInteractableComponent candidate = scopeCandidates[i];
            if (ReferenceEquals(candidate, origin) || !IsEligible(candidate))
                continue;

            Vector2 delta = GetCenter(candidate) - originCenter;
            float projection = Vector2.Dot(delta, dir);
            if (projection >= -1.0e-3f)
                continue;

            float perpSq = MathF.Max(0.0f, delta.LengthSquared() - projection * projection);
            float perp = MathF.Sqrt(perpSq);
            if (projection < bestProjection - 1.0f || (MathF.Abs(projection - bestProjection) <= 1.0f && perp < bestPerp))
            {
                bestProjection = projection;
                bestPerp = perp;
                best = candidate;
            }
        }

        return best;
    }

    private static Vector2 GetCenter(UIInteractableComponent component)
    {
        if (component.Transform is UIBoundableTransform tfm)
            return tfm.LocalToCanvas(tfm.ActualSize * 0.5f);

        return Vector2.Zero;
    }
}

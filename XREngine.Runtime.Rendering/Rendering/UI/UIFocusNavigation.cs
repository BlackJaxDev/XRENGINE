using System.Numerics;
using XREngine.Rendering.UI.State;

namespace XREngine.Rendering.UI;

/// <summary>Selects focus targets from one frozen input generation.</summary>
public static class UIFocusNavigation
{
    /// <summary>Finds the next tab stop by authored index, tree order, and stable membership.</summary>
    public static int FindTabTarget(ReadOnlySpan<UIHitRecord> records, long currentMembership, bool reverse,
        UIElementIdentity? scope = null)
        => FindTabTargetCore(new UIHitRecordView(records), currentMembership, reverse, scope);

    /// <summary>Finds a tab stop in one retained publication.</summary>
    public static int FindTabTarget(UIPublication publication, long currentMembership, bool reverse,
        UIElementIdentity? scope = null)
        => FindTabTargetCore(new UIHitRecordView(publication), currentMembership, reverse, scope);

    private static int FindTabTargetCore(UIHitRecordView records, long currentMembership,
        bool reverse, UIElementIdentity? scope)
    {
        int current = FindCurrent(ref records, currentMembership);
        int best = -1;
        int wrap = -1;
        for (int index = 0; index < records.Length; index++)
        {
            UIHitRecord record = records[index];
            if (!CanFocus(record) || scope.HasValue && record.FocusScopeIdentity != scope.Value)
                continue;
            if (wrap < 0 || Compare(record, records[wrap]) * (reverse ? -1 : 1) < 0)
                wrap = index;
            if (current >= 0 && Compare(record, records[current]) * (reverse ? -1 : 1) <= 0)
                continue;
            if (best < 0 || Compare(record, records[best]) * (reverse ? -1 : 1) < 0)
                best = index;
        }
        return best >= 0 ? best : wrap;
    }

    /// <summary>Finds the nearest focus target in a directional half-plane.</summary>
    public static int FindDirectionalTarget(ReadOnlySpan<UIHitRecord> records, long currentMembership, Vector2 direction,
        UIElementIdentity? scope = null)
        => FindDirectionalTargetCore(new UIHitRecordView(records), currentMembership, direction, scope);

    /// <summary>Finds a directional target in one retained publication.</summary>
    public static int FindDirectionalTarget(UIPublication publication, long currentMembership,
        Vector2 direction, UIElementIdentity? scope = null)
        => FindDirectionalTargetCore(new UIHitRecordView(publication), currentMembership, direction, scope);

    private static int FindDirectionalTargetCore(UIHitRecordView records, long currentMembership,
        Vector2 direction, UIElementIdentity? scope)
    {
        if (!float.IsFinite(direction.X) || !float.IsFinite(direction.Y) || direction.LengthSquared() == 0)
            return -1;
        int current = FindCurrent(ref records, currentMembership);
        if (current < 0)
            return FindTabTargetCore(records, currentMembership, false, scope);
        direction = Vector2.Normalize(direction);
        Vector2 origin = Center(records[current]);
        float bestScore = float.PositiveInfinity;
        int best = -1;
        for (int index = 0; index < records.Length; index++)
        {
            if (index == current || !CanFocus(records[index]) ||
                scope.HasValue && records[index].FocusScopeIdentity != scope.Value)
                continue;
            Vector2 delta = Center(records[index]) - origin;
            float forward = Vector2.Dot(delta, direction);
            if (forward <= 0)
                continue;
            float lateral = MathF.Abs(delta.X * direction.Y - delta.Y * direction.X);
            float score = forward + lateral * 3;
            if (score < bestScore || score == bestScore && (best < 0 || Compare(records[index], records[best]) < 0))
            {
                best = index;
                bestScore = score;
            }
        }
        return best;
    }

    private static bool CanFocus(in UIHitRecord record)
        => record.IsInteractable && record.IsTabStop && record.TabIndex >= 0 &&
            record.Size.X > 0 && record.Size.Y > 0 &&
            float.IsFinite(record.Size.X) && float.IsFinite(record.Size.Y) &&
            float.IsFinite(record.Bounds.MinX) && float.IsFinite(record.Bounds.MinY) &&
            float.IsFinite(record.Bounds.MaxX) && float.IsFinite(record.Bounds.MaxY) &&
            (record.Clip is not { } clip ||
                clip.Width > 0 && clip.Height > 0 && record.Bounds.MaxX > clip.MinX &&
                record.Bounds.MaxY > clip.MinY && record.Bounds.MinX < clip.MaxX &&
                record.Bounds.MinY < clip.MaxY);

    private static int FindCurrent(ref UIHitRecordView records, long membership)
    {
        for (int index = 0; index < records.Length; index++)
            if (records[index].MembershipId == membership)
                return index;
        return -1;
    }

    private static int Compare(in UIHitRecord left, in UIHitRecord right)
    {
        int comparison = left.TabIndex.CompareTo(right.TabIndex);
        if (comparison == 0)
            comparison = left.NodeIndex.CompareTo(right.NodeIndex);
        return comparison != 0 ? comparison : left.MembershipId.CompareTo(right.MembershipId);
    }

    private static Vector2 Center(in UIHitRecord record)
        => new(record.Bounds.MinX + record.Bounds.Width * 0.5f,
            record.Bounds.MinY + record.Bounds.Height * 0.5f);
}

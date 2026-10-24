using System.Collections;
using System.Numerics;
using XREngine.Data.Colors;
using XREngine.Scene;

namespace XREngine.Editor.UI;

/// <summary>Applies one exact BCL list slot change after a caller checks ownership.</summary>
internal static class EditorListSequenceKernel
{
    public static object?[] Snapshot(IList list)
    {
        object?[] values = new object?[list.Count];
        for (int index = 0; index < values.Length; index++)
            values[index] = list[index];
        return values;
    }

    public static object?[] Stage(object?[] before, EEditorListMutation operation, int index, object? value)
    {
        List<object?> after = [.. before];
        switch (operation)
        {
            case EEditorListMutation.Set: after[index] = value; break;
            case EEditorListMutation.Insert: after.Insert(index, value); break;
            case EEditorListMutation.Remove: after.RemoveAt(index); break;
        }
        return [.. after];
    }

    public static void Mutate(IList list, EEditorListMutation operation, int index,
        object?[] before, object?[] after, bool toApplied)
    {
        switch (operation)
        {
            case EEditorListMutation.Set:
                list[index] = toApplied ? after[index] : before[index];
                break;
            case EEditorListMutation.Insert:
                if (toApplied) list.Insert(index, after[index]);
                else list.RemoveAt(index);
                break;
            case EEditorListMutation.Remove:
                if (toApplied) list.RemoveAt(index);
                else list.Insert(index, before[index]);
                break;
        }
        if (!Matches(list, toApplied ? after : before))
            throw new InvalidOperationException("The list changed during the edit.");
    }

    public static bool Matches(IList list, object?[] expected)
    {
        if (list.Count != expected.Length)
            return false;
        for (int index = 0; index < expected.Length; index++)
            if (!SameValue(list[index], expected[index]))
                return false;
        return true;
    }

    public static bool SameSequence(object?[] first, object?[] second)
    {
        if (first.Length != second.Length) return false;
        for (int index = 0; index < first.Length; index++)
            if (!SameValue(first[index], second[index])) return false;
        return true;
    }

    public static bool SameValue(object? actual, object? expected)
    {
        if (ReferenceEquals(actual, expected)) return true;
        if (actual is null || expected is null || actual.GetType() != expected.GetType()) return false;
        if (actual is Vector2 v2 && expected is Vector2 other2)
            return v2.X.Equals(other2.X) && v2.Y.Equals(other2.Y);
        if (actual is Vector3 v3 && expected is Vector3 other3)
            return v3.X.Equals(other3.X) && v3.Y.Equals(other3.Y) && v3.Z.Equals(other3.Z);
        if (actual is Vector4 v4 && expected is Vector4 other4)
            return v4.X.Equals(other4.X) && v4.Y.Equals(other4.Y) &&
                v4.Z.Equals(other4.Z) && v4.W.Equals(other4.W);
        if (actual is Quaternion q && expected is Quaternion otherQ)
            return q.X.Equals(otherQ.X) && q.Y.Equals(otherQ.Y) &&
                q.Z.Equals(otherQ.Z) && q.W.Equals(otherQ.W);
        if (actual is ColorF3 c3 && expected is ColorF3 otherC3)
            return c3.R.Equals(otherC3.R) && c3.G.Equals(otherC3.G) && c3.B.Equals(otherC3.B);
        if (actual is ColorF4 c4 && expected is ColorF4 otherC4)
            return c4.R.Equals(otherC4.R) && c4.G.Equals(otherC4.G) &&
                c4.B.Equals(otherC4.B) && c4.A.Equals(otherC4.A);
        if (actual is LayerMask mask && expected is LayerMask otherMask)
            return mask.Value == otherMask.Value;
        if (actual is System.Drawing.Color color && expected is System.Drawing.Color otherColor)
            return color.ToArgb() == otherColor.ToArgb();
        Type type = actual.GetType();
        return (type.IsPrimitive || type.IsEnum || type == typeof(string) ||
            type == typeof(decimal) || type == typeof(DateTime) || type == typeof(Guid)) &&
            Equals(actual, expected);
    }
}

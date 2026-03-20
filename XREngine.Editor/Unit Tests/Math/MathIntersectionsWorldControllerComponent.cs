using System.Numerics;
using XREngine.Components;
using XREngine.Data.Geometry;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

public sealed class MathIntersectionsWorldControllerComponent : XRComponent
{
    private readonly List<MathIntersectionsWorldTestEntry> _tests = [];
    private CustomUIComponent? _customUi;

    public void RegisterTest(SceneNode rootNode, string displayName, AABB bounds)
    {
        var entry = new MathIntersectionsWorldTestEntry(rootNode, displayName, bounds, rootNode.GetTransformAs<Transform>(true)!);
        _tests.Add(entry);

        if (IsActiveInHierarchy)
        {
            RebuildUi();
            Relayout();
        }
    }

    protected override void OnComponentActivated()
    {
        base.OnComponentActivated();
        _customUi = GetSiblingComponent<CustomUIComponent>(createIfNotExist: true);
        if (_customUi is not null)
        {
            _customUi.Name = "Math Intersections Test Controls";
            RebuildUi();
        }

        Relayout();
    }

    protected override void OnComponentDeactivated()
    {
        _customUi?.ClearFields();
        _customUi = null;
        base.OnComponentDeactivated();
    }

    private void RebuildUi()
    {
        if (_customUi is null)
            return;

        _customUi.ClearFields();

        foreach (MathIntersectionsWorldTestEntry entry in _tests)
        {
            MathIntersectionsWorldTestEntry capturedEntry = entry;
            _customUi.AddBoolField(
                capturedEntry.DisplayName,
                () => capturedEntry.RootNode.IsActiveSelf,
                value => SetTestEnabled(capturedEntry, value),
                $"Toggle the {capturedEntry.DisplayName} math intersection test.");
        }
    }

    private void SetTestEnabled(MathIntersectionsWorldTestEntry entry, bool enabled)
    {
        entry.RootNode.IsActiveSelf = enabled;
        Relayout();
    }

    private void Relayout()
    {
        if (_tests.Count == 0)
            return;

        List<MathIntersectionsWorldTestEntry> activeTests = [];
        foreach (MathIntersectionsWorldTestEntry entry in _tests)
        {
            if (entry.RootNode.IsActiveSelf)
                activeTests.Add(entry);
        }

        if (activeTests.Count == 0)
            return;

        if (activeTests.Count == 1)
        {
            CenterAtOrigin(activeTests[0]);
            return;
        }

        float cellWidth = 0.0f;
        float cellDepth = 0.0f;
        foreach (MathIntersectionsWorldTestEntry entry in activeTests)
        {
            Vector3 size = entry.Bounds.Size;
            cellWidth = MathF.Max(cellWidth, size.X);
            cellDepth = MathF.Max(cellDepth, size.Z);
        }

        const float cellPadding = 4.0f;
        cellWidth += cellPadding;
        cellDepth += cellPadding;

        int columns = (int)MathF.Ceiling(MathF.Sqrt(activeTests.Count));
        int rows = (int)MathF.Ceiling(activeTests.Count / (float)columns);
        float startX = -((columns - 1) * cellWidth) * 0.5f;
        float startZ = -((rows - 1) * cellDepth) * 0.5f;

        for (int index = 0; index < activeTests.Count; index++)
        {
            int row = index / columns;
            int column = index % columns;
            Vector3 desiredCenter = new(
                startX + column * cellWidth,
                0.0f,
                startZ + row * cellDepth);

            CenterWithinCell(activeTests[index], desiredCenter);
        }
    }

    private static void CenterAtOrigin(MathIntersectionsWorldTestEntry entry)
        => CenterWithinCell(entry, Vector3.Zero);

    private static void CenterWithinCell(MathIntersectionsWorldTestEntry entry, Vector3 desiredCenter)
    {
        Vector3 boundsCenter = entry.Bounds.Center;
        entry.RootTransform.Translation = new Vector3(
            desiredCenter.X - boundsCenter.X,
            0.0f,
            desiredCenter.Z - boundsCenter.Z);
    }

    private sealed record MathIntersectionsWorldTestEntry(SceneNode RootNode, string DisplayName, AABB Bounds, Transform RootTransform);
}
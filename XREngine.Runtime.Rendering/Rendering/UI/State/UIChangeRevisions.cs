namespace XREngine.Rendering.UI.State;

/// <summary>Captures independent element revisions. Sample on the serialized UI owner.</summary>
public readonly record struct UIChangeRevisions(
    long Measure, long Arrange, long Transform, long Visual,
    long Clip, long HitTest, long Topology, long Text);

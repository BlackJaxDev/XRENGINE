namespace XREngine.Rendering;

/// <summary>
/// Describes how a multi-view pipeline instance renders its views, for tools that list pipeline instances.
/// </summary>
/// <param name="Name">A short name of the setup, for example <c>Both eyes</c> or <c>Separate eye</c>.</param>
/// <param name="ViewCount">The number of views that one execution renders.</param>
/// <param name="EyeIndex">The eye that a separate-eye instance renders, or null when the instance renders more than one view.</param>
public readonly record struct RenderPipelineViewSetup(string Name, uint ViewCount, uint? EyeIndex);

using System.Reflection;
using NUnit.Framework;
using XREngine.Editor;
using XREngine.Editor.UI;

namespace XREngine.UnitTests.Editor;

/// <summary>Checks preview refresh, mixed values, and retired row callbacks.</summary>
[TestFixture, NonParallelizable]
public sealed class InspectorStringPreviewBindingTests
{
    private static readonly PropertyInfo ValueProperty = typeof(InspectorPreviewTarget)
        .GetProperty(nameof(InspectorPreviewTarget.Value))!;

    [Test]
    public void ChangeRefreshesCommonValueAndRetiredRowIgnoresQueuedNotification()
    {
        InspectorPreviewTarget first = new() { Value = "First" };
        InspectorPreviewTarget second = new() { Value = "Second" };
        List<string> values = [];
        using InspectorStringPreviewBinding binding = new(ValueProperty, [first, second], values.Add);
        binding.Refresh();
        Assert.That(values, Is.EqualTo(new[] { "Multiple values" }));
        first.Value = "Same";
        second.Value = "Same";
        Drain();
        Assert.That(values, Is.EqualTo(new[] { "Multiple values", "Same" }));
        first.Value = "Recycled";
        binding.Dispose();
        Drain();
        Assert.That(values.Count, Is.EqualTo(2));
    }

    [Test]
    public void ReentrantRetirementDuringGetterDoesNotPublish()
    {
        InspectorPreviewTarget target = new() { Value = "Retired" };
        List<string> values = [];
        using InspectorStringPreviewBinding binding = new(ValueProperty, [target], values.Add);
        target.OnRead = binding.Dispose;
        binding.Refresh();
        Assert.That(values, Is.Empty);
    }

    [Test]
    public void PreviewStopsAtLineAndDoesNotSplitTextElements()
    {
        string unit = "\U0001F600\u0301";
        InspectorPreviewTarget target = new() { Value = string.Concat(Enumerable.Repeat(unit, 57)) };
        string? preview = null;
        using InspectorStringPreviewBinding binding = new(ValueProperty, [target], value => preview = value);
        binding.Refresh();
        Assert.That(preview, Is.EqualTo(string.Concat(Enumerable.Repeat(unit, 56)) + "…"));
        target.Value = "First\nSecond";
        Drain();
        Assert.That(preview, Is.EqualTo("First"));
    }

    private static void Drain() => typeof(EditorImGuiUI)
        .GetMethod("ProcessQueuedSceneEdits", BindingFlags.NonPublic | BindingFlags.Static)!
        .Invoke(null, null);
}

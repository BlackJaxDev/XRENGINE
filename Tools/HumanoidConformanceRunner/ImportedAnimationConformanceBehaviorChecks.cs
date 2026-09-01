using System.Numerics;
using XREngine.Animation;
using XREngine.Animation.Importers;
using XREngine.Components.Animation;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace HumanoidConformanceRunner;

/// <summary>
/// Executes the public imported-animation playback path against the explicit
/// conformance probe. It deliberately does not inspect private animation state or
/// invoke adapter methods directly.
/// </summary>
internal static class ImportedAnimationConformanceBehaviorChecks
{
    public static ImportedAnimationConformanceBehaviorCheckResult ImportAndEvaluate(
        string clipPath,
        SceneNode animatedRoot,
        ImportedAnimationConformanceBehaviorCheckOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clipPath);
        ArgumentNullException.ThrowIfNull(animatedRoot);
        return Evaluate(AnimYamlImporter.Import(clipPath), animatedRoot, options);
    }

    public static ImportedAnimationConformanceBehaviorCheckResult Evaluate(
        AnimationClip clip,
        SceneNode animatedRoot,
        ImportedAnimationConformanceBehaviorCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(animatedRoot);

        EnsureExplicitBindingNodes(clip, animatedRoot);
        var probe = animatedRoot.GetComponent<ImportedAnimationConformanceProbeComponent>()
            ?? animatedRoot.AddComponent<ImportedAnimationConformanceProbeComponent>()
            ?? throw new InvalidOperationException("Could not attach the imported-animation conformance probe.");
        var component = animatedRoot.AddComponent<AnimationClipComponent>()
            ?? throw new InvalidOperationException("Could not attach an AnimationClipComponent for the conformance probe.");
        component.Animation = clip;
        return Evaluate(component, probe, options);
    }

    public static ImportedAnimationConformanceBehaviorCheckResult Evaluate(
        AnimationClipComponent component,
        ImportedAnimationConformanceProbeComponent probe,
        ImportedAnimationConformanceBehaviorCheckOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(component);
        ArgumentNullException.ThrowIfNull(probe);
        AnimationClip clip = component.Animation
            ?? throw new InvalidOperationException("The AnimationClipComponent has no imported clip assigned.");
        options ??= new ImportedAnimationConformanceBehaviorCheckOptions();

        var result = new ImportedAnimationConformanceBehaviorCheckResult
        {
            ImportedEventCount = clip.ImportedEvents.Length,
        };
        CountContractBindings(clip, probe, result);

        float duration = Math.Max(clip.LengthInSeconds, 0.0001f);
        probe.ClearObservations();
        result.ObservedTransformChange = EvaluatePropertyTracks(component, duration);
        CaptureProbeReadback(probe, result);
        ValidatePropertyReadback(options, result);

        probe.ClearObservations();
        (ImportedAnimationEventBuffer expectedForward, ImportedAnimationEventBuffer expectedReverse) =
            EvaluateEvents(component, clip, duration);
        CaptureEventReadback(probe, result);
        ValidateEvents(clip, options, expectedForward, expectedReverse, result);
        ValidateSourceEncoding(clip, options, result);
        return result;
    }

    private static void CountContractBindings(
        AnimationClip clip,
        ImportedAnimationConformanceProbeComponent probe,
        ImportedAnimationConformanceBehaviorCheckResult result)
    {
        foreach (ImportedAnimationBindingDescriptor binding in clip.ImportedGenericBindings)
        {
            if (!probe.CanBind(binding, out _))
                continue;
            if (binding.ValueKind == EImportedAnimationBindingValueKind.ObjectReference)
                result.ContractObjectReferenceBindingCount++;
            else
                result.ContractScalarBindingCount++;
        }
    }

    private static bool EvaluatePropertyTracks(AnimationClipComponent component, float duration)
    {
        component.EvaluateAtTime(0.0f);
        Dictionary<string, Matrix4x4> baseline = CaptureLocalMatrices(component.SceneNode);
        float[] probes = [duration * 0.25f, duration * 0.5f, duration * 0.75f, duration];
        bool changed = false;
        for (int i = 0; i < probes.Length; i++)
        {
            component.EvaluateAtTime(probes[i]);
            changed |= !LocalMatricesMatch(baseline, CaptureLocalMatrices(component.SceneNode));
        }
        return changed;
    }

    private static (ImportedAnimationEventBuffer Forward, ImportedAnimationEventBuffer Reverse) EvaluateEvents(
        AnimationClipComponent component,
        AnimationClip clip,
        float duration)
    {
        // Establish a deterministic source clock without delivery, then traverse it forward
        // and backward through more than one period. The component itself owns all wrapping,
        // cycle numbering, ordering, and event dispatch.
        const double forwardStart = -1.0;
        const double forwardEnd = 2.0;
        const double reverseEnd = -2.0;
        var expectedForward = new ImportedAnimationEventBuffer();
        var expectedReverse = new ImportedAnimationEventBuffer();
        clip.CollectImportedAnimationEvents(expectedForward, forwardStart * duration, forwardEnd * duration, includePrevious: false);
        clip.CollectImportedAnimationEvents(expectedReverse, forwardEnd * duration, reverseEnd * duration, includePrevious: false);
        component.EvaluateAtUnwrappedTime(forwardStart * duration, dispatchEvents: false);
        component.EvaluateAtUnwrappedTime(forwardEnd * duration, dispatchEvents: true);
        component.EvaluateAtUnwrappedTime(reverseEnd * duration, dispatchEvents: true);
        return (expectedForward, expectedReverse);
    }

    private static void CaptureProbeReadback(
        ImportedAnimationConformanceProbeComponent probe,
        ImportedAnimationConformanceBehaviorCheckResult result)
    {
        result.ScalarWriteCount = probe.ScalarWriteCount;
        result.ObjectReferenceWriteCount = probe.ObjectReferenceWriteCount;
        result.ObservedNonNullObjectReference = probe.ObservedNonNullObjectReference;
        result.ObservedNullObjectReference = probe.ObservedNullObjectReference;
        result.ScalarWrites = [.. probe.ScalarWrites];
        result.ObjectReferenceWrites = [.. probe.ObjectReferenceWrites];
        int nonNullIndex = result.ObjectReferenceWrites.FindIndex(static value => !value.IsNull);
        result.ObservedNonNullThenNullObjectReference = nonNullIndex >= 0
            && result.ObjectReferenceWrites.Skip(nonNullIndex + 1).Any(static value => value.IsNull);
    }

    private static void CaptureEventReadback(
        ImportedAnimationConformanceProbeComponent probe,
        ImportedAnimationConformanceBehaviorCheckResult result)
    {
        result.Events = [.. probe.Events];
        result.ObservedForwardEvent = result.Events.Any(static x => !x.Reverse);
        result.ObservedReverseEvent = result.Events.Any(static x => x.Reverse);
    }

    private static void ValidatePropertyReadback(
        ImportedAnimationConformanceBehaviorCheckOptions options,
        ImportedAnimationConformanceBehaviorCheckResult result)
    {
        if (options.RequireScalarWrite && result.ContractScalarBindingCount == 0)
            result.Failures.Add("No reserved conformance scalar binding was imported.");
        if (options.RequireScalarWrite && result.ScalarWriteCount == 0)
            result.Failures.Add("The reserved conformance scalar binding was not written by runtime evaluation.");

        if (options.RequireObjectReferenceTransition && result.ContractObjectReferenceBindingCount == 0)
            result.Failures.Add("No reserved conformance object-reference binding was imported.");
        if (options.RequireObjectReferenceTransition && result.ObjectReferenceWriteCount == 0)
            result.Failures.Add("The reserved conformance object-reference binding was not written by runtime evaluation.");
        if (options.RequireObjectReferenceTransition && !result.ObservedNonNullObjectReference)
            result.Failures.Add("Runtime evaluation did not write a non-null conformance object reference.");
        if (options.RequireObjectReferenceTransition && !result.ObservedNullObjectReference)
            result.Failures.Add("Runtime evaluation did not write a null conformance object reference after the non-null key.");
        if (options.RequireObjectReferenceTransition && !result.ObservedNonNullThenNullObjectReference)
            result.Failures.Add("Runtime evaluation did not observe the required non-null-to-null conformance object-reference transition.");
    }

    private static void ValidateEvents(
        AnimationClip clip,
        ImportedAnimationConformanceBehaviorCheckOptions options,
        ImportedAnimationEventBuffer expectedForward,
        ImportedAnimationEventBuffer expectedReverse,
        ImportedAnimationConformanceBehaviorCheckResult result)
    {
        if (options.RequireEvents && clip.ImportedEvents.Length == 0)
            result.Failures.Add("No allowlisted imported events were available for runtime dispatch.");
        if (options.RequireEvents && result.Events.Count == 0)
            result.Failures.Add("No imported events reached the typed conformance receiver.");
        if (options.RequireEvents && !result.ObservedForwardEvent)
            result.Failures.Add("No forward imported event occurrence was observed.");
        if (options.RequireEvents && !result.ObservedReverseEvent)
            result.Failures.Add("No reverse imported event occurrence was observed.");

        result.ForwardEventPayloadsMatch = ValidateEventSequence(
            expectedForward.Items,
            result.Events.Where(static x => !x.Reverse).ToArray(),
            "forward",
            result.Failures);
        result.ReverseEventPayloadsMatch = ValidateEventSequence(
            expectedReverse.Items,
            result.Events.Where(static x => x.Reverse).ToArray(),
            "reverse",
            result.Failures);

        foreach (string expectedEventId in options.ExpectedEventIds)
        {
            if (!result.Events.Any(x => string.Equals(x.EventId, expectedEventId, StringComparison.Ordinal)))
                result.Failures.Add($"Expected imported event '{expectedEventId}' was not dispatched.");
        }
    }

    private static bool ValidateEventSequence(
        ReadOnlySpan<ImportedAnimationEventOccurrence> expected,
        IReadOnlyList<ImportedAnimationConformanceEventObservation> actual,
        string direction,
        List<string> failures)
    {
        if (expected.Length != actual.Count)
        {
            failures.Add($"{direction} event count expected={expected.Length} actual={actual.Count}.");
            return false;
        }

        for (int i = 0; i < expected.Length; i++)
        {
            ImportedAnimationEventOccurrence occurrence = expected[i];
            ImportedAnimationConformanceEventObservation observed = actual[i];
            ImportedAnimationEvent source = occurrence.Event;
            bool matches = string.Equals(source.EventId, observed.EventId, StringComparison.Ordinal)
                && MathF.Abs(source.Time - observed.EventTime) <= 0.000001f
                && string.Equals(source.StringParameter, observed.StringParameter, StringComparison.Ordinal)
                && MathF.Abs(source.FloatParameter - observed.FloatParameter) <= 0.000001f
                && source.IntParameter == observed.IntParameter
                && source.SourceOrder == observed.SourceOrder
                && source.ObjectReferenceParameter.Equals(observed.ObjectReferenceParameter)
                && source.MessageOptions == observed.MessageOptions
                && occurrence.LoopCycle == observed.LoopCycle
                && occurrence.Reverse == observed.Reverse;
            if (matches)
                continue;

            failures.Add(
                $"{direction} event[{i}] payload/order mismatch expected=" +
                $"id={source.EventId},time={source.Time:G9},string={source.StringParameter},float={source.FloatParameter:G9}," +
                $"int={source.IntParameter},sourceOrder={source.SourceOrder},options={source.MessageOptions},cycle={occurrence.LoopCycle},reverse={occurrence.Reverse}.");
            return false;
        }

        return true;
    }

    private static void ValidateSourceEncoding(
        AnimationClip clip,
        ImportedAnimationConformanceBehaviorCheckOptions options,
        ImportedAnimationConformanceBehaviorCheckResult result)
    {
        bool containsSourceEncoding = clip.SourceImportManifest?.Domains.Any(
            static x => x.Domain == EImportedAnimationDataDomain.SourceEncoding
                && x.AppliedItemCount > 0
                && x.State == EImportedAnimationCapabilityState.SupportedAndApplied) == true;
        result.ObservedSourceEncodingEvaluation = containsSourceEncoding
            && (result.ObservedTransformChange
                || result.ScalarWriteCount > 0
                || result.ObjectReferenceWriteCount > 0);
        if (options.RequireSourceEncodingEvaluation && !containsSourceEncoding)
            result.Failures.Add("The imported clip did not declare an applied source encoding.");
        if (options.RequireSourceEncodingEvaluation && !result.ObservedSourceEncodingEvaluation)
            result.Failures.Add("No reserved conformance track from an applied source encoding produced a runtime write.");
    }

    private static void EnsureExplicitBindingNodes(AnimationClip clip, SceneNode root)
    {
        foreach (ImportedAnimationBindingDescriptor binding in clip.ImportedGenericBindings)
        {
            if (binding.RequiresAdapter || string.IsNullOrWhiteSpace(binding.NodePath))
                continue;

            SceneNode current = root;
            string[] segments = binding.NodePath.Split(
                '/',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (int i = 0; i < segments.Length; i++)
            {
                SceneNode? child = current.Transform.Children
                    .Select(static transform => transform.SceneNode)
                    .FirstOrDefault(node => node is not null
                        && string.Equals(node.Name, segments[i], StringComparison.OrdinalIgnoreCase));
                current = child ?? new SceneNode(current, segments[i], new Transform());
            }
        }
    }

    private static Dictionary<string, Matrix4x4> CaptureLocalMatrices(SceneNode root)
    {
        var matrices = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
        CaptureLocalMatrices(root, string.Empty, matrices);
        return matrices;
    }

    private static void CaptureLocalMatrices(
        SceneNode node,
        string relativePath,
        Dictionary<string, Matrix4x4> matrices)
    {
        node.Transform.RecalculateMatrices(forceWorldRecalc: true);
        matrices.Add(relativePath, node.Transform.LocalMatrix);
        foreach (var childTransform in node.Transform.Children)
        {
            SceneNode? child = childTransform.SceneNode;
            if (child is null)
                continue;
            string childPath = relativePath.Length == 0
                ? child.Name ?? string.Empty
                : $"{relativePath}/{child.Name}";
            CaptureLocalMatrices(child, childPath, matrices);
        }
    }

    private static bool LocalMatricesMatch(
        IReadOnlyDictionary<string, Matrix4x4> left,
        IReadOnlyDictionary<string, Matrix4x4> right)
    {
        if (left.Count != right.Count)
            return false;
        foreach ((string path, Matrix4x4 expected) in left)
        {
            if (!right.TryGetValue(path, out Matrix4x4 actual) || !MatrixNearlyEquals(expected, actual))
                return false;
        }
        return true;
    }

    private static bool MatrixNearlyEquals(in Matrix4x4 left, in Matrix4x4 right)
    {
        const float epsilon = 1.0e-6f;
        return MathF.Abs(left.M11 - right.M11) <= epsilon
            && MathF.Abs(left.M12 - right.M12) <= epsilon
            && MathF.Abs(left.M13 - right.M13) <= epsilon
            && MathF.Abs(left.M14 - right.M14) <= epsilon
            && MathF.Abs(left.M21 - right.M21) <= epsilon
            && MathF.Abs(left.M22 - right.M22) <= epsilon
            && MathF.Abs(left.M23 - right.M23) <= epsilon
            && MathF.Abs(left.M24 - right.M24) <= epsilon
            && MathF.Abs(left.M31 - right.M31) <= epsilon
            && MathF.Abs(left.M32 - right.M32) <= epsilon
            && MathF.Abs(left.M33 - right.M33) <= epsilon
            && MathF.Abs(left.M34 - right.M34) <= epsilon
            && MathF.Abs(left.M41 - right.M41) <= epsilon
            && MathF.Abs(left.M42 - right.M42) <= epsilon
            && MathF.Abs(left.M43 - right.M43) <= epsilon
            && MathF.Abs(left.M44 - right.M44) <= epsilon;
    }
}

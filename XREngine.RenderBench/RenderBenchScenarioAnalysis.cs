using XREngine.Rendering.Vulkan;
using XREngine.Rendering.Occlusion;

namespace XREngine.RenderBench;

/// <summary>Evaluates cold diagnostic evidence and never feeds visibility back into rendering.</summary>
internal static class RenderBenchScenarioAnalysis
{
    private static readonly string[] VisibilityLanes = ["eligibility", "disabled", "hiz"];

    public static RenderBenchVisibilityAnalysisSummary? ValidateVisibility(
        IReadOnlyList<RenderBenchScenarioResult> lanes,
        List<string> failures)
    {
        if (!TryGetVisibilityCohort(lanes, failures, out RenderBenchScenarioResult eligibility,
                out RenderBenchScenarioResult reference, out RenderBenchScenarioResult production,
                out bool cohortComplete, out int analyzedFrameCount))
            return null;

        bool identityMatched = IdentitiesMatch(eligibility, reference, production);
        if (!identityMatched)
            failures.Add("Visibility controls have different input, executable, shader, or hardware identity evidence.");

        bool receiptProvenanceAvailable = ValidateLaneProvenance(eligibility, failures) &
            ValidateLaneProvenance(reference, failures) &
            ValidateLaneProvenance(production, failures);
        List<RenderBenchVisibilityFrameVerdict> verdicts = [];
        VisibilityCounts totals = default;
        int twoPassFrames = 0;
        int laterTwoPassFrames = 0;
        int heavyCandidateCulls = 0;

        for (int step = 0; step < analyzedFrameCount; step++)
        {
            RenderBenchVisibilityFrameVerdict verdict = ValidateVisibilityFrame(
                step, eligibility.Frames[step], reference.Frames[step], production.Frames[step]);
            verdicts.Add(verdict);
            totals.Add(verdict);
            heavyCandidateCulls += verdict.HeavyCandidateCullCount;
            if (verdict.TwoPassExecuted)
            {
                twoPassFrames++;
                if (step > 0)
                    laterTwoPassFrames++;
            }
            foreach (string failure in verdict.Failures)
                failures.Add($"{production.Depth}/step-{step}: {failure}");
        }

        if (laterTwoPassFrames == 0)
            failures.Add($"{production.Depth}: no later frame executed two-pass Hi-Z.");
        if (RenderBenchScenarioWorkloads.RequiresOcclusion(production.Workload) && totals.DemonstratedCullCount == 0)
            failures.Add($"{production.Depth}: no eligible hidden candidate was removed from K.");
        if (RenderBenchScenarioWorkloads.IsHeavy(production.Workload) && heavyCandidateCulls == 0)
            failures.Add($"{production.Depth}: no heavy candidate was removed from K after temporal history became valid.");

        return new()
        {
            Depth = production.Depth,
            Workload = production.Workload,
            FrameCount = verdicts.Count,
            CohortComplete = cohortComplete,
            EligibilityCount = totals.EligibilityCount,
            VisibleCount = totals.VisibleCount,
            OccludedCount = totals.OccludedCount,
            KeptCount = totals.KeptCount,
            RenderedCount = totals.RenderedCount,
            FalseOcclusionCount = totals.FalseOcclusionCount,
            MissingVisibleCount = totals.MissingVisibleCount,
            ConservativeOverdrawCount = totals.ConservativeOverdrawCount,
            DemonstratedCullCount = totals.DemonstratedCullCount,
            HeavyCandidateCullCount = heavyCandidateCulls,
            TwoPassFrameCount = twoPassFrames,
            LaterTwoPassFrameCount = laterTwoPassFrames,
            DeterministicIdentityMatched = identityMatched,
            ReceiptProvenanceAvailable = receiptProvenanceAvailable,
            Passed = cohortComplete && identityMatched && receiptProvenanceAvailable && verdicts.All(static verdict => verdict.Passed) &&
                laterTwoPassFrames > 0 &&
                (!RenderBenchScenarioWorkloads.RequiresOcclusion(production.Workload) || totals.DemonstratedCullCount > 0) &&
                (!RenderBenchScenarioWorkloads.IsHeavy(production.Workload) || heavyCandidateCulls > 0),
            Frames = [.. verdicts],
        };
    }

    public static RenderBenchColdRepeatAnalysisSummary ValidateColdRepeats(
        IReadOnlyList<RenderBenchScenarioResult> results,
        List<string> failures)
    {
        RenderBenchScenarioResult[] comparable = results.Where(static result => result.Lane != "buffers").ToArray();
        if (comparable.Length == 0)
        {
            return new()
            {
                Applicable = false,
                Status = "not-applicable",
                Passed = false,
            };
        }
        Dictionary<(string Depth, string Workload), int> expectedRepeatsByCohort = comparable
            .GroupBy(static result => (result.Depth, result.Workload))
            .ToDictionary(static group => group.Key, static group => group.Count());
        int comparedLanes = 0;
        int comparedFrames = 0;
        int mismatchedFrames = 0;
        bool identityMatched = true;
        bool complete = expectedRepeatsByCohort.Count != 0;

        foreach (IGrouping<(string Depth, string Workload, string Lane), RenderBenchScenarioResult> group in comparable
                     .GroupBy(static result => (result.Depth, result.Workload, result.Lane)))
        {
            RenderBenchScenarioResult[] repeats = group.ToArray();
            int expectedRepeats = expectedRepeatsByCohort[(group.Key.Depth, group.Key.Workload)] / VisibilityLanes.Length;
            if (expectedRepeats < 2 || repeats.Length != expectedRepeats)
            {
                complete = false;
                failures.Add($"{group.Key}: expected {expectedRepeats} cold repeats, found {repeats.Length}.");
                continue;
            }
            if (repeats.Any(static repeat => repeat.Status != "passed"))
            {
                complete = false;
                failures.Add($"{group.Key}: one or more cold repeats did not pass.");
                continue;
            }

            bool provenanceValid = true;
            foreach (RenderBenchScenarioResult repeat in repeats)
                provenanceValid &= ValidateLaneProvenance(repeat, failures);
            if (!provenanceValid)
            {
                complete = false;
                failures.Add($"{group.Key}: cold-repeat provenance is incomplete.");
                continue;
            }

            comparedLanes++;
            RenderBenchScenarioResult baseline = repeats[0];
            foreach (RenderBenchScenarioResult repeat in repeats.Skip(1))
            {
                if (!IdentitiesMatch(baseline, repeat))
                {
                    identityMatched = false;
                    failures.Add($"{group.Key}: repeat input, executable, shader, or hardware identity evidence differs.");
                }
                if (repeat.Frames.Length != baseline.Frames.Length)
                {
                    complete = false;
                    failures.Add($"{group.Key}: cold repeat frame counts differ.");
                    continue;
                }
                for (int step = 0; step < baseline.Frames.Length; step++)
                {
                    comparedFrames++;
                    if (!FramesMatch(baseline.Frames[step], repeat.Frames[step]))
                    {
                        mismatchedFrames++;
                        failures.Add($"{group.Key}/step-{step}: cold-repeat image or candidate sets differ.");
                    }
                }
            }
        }

        return new()
        {
            Applicable = true,
            Status = complete && identityMatched && mismatchedFrames == 0 ? "passed" : "failed",
            ComparedLaneCount = comparedLanes,
            ComparedFrameCount = comparedFrames,
            MismatchedFrameCount = mismatchedFrames,
            IdentityMatched = identityMatched,
            Passed = complete && identityMatched && mismatchedFrames == 0,
        };
    }

    private static bool TryGetVisibilityCohort(
        IReadOnlyList<RenderBenchScenarioResult> lanes,
        List<string> failures,
        out RenderBenchScenarioResult eligibility,
        out RenderBenchScenarioResult reference,
        out RenderBenchScenarioResult production,
        out bool cohortComplete,
        out int analyzedFrameCount)
    {
        eligibility = null!;
        reference = null!;
        production = null!;
        cohortComplete = false;
        analyzedFrameCount = 0;
        if (lanes.Count != VisibilityLanes.Length ||
            lanes.Select(static lane => lane.Lane).Distinct(StringComparer.Ordinal).Count() != VisibilityLanes.Length ||
            !VisibilityLanes.All(expected => lanes.Any(lane => lane.Lane == expected)))
        {
            failures.Add("Visibility cohort requires exactly one eligibility, disabled, and hiz lane.");
            return false;
        }

        eligibility = lanes.Single(static lane => lane.Lane == "eligibility");
        reference = lanes.Single(static lane => lane.Lane == "disabled");
        production = lanes.Single(static lane => lane.Lane == "hiz");
        foreach (RenderBenchScenarioResult lane in lanes.Where(static lane => lane.Status != "passed"))
            failures.Add($"{lane.Depth}/{lane.Lane}: {lane.Failure ?? "cohort incomplete"}");
        analyzedFrameCount = Math.Min(
            eligibility.Frames.Length,
            Math.Min(reference.Frames.Length, production.Frames.Length));
        if (analyzedFrameCount == 0)
        {
            failures.Add("Visibility cohort has no common completed frame prefix.");
            return false;
        }

        bool laneStatusesPassed = eligibility.Status == "passed" &&
            reference.Status == "passed" && production.Status == "passed";
        bool frameCountsAligned = eligibility.Frames.Length == reference.Frames.Length &&
            reference.Frames.Length == production.Frames.Length;
        cohortComplete = laneStatusesPassed && frameCountsAligned;
        if (!frameCountsAligned)
        {
            failures.Add(
                $"Visibility cohort frame counts differ: eligibility={eligibility.Frames.Length}, " +
                $"disabled={reference.Frames.Length}, hiz={production.Frames.Length}; " +
                $"analyzing common completed prefix of {analyzedFrameCount}.");
        }
        return true;
    }

    private static RenderBenchVisibilityFrameVerdict ValidateVisibilityFrame(
        int step,
        RenderBenchScenarioFrame eligibility,
        RenderBenchScenarioFrame reference,
        RenderBenchScenarioFrame production)
    {
        List<string> failures = [];
        HashSet<int> eligibilitySet = [.. eligibility.VisibleCandidateIds];
        HashSet<int> visibleSet = [.. reference.VisibleCandidateIds];
        HashSet<int> keptSet = [.. production.KeptCandidateIds];
        HashSet<int> renderedSet = [.. production.VisibleCandidateIds];
        HashSet<int> occludedSet = [.. eligibilitySet.Except(visibleSet)];
        HashSet<int> falseOcclusionSet = [.. visibleSet.Except(keptSet)];
        HashSet<int> missingVisibleSet = [.. visibleSet.Except(renderedSet)];
        HashSet<int> gpuKeptSet = [.. production.EarlyCandidateIds];
        gpuKeptSet.UnionWith(production.LateCandidateIds);
        HashSet<int> conservativeOverdrawSet = [.. keptSet.Intersect(occludedSet)];
        HashSet<int> demonstratedCullSet = [.. occludedSet.Except(keptSet)];
        HashSet<uint> earlyAllDrawIds = [.. production.EarlyDrawIds];
        HashSet<uint> submittedAllDrawIds = [.. production.EarlyDrawIds];
        submittedAllDrawIds.UnionWith(production.LateDrawIds);
        int heavyCandidateCullCount = 0;
        bool heavyCandidateStreamCoverageComplete = true;
        bool conservativeEarlyCoverage = !production.TemporalInvalidated ||
            earlyAllDrawIds.SetEquals(submittedAllDrawIds) &&
            earlyAllDrawIds.Count >= production.GpuCandidateCount;

        if (eligibility.Step != step || reference.Step != step || production.Step != step)
            failures.Add("step provenance does not align.");
        if (production.DiagnosticFailure is not null)
            failures.Add($"production diagnostics failed: {production.DiagnosticFailure}");
        if (eligibilitySet.Count == 0 || visibleSet.Count == 0)
            failures.Add("E and V must be nonempty.");
        if (RenderBenchScenarioWorkloads.RequiresOcclusion(production.Workload) && occludedSet.Count == 0)
            failures.Add("This workload requires a nonempty occluded reference set.");
        if (!visibleSet.IsSubsetOf(eligibilitySet))
            failures.Add("eligibility does not cover reference visibility.");
        if (!eligibilitySet.Contains(1) || !visibleSet.Contains(1) || !keptSet.Contains(1) || !renderedSet.Contains(1))
            failures.Add("visible sentinel 1 is absent from E/V/K/output.");
        if (falseOcclusionSet.Count != 0)
            failures.Add($"false occlusion [{FormatIds(falseOcclusionSet)}].");
        if (missingVisibleSet.Count != 0)
            failures.Add($"missing visible output [{FormatIds(missingVisibleSet)}].");
        ValidateDrawIdMappings("early", production.EarlyDrawIds, production.EarlyDrawMappings, failures);
        ValidateDrawIdMappings("late", production.LateDrawIds, production.LateDrawMappings, failures);
        ValidatePhaseSeparation(production, failures);
        if (production.TwoPassExecuted)
        {
            if (!gpuKeptSet.SetEquals(keptSet))
                failures.Add("K must equal the union of early and late candidate IDs.");
            if (production.GpuCandidateCount == 0 || production.GpuCandidateCount < gpuKeptSet.Count)
            {
                failures.Add(
                    $"GPU candidate count {production.GpuCandidateCount} cannot account for {gpuKeptSet.Count} kept candidates.");
            }
        }
        if (!conservativeEarlyCoverage)
            failures.Add("temporal invalidation did not retain every submitted candidate/known-occluder DrawID in the early stream.");
        ValidateMaskedCoverage(reference, production, visibleSet, failures);
        if (RenderBenchScenarioWorkloads.IsHeavy(production.Workload))
        {
            for (int candidateId = 7; candidateId <= 70; candidateId++)
            {
                if (!reference.KeptCandidateIds.Contains(candidateId))
                {
                    heavyCandidateStreamCoverageComplete = false;
                    failures.Add($"disabled stream omitted expected heavy candidate {candidateId}.");
                    continue;
                }
                if (production.TwoPassExecuted && !production.TemporalInvalidated && !keptSet.Contains(candidateId))
                    heavyCandidateCullCount++;
            }
        }

        return new()
        {
            Step = step,
            EligibilityCount = eligibilitySet.Count,
            VisibleCount = visibleSet.Count,
            OccludedCount = occludedSet.Count,
            KeptCount = keptSet.Count,
            RenderedCount = renderedSet.Count,
            EarlyCount = production.EarlyCandidateIds.Length,
            LateCount = production.LateCandidateIds.Length,
            FalseOcclusionCount = falseOcclusionSet.Count,
            MissingVisibleCount = missingVisibleSet.Count,
            ConservativeOverdrawCount = conservativeOverdrawSet.Count,
            DemonstratedCullCount = demonstratedCullSet.Count,
            HeavyCandidateCullCount = heavyCandidateCullCount,
            HeavyCandidateStreamCoverageComplete = heavyCandidateStreamCoverageComplete,
            TwoPassExecuted = production.TwoPassExecuted,
            TemporalInvalidated = production.TemporalInvalidated,
            ConservativeEarlyCoverageProven = conservativeEarlyCoverage,
            ReceiptProvenanceAvailable = production.Submission.IsValid,
            Passed = failures.Count == 0,
            Failures = [.. failures],
        };
    }

    private static void ValidateMaskedCoverage(
        RenderBenchScenarioFrame reference,
        RenderBenchScenarioFrame production,
        IReadOnlySet<int> visibleSet,
        List<string> failures)
    {
        if (!RenderBenchScenarioWorkloads.IsMasked(production.Workload))
            return;
        if (!string.Equals(reference.MaskedCoverageMode, production.MaskedCoverageMode, StringComparison.Ordinal))
        {
            failures.Add("masked coverage mode differs between disabled and Hi-Z lanes.");
            return;
        }

        switch (reference.MaskedCoverageMode)
        {
            case "cutout":
                if (!visibleSet.Contains(2))
                    failures.Add("the disabled cutout control did not reveal palette candidate 2 through the alpha-tested hole.");
                if (reference.MaskedBorderPixelCount < 4 || reference.MaskedHoleAdjacentTargetPixelCount == 0)
                    failures.Add("the disabled cutout control lacks adjacent raw-albedo border and palette-target pixels.");
                break;
            case "opaque-control":
                if (visibleSet.Contains(2))
                    failures.Add("the opaque masked-fixture control did not hide palette candidate 2.");
                if (reference.MaskedBorderPixelCount < 4)
                    failures.Add("the opaque masked-fixture control lacks raw-albedo panel border evidence.");
                break;
            default:
                failures.Add($"masked workload has unexpected coverage mode '{reference.MaskedCoverageMode}'.");
                break;
        }
    }

    private static bool ValidateLaneProvenance(RenderBenchScenarioResult lane, List<string> failures)
    {
        bool valid = ValidateResultSha256(lane, failures);
        HashSet<ulong> completedReceiptFrames = [.. lane.Frames
            .Where(static frame => frame.Submission.IsValid)
            .Select(static frame => frame.Submission.EngineFrameId)];
        ulong priorEngineFrameId = 0;
        ulong priorExplicitFrameNumber = 0;
        ulong priorTimelineSignal = 0;
        long priorCollectGeneration = 0;
        VulkanExplicitProductionSubmissionReceipt firstReceipt = default;
        for (int step = 0; step < lane.Frames.Length; step++)
        {
            RenderBenchScenarioFrame frame = lane.Frames[step];
            string prefix = $"{lane.Depth}/{lane.Lane}/step-{step}";
            if (!string.Equals(frame.Workload, lane.Workload, StringComparison.Ordinal))
            {
                valid = false;
                failures.Add($"{prefix}: frame workload '{frame.Workload}' does not match lane workload '{lane.Workload}'.");
            }
            if (!IsSha256(frame.ColorSha256))
            {
                valid = false;
                failures.Add($"{prefix}: color SHA-256 is missing or malformed.");
            }
            VulkanExplicitProductionSubmissionReceipt receipt = frame.Submission;
            if (!receipt.IsValid)
            {
                valid = false;
                failures.Add($"{prefix}: submission receipt is invalid.");
                continue;
            }
            if (!firstReceipt.IsValid)
                firstReceipt = receipt;
            else if (receipt.OwnerIdentity != firstReceipt.OwnerIdentity ||
                     receipt.BackendGeneration != firstReceipt.BackendGeneration ||
                     receipt.DeviceHandle != firstReceipt.DeviceHandle ||
                     receipt.TargetGeneration != firstReceipt.TargetGeneration)
            {
                valid = false;
                failures.Add($"{prefix}: submission owner, backend, device, or target provenance changed within one lane.");
            }
            if (receipt.EngineFrameId != frame.EngineFrameId)
            {
                valid = false;
                failures.Add($"{prefix}: receipt engine frame does not match frame provenance.");
            }
            if (priorEngineFrameId != 0 && receipt.EngineFrameId <= priorEngineFrameId)
            {
                valid = false;
                failures.Add($"{prefix}: engine frame IDs must increase.");
            }
            if (priorExplicitFrameNumber != 0 && receipt.ExplicitFrameNumber <= priorExplicitFrameNumber)
            {
                valid = false;
                failures.Add($"{prefix}: explicit frame numbers must increase.");
            }
            if (priorTimelineSignal != 0 && receipt.GraphicsTimelineSignal <= priorTimelineSignal)
            {
                valid = false;
                failures.Add($"{prefix}: graphics timeline signals must increase.");
            }
            if (priorCollectGeneration != 0 && frame.CollectGeneration <= priorCollectGeneration)
            {
                valid = false;
                failures.Add($"{prefix}: collect generations must increase.");
            }
            priorEngineFrameId = receipt.EngineFrameId;
            priorExplicitFrameNumber = receipt.ExplicitFrameNumber;
            priorTimelineSignal = receipt.GraphicsTimelineSignal;
            priorCollectGeneration = frame.CollectGeneration;
            if (frame.GpuTiming is { } timing)
            {
                valid &= ValidateGpuTimingSample(prefix + "/build", timing.Build, completedReceiptFrames, failures);
                valid &= ValidateGpuTimingSample(prefix + "/test", timing.Test, completedReceiptFrames, failures);
            }
        }
        return valid;
    }

    private static bool ValidateGpuTimingSample(
        string prefix,
        RenderBenchScenarioGpuTimingSample sample,
        IReadOnlySet<ulong> completedReceiptFrames,
        List<string> failures)
    {
        if (sample.SourceFrameId != 0 && !completedReceiptFrames.Contains(sample.SourceFrameId))
        {
            failures.Add($"{prefix}: delayed timestamp source frame {sample.SourceFrameId} has no completed receipt in this cohort.");
            return false;
        }

        if (sample.Availability != EOcclusionGpuElapsedAvailability.Ready)
            return true;
        if (sample.SourceFrameId != 0 && sample.ElapsedNanoseconds != 0 && sample.Sequence != 0)
            return true;

        failures.Add($"{prefix}: a Ready GPU timestamp lacks source-frame, elapsed-time, or sequence provenance.");
        return false;
    }

    private static bool ValidateResultSha256(RenderBenchScenarioResult result, List<string> failures)
    {
        bool valid = IsSha256(result.InputSha256) && IsSha256(result.ExecutableSha256) &&
            result.EngineAssemblySha256.Count != 0 && result.EngineAssemblySha256.All(static pair => !string.IsNullOrWhiteSpace(pair.Key) && IsSha256(pair.Value)) &&
            result.ShaderSha256.Count != 0 && result.ShaderSha256.All(static pair => !string.IsNullOrWhiteSpace(pair.Key) && IsSha256(pair.Value));
        if (!valid)
            failures.Add($"{result.Depth}/{result.Lane}: input, executable, or shader SHA-256 evidence is missing or malformed.");
        return valid;
    }

    private static bool IdentitiesMatch(params RenderBenchScenarioResult[] results)
    {
        if (results.Length == 0)
            return false;
        RenderBenchScenarioResult baseline = results[0];
        return results.Skip(1).All(result => result.InputSha256 == baseline.InputSha256 &&
            result.ExecutableSha256 == baseline.ExecutableSha256 &&
            result.VendorId == baseline.VendorId && result.DeviceId == baseline.DeviceId &&
            string.Equals(result.Adapter, baseline.Adapter, StringComparison.Ordinal) &&
            result.Driver == baseline.Driver &&
            result.EngineAssemblySha256.Count != 0 && result.EngineAssemblySha256.Count == baseline.EngineAssemblySha256.Count &&
            result.EngineAssemblySha256.All(pair => baseline.EngineAssemblySha256.TryGetValue(pair.Key, out string? hash) && hash == pair.Value) &&
            result.ShaderSha256.Count == baseline.ShaderSha256.Count &&
            result.ShaderSha256.All(pair => baseline.ShaderSha256.TryGetValue(pair.Key, out string? hash) && hash == pair.Value));
    }

    private static bool FramesMatch(RenderBenchScenarioFrame first, RenderBenchScenarioFrame second)
        => first.ColorSha256 == second.ColorSha256 &&
           first.MaskedCoverageMode == second.MaskedCoverageMode &&
           first.MaskedBorderPixelCount == second.MaskedBorderPixelCount &&
           first.MaskedHoleAdjacentTargetPixelCount == second.MaskedHoleAdjacentTargetPixelCount &&
           first.VisibleCandidateIds.Order().SequenceEqual(second.VisibleCandidateIds.Order()) &&
           first.KeptCandidateIds.Order().SequenceEqual(second.KeptCandidateIds.Order()) &&
           first.EarlyCandidateIds.Order().SequenceEqual(second.EarlyCandidateIds.Order()) &&
           first.LateCandidateIds.Order().SequenceEqual(second.LateCandidateIds.Order());

    private static void ValidateDrawIdMappings(
        string phase,
        uint[] drawIds,
        RenderBenchDrawIdMapping[] mappings,
        List<string> failures)
    {
        if (mappings.Length != drawIds.Length)
        {
            failures.Add($"{phase} DrawID mapping count {mappings.Length} does not match raw count {drawIds.Length}.");
            return;
        }

        HashSet<uint> mappedDrawIds = [];
        HashSet<int> mappedCandidates = [];
        for (int index = 0; index < drawIds.Length; index++)
        {
            RenderBenchDrawIdMapping mapping = mappings[index];
            if (mapping.DrawId != drawIds[index])
                failures.Add($"{phase} DrawID mapping at index {index} does not preserve the raw DrawID.");
            if (!mappedDrawIds.Add(mapping.DrawId))
                failures.Add($"{phase} DrawID mapping contains duplicate DrawID {mapping.DrawId}.");
            if (mapping.CandidateId.HasValue == mapping.IsKnownOccluder)
                failures.Add($"{phase} DrawID {mapping.DrawId} is not classified as exactly one candidate or the known occluder.");
            if (mapping.CandidateId is { } candidateId && !mappedCandidates.Add(candidateId))
                failures.Add($"{phase} candidate {candidateId} maps from more than one raw DrawID.");
        }
    }

    private static void ValidatePhaseSeparation(RenderBenchScenarioFrame frame, List<string> failures)
    {
        HashSet<uint> earlyDrawIds = [.. frame.EarlyDrawIds];
        earlyDrawIds.IntersectWith(frame.LateDrawIds);
        if (earlyDrawIds.Count != 0)
            failures.Add($"a raw DrawID appears in both early and late output [{string.Join(',', earlyDrawIds.Order())}].");

        HashSet<int> earlyCandidates = [.. frame.EarlyDrawMappings
            .Where(static mapping => mapping.CandidateId.HasValue)
            .Select(static mapping => mapping.CandidateId!.Value)];
        HashSet<int> lateCandidates = [.. frame.LateDrawMappings
            .Where(static mapping => mapping.CandidateId.HasValue)
            .Select(static mapping => mapping.CandidateId!.Value)];
        earlyCandidates.IntersectWith(lateCandidates);
        if (earlyCandidates.Count != 0)
            failures.Add($"a candidate maps from both early and late output [{FormatIds(earlyCandidates)}].");
    }

    private static bool IsSha256(string? value)
        => value is { Length: 64 } && value.All(static character => character is >= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f');

    private static string FormatIds(IEnumerable<int> ids) => string.Join(',', ids.Order());

    private struct VisibilityCounts
    {
        public int EligibilityCount;
        public int VisibleCount;
        public int OccludedCount;
        public int KeptCount;
        public int RenderedCount;
        public int FalseOcclusionCount;
        public int MissingVisibleCount;
        public int ConservativeOverdrawCount;
        public int DemonstratedCullCount;

        public void Add(RenderBenchVisibilityFrameVerdict verdict)
        {
            EligibilityCount += verdict.EligibilityCount;
            VisibleCount += verdict.VisibleCount;
            OccludedCount += verdict.OccludedCount;
            KeptCount += verdict.KeptCount;
            RenderedCount += verdict.RenderedCount;
            FalseOcclusionCount += verdict.FalseOcclusionCount;
            MissingVisibleCount += verdict.MissingVisibleCount;
            ConservativeOverdrawCount += verdict.ConservativeOverdrawCount;
            DemonstratedCullCount += verdict.DemonstratedCullCount;
        }
    }
}

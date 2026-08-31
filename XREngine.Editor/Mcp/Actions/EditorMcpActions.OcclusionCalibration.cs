using System;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using XREngine.Data.Core;
using XREngine.Rendering.Occlusion;

namespace XREngine.Editor.Mcp;

public sealed partial class EditorMcpActions
{
    private const int MaximumGpuHiZCrossoverSamples = 4096;

    [XRMcp(Name = "evaluate_gpu_hiz_crossover", Permission = McpPermissionLevel.ReadOnly)]
    [Description("Evaluate caller-supplied, parity-proven matched GPU Hi-Z timings into an offline calibration artifact. This does not alter engine policy or forced modes.")]
    public static Task<McpToolResponse> EvaluateGpuHiZCrossoverAsync(
        McpToolContext context,
        [McpName("samples_json"), Description("JSON array of GpuHiZMatchedCrossoverSample records. Costs must share the stated timestamp scope.")]
        string samplesJson,
        [McpName("requirements_json"), Description("JSON GpuHiZCrossoverRequirements record supplied by the benchmark owner.")]
        string requirementsJson)
    {
        try
        {
            GpuHiZMatchedCrossoverSample[]? samples = JsonSerializer.Deserialize<GpuHiZMatchedCrossoverSample[]>(
                samplesJson,
                GpuHiZCrossoverJsonOptions);
            GpuHiZCrossoverRequirements requirements = JsonSerializer.Deserialize<GpuHiZCrossoverRequirements>(
                requirementsJson,
                GpuHiZCrossoverJsonOptions);
            if (samples is null || samples.Length == 0 || samples.Length > MaximumGpuHiZCrossoverSamples)
                throw new ArgumentOutOfRangeException(nameof(samplesJson), $"Provide 1 through {MaximumGpuHiZCrossoverSamples} matched samples.");

            requirements.Validate();
            for (int index = 0; index < samples.Length; ++index)
            {
                if (!samples[index].IsValid)
                    throw new ArgumentException($"Sample {index} is incomplete, non-finite, or not a Full/Coarse candidate.", nameof(samplesJson));
                samples[index].Bucket.Validate();
            }

            GpuHiZSelectorCalibration calibration = GpuHiZSelectorCalibrator.Calibrate(samples, requirements);
            object[] decisions = calibration.Decisions
                .OrderBy(static entry => entry.Key.BackendId, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Key.GpuIdentity, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Key.SceneFingerprint, StringComparer.Ordinal)
                .Select(static entry => new
                {
                    bucket = entry.Key,
                    candidate = entry.Value.Candidate.ToString(),
                    reason = entry.Value.Reason.ToString(),
                    profile = entry.Value.Profile is { } profile
                        ? new
                        {
                            candidate = profile.Candidate.ToString(),
                            parity_proof_source = profile.ParityProofSource,
                            matched_cohort_fingerprint = profile.MatchedCohortFingerprint,
                            timestamp_scope = profile.TimestampScope,
                            completed_matched_frames = profile.CompletedMatchedFrames,
                            paired_win_samples = profile.PairedWinSamples,
                            median_disabled_gpu_nanoseconds = profile.MedianDisabledGpuNanoseconds,
                            median_candidate_gpu_nanoseconds = profile.MedianCandidateGpuNanoseconds,
                            worst_paired_savings_nanoseconds = profile.WorstPairedSavingsNanoseconds,
                        }
                        : null,
                })
                .Cast<object>()
                .ToArray();

            return Task.FromResult(new McpToolResponse(
                "Evaluated caller-supplied matched GPU timing evidence without changing occlusion policy.",
                new
                {
                    requirements,
                    input_sample_count = samples.Length,
                    decisions,
                    note = "Selected means the supplied parity proof, cohort, timestamp scope, and conservative paired-cost requirements were internally consistent. It is not a runtime policy change or a general performance claim.",
                }));
        }
        catch (JsonException ex)
        {
            return Task.FromResult(new McpToolResponse($"Invalid crossover JSON: {ex.Message}", isError: true));
        }
        catch (ArgumentException ex)
        {
            return Task.FromResult(new McpToolResponse($"Invalid crossover evidence: {ex.Message}", isError: true));
        }
        catch (OverflowException ex)
        {
            return Task.FromResult(new McpToolResponse($"Crossover evidence exceeds supported bounds: {ex.Message}", isError: true));
        }
    }

    private static JsonSerializerOptions GpuHiZCrossoverJsonOptions { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

"""Summarize Vulkan 1.4 samples without treating invalid captures as baselines."""

import argparse
import datetime as dt
import json
import math
import re
from pathlib import Path


TIMINGS = (
    "frame_output_whole_frame_ms", "render_dispatch_ms", "update_ms", "fixed_update_ms", "collect_visible_ms",
    "render_outside_vulkan_frame_ms", "vulkan_frame_gpu_command_buffer_ms", "gpu_pipeline_frame_ms",
    "collect_wait_for_render_ms", "render_wait_for_collect_ms", "vulkan_frame_total_ms",
    "vulkan_frame_wait_fence_ms", "vulkan_frame_wait_swapchain_image_ms",
    "vulkan_frame_wait_current_slot_ms", "vulkan_frame_wait_next_slot_before_collect_ms",
    "vulkan_frame_acquire_image_ms", "vulkan_frame_record_command_buffer_ms",
    "vulkan_frame_submit_ms", "vulkan_frame_present_ms", "vulkan_frame_tree_wait_ms",
    "vulkan_frame_sample_timing_queries_ms", "vulkan_frame_drain_retired_resources_ms", "vulkan_longest_causal_wait_ms",
    "vulkan_render_thread_wait_for_chain_workers_ms", "vulkan_pipeline_compile_total_ms",
    "vulkan_frame_stage_resource_prepare_ms",
    "vulkan_cpu_frame_op_preparation_ms", "vulkan_cpu_resource_planning_ms",
    "vulkan_cpu_frame_data_refresh_ms", "vulkan_cpu_packet_construction_ms",
    "vulkan_cpu_command_buffer_reuse_ms", "vulkan_cpu_frame_op_signature_ms",
    "vulkan_cpu_command_chain_fast_signature_ms", "vulkan_cpu_command_chain_packet_lowering_ms",
    "vulkan_cpu_command_chain_schedule_evaluation_ms", "vulkan_cpu_command_chain_compatibility_scan_ms",
    "vulkan_cpu_command_chain_dependency_aggregation_ms", "vulkan_cpu_command_chain_recorded_key_capture_ms",
    "vulkan_cpu_prepared_mesh_binding_validation_ms", "vulkan_cpu_prepared_mesh_hole_materialization_ms",
    "vulkan_cpu_primary_recording_ms", "vulkan_cpu_secondary_recording_ms",
    "vulkan_cpu_descriptor_publication_ms", "vulkan_cpu_submission_preparation_ms",
    "vulkan_cpu_submission_image_state_validation_ms", "vulkan_cpu_submission_resource_lifetime_validation_ms",
    "vulkan_cpu_queue_submit_ms", "vulkan_cpu_submission_publication_ms",
    "vulkan_cpu_primary_encoding_setup_ms", "vulkan_cpu_primary_operation_loop_ms",
    "vulkan_cpu_primary_finalization_ms", "vulkan_cpu_primary_end_command_buffer_ms",
    "vulkan_cpu_primary_frame_data_manifest_ms", "vulkan_cpu_primary_prewarm_ms",
    "vulkan_cpu_primary_command_encoding_ms", "vulkan_cpu_raw_mesh_request_drain_ms",
    "vulkan_cpu_frame_op_cohort_ms", "vulkan_cpu_frame_op_plan_ms",
)
COUNTERS = (
    "vulkan_advanced_early_visibility_barrier_emissions", "vulkan_indirect_api_calls",
    "vulkan_command_buffer_clean_reuse_count", "vulkan_command_buffer_record_count",
    "vulkan_command_buffer_forced_dirty_count", "vulkan_command_buffer_frame_op_signature_dirty_count",
    "vulkan_command_buffer_planner_dirty_count", "vulkan_command_buffer_profiler_dirty_count",
    "vulkan_indirect_secondary_eligible_producer_complete", "vulkan_indirect_secondary_mutable_current_frame",
    "vulkan_indirect_secondary_producer_incomplete", "vulkan_indirect_secondary_buffer_identity_changed",
    "vulkan_indirect_secondary_invalid_range", "vulkan_indirect_secondary_command_chains_disabled",
    "vulkan_indirect_secondary_unsupported_inheritance", "vulkan_indirect_secondary_resource_preparation_failed",
    "vulkan_command_chains_scheduled", "vulkan_command_chains_recorded", "vulkan_command_chains_reused",
    "vulkan_volatile_command_chains_recorded", "vulkan_primary_command_buffers_reused",
    "vulkan_primary_command_buffers_recorded", "vulkan_pipeline_cache_lookup_hits",
    "vulkan_driver_pipeline_cache_persisted_hits", "vulkan_driver_pipeline_cache_runtime_hits",
    "vulkan_driver_pipeline_cache_misses", "vulkan_driver_pipeline_cache_unknown",
    "vulkan_pipeline_cache_lookup_misses", "vulkan_pipeline_compile_required_count",
    "vulkan_pipeline_compile_completed_count", "vulkan_required_pipeline_pending_count",
    "vulkan_render_thread_shader_compile_count", "vulkan_material_payload_cache_hits",
    "vulkan_material_payload_cache_misses", "vulkan_descriptor_expansion_cache_hits",
    "vulkan_descriptor_expansion_cache_misses", "vulkan_auto_uniform_fallback_draws",
    "texture_upload_jobs", "texture_upload_bytes",
)
NATIVE_HEAP_PROCESS_COUNTERS = (
    "vulkan_heap_sampler_binds_total", "vulkan_heap_resource_binds_total",
    "vulkan_heap_pushes_total", "vulkan_heap_sampler_writes_total",
    "vulkan_heap_resource_writes_total", "vulkan_heap_payload_allocations_total",
)
INDIRECT_SECONDARY_PROCESS_COUNTERS = (
    "vulkan_indirect_secondary_reuses_total", "vulkan_indirect_secondary_recordings_total",
    "vulkan_indirect_secondary_key_evaluations_total", "vulkan_indirect_secondary_complete_keys_total",
    "vulkan_indirect_secondary_matching_keys_total", "vulkan_indirect_secondary_policy_rejections_total",
)
REUSE_DECISION_FIELDS = (
    "vulkan_command_chain_schedule_decision",
    "vulkan_command_buffer_decision_reason_mask",
    "vulkan_primary_entry_state_mismatch",
    "vulkan_first_command_chain_structural_dirty_reason",
    "vulkan_first_command_chain_descriptor_generation_mismatch",
    "vulkan_first_command_chain_resource_plan_revision_mismatch",
)

EXPECTED_POLICY = {
    "active_render_backend": "Vulkan",
    "profile_mode": "ReleaseBenchmark",
    "profile_intrusive": False,
    "active_texture_binding_rung": "BindlessMaterialTable",
    "occlusion_submission_strategy": "GpuIndirectZeroReadback",
    "vulkan_render_target_mode": "DynamicRendering",
    "vulkan_presentation_profile_resolved": "Uncapped",
    "vulkan_presentation_limiter_enabled": False,
    "vulkan_validation_enabled": False,
}
OBSERVED_POLICY = tuple(EXPECTED_POLICY) + (
    "effective_strategy",
    "vulkan_command_chain_benchmark_force_rerecord",
    "vulkan_present_mode", "vulkan_presentation_maximum_frames_ahead", "tsr_render_scale",
    "vulkan_frame_slot_count", "vulkan_swapchain_image_count", "vulkan_present_id_enabled",
    "vulkan_present_wait_enabled", "vulkan_display_timing_enabled",
)
REUSE_POLICY_OPTIONS = {
    "Allowed": {
        "VulkanPrimaryReuse": "Enabled",
        "VulkanCommandChains": "Enabled",
        "VulkanCommandChainBenchmarkForceRerecord": False,
    },
    "ForceRecording": {
        "VulkanPrimaryReuse": "Disabled",
        "VulkanCommandChains": "Enabled",
        "VulkanCommandChainBenchmarkForceRerecord": True,
    },
}


def read_json(path):
    return json.loads(path.read_text(encoding="utf-8-sig"))


def timestamp(value):
    # .NET writes seven fractional digits; Python 3.10 accepts microseconds.
    normalized = re.sub(r"(\.\d{6})\d+", r"\1", value.replace("Z", "+00:00"))
    return dt.datetime.fromisoformat(normalized)


def distribution(values):
    ordered = sorted(values)
    if not ordered:
        return {"n": 0, "p50": None, "p95": None, "p99": None, "max": None, "mean": None}

    def percentile(fraction):
        position = (len(ordered) - 1) * fraction
        lower = math.floor(position)
        upper = math.ceil(position)
        return round(ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower), 6)

    return {
        "n": len(ordered), "p50": percentile(.50), "p95": percentile(.95),
        "p99": percentile(.99), "max": max(ordered), "mean": sum(ordered) / len(ordered),
    }


def summarize_process_cumulative_counter(samples, field, elapsed_seconds, rendered_frame_id_span):
    """Describe a process-total counter without presenting it as calls per frame.

    Native descriptor-heap recording can occur in a recording that is later
    discarded, so these values are recording activity rather than GPU execution.
    """
    values = [sample.get(field) for sample in samples]
    numeric = [value for value in values if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value)]
    if not numeric:
        return {"available": False, "reason": "Counter is unavailable in retained samples."}
    if len(numeric) != len(samples):
        return {"available": False, "reason": "Counter is missing or non-numeric in one or more retained samples."}
    if any(current < previous for previous, current in zip(numeric, numeric[1:])):
        return {
            "available": False, "reason": "Counter is not monotonic across retained samples.",
            "first": numeric[0], "last": numeric[-1],
        }
    delta = numeric[-1] - numeric[0]
    return {
        "available": True,
        "first": numeric[0], "last": numeric[-1], "delta": delta,
        "delta_per_elapsed_second": delta / elapsed_seconds if elapsed_seconds > 0 else None,
        # This is normalized to the sampled rendered-frame ID range, not a
        # per-frame call count. The sampled endpoints can span many frames.
        "delta_per_rendered_frame_id_span": (
            delta / rendered_frame_id_span if rendered_frame_id_span and rendered_frame_id_span > 0 else None),
    }


def validate_mutation_workload(directory, result):
    if result["workload"] == "Moving":
        log_path = directory / "raw" / "profile-camera-motion.log"
        log = log_path.read_text(encoding="utf-8-sig") if log_path.exists() else ""
        completion = re.search(r"Deactivated updates=(\d+) setupFailed=False", log)
        if "Invalidate this profile run" in log or completion is None or int(completion[1]) < 2:
            result["valid"] = False
            result["reasons"].append("Active viewport camera motion has no successful completion evidence.")
        return
    if result["workload"] not in ("MaterialEdits", "Streaming", "VolatileUi", "Resize"):
        return
    log_path = directory / "raw" / "profile-mutation-workload.log"
    log = log_path.read_text(encoding="utf-8-sig") if log_path.exists() else ""
    summaries = re.findall(r"Deactivated mode=(\w+) (.*)", log)
    if not summaries or summaries[-1][0] != result["workload"]:
        result["valid"] = False
        result["reasons"].append("Mutation workload completion evidence is missing.")
        return
    counters = {key: int(value) for key, value in re.findall(r"(\w+)=(\d+)\b", summaries[-1][1])}
    result["mutation_workload"] = counters
    required = {"Streaming": "visibleBindings", "Resize": "resizes"}.get(result["workload"], "updates")
    if "Invalidate this profile run" in log or counters.get("failed", 0) or counters.get(required, 0) < 2:
        result["valid"] = False
        result["reasons"].append("Mutation workload did not complete repeated changes successfully.")


def summarize_run(directory):
    invocation = read_json(directory / "invocation.json")
    validation_path = directory / "validation.json"
    validation = read_json(validation_path) if validation_path.exists() else {
        "valid": False, "reasons": ["Capture is incomplete: validation.json is missing."]}
    result = {
        "label": directory.name, "binding": invocation["binding"], "workload": invocation["workload"],
        "seed": invocation["seed"], "valid": validation["valid"], "reasons": validation["reasons"],
        "metrics": {}, "capture_samples": 0, "native_heap_recording_activity": {},
        "unavailable_metrics": {
            "vulkan_cpu_*_allocated_bytes": "Detailed Vulkan CPU allocation probes are disabled in the production profile.",
        },
    }
    # Pre-Phase E captures did not pin a reuse policy. Keep those reports
    # readable, but only Phase E cohorts can participate in a policy contrast.
    reuse_policy = invocation.get("reusePolicy")
    result["reuse_policy"] = reuse_policy
    if reuse_policy is not None:
        expected_reuse_options = REUSE_POLICY_OPTIONS.get(reuse_policy)
        if expected_reuse_options is None:
            result["valid"] = False
            result["reasons"].append(f"Unknown reuse policy {reuse_policy!r}.")
        else:
            pinned_options = invocation.get("expectedReuseOptions")
            argument_options = {key: invocation.get("arguments", {}).get(key) for key in expected_reuse_options}
            if pinned_options != expected_reuse_options or argument_options != expected_reuse_options:
                result["valid"] = False
                result["reasons"].append("Invocation reuse-policy options differ from the strict expected policy.")
    advanced = invocation.get("environment", {}).get("XRE_UNIT_TEST_RENDER_PIPELINE") == "AdvancedRenderPipeline"
    result["render_pipeline"] = "AdvancedRenderPipeline" if advanced else "DefaultRenderPipeline"
    advanced_failures = set()
    advanced_emissions = []
    expected_policy = dict(EXPECTED_POLICY)
    if reuse_policy is not None:
        expected_policy["vulkan_command_chain_benchmark_force_rerecord"] = (
            reuse_policy == "ForceRecording")
    if advanced:
        # Advanced uses native stable-bin recording, not GPURenderPassCollection's
        # occlusion telemetry. Require the resolved strategy and physical output
        # plus native recording evidence instead of its unused reset enum.
        del expected_policy["occlusion_submission_strategy"]
        expected_policy["effective_strategy"] = "GpuIndirectZeroReadback"
        if invocation.get("environment", {}).get("XRE_ADVANCED_RENDER_PIPELINE_MODE") != "Required":
            advanced_failures.add("Advanced execution was not explicitly required.")
    summary_path = directory / "summary.json"
    if not summary_path.exists():
        return result
    summary = read_json(summary_path)
    if isinstance(summary, list):
        if len(summary) != 1:
            raise ValueError(f"Expected one harness run in {summary_path}")
        summary = summary[0]
    result["summary"] = summary
    if not summary.get("CaptureStartUtc") or not summary.get("CaptureEndUtc"):
        return result
    start, end = timestamp(summary["CaptureStartUtc"]), timestamp(summary["CaptureEndUtc"])
    process_start = timestamp(summary["ProcessStartUtc"]) if summary.get("ProcessStartUtc") else None
    raw_path = directory / "raw" / "profiler-render-stats.ndjson"
    if not raw_path.exists():
        result["valid"] = False
        result["reasons"].append("Retained render-stat samples are missing.")
        return result
    values_by_field = {field: [] for field in TIMINGS + COUNTERS}
    startup_values = {field: [] for field in TIMINGS + COUNTERS}
    startup_samples = 0
    first_completed = None
    outcomes = {}
    failed_frame_examples = []
    policy = {field: {} for field in OBSERVED_POLICY}
    scene_submission_policy = {}
    empty_completed_frames = 0
    capture_cumulative_samples = []
    reuse_decisions = {field: {} for field in REUSE_DECISION_FIELDS}
    forced_reuse_samples = 0
    # Keep numeric series, not the large complete diagnostic objects for every
    # sampled frame. Failed render loops can produce thousands of samples.
    with raw_path.open(encoding="utf-8-sig") as source:
        for line in source:
            if not line.startswith("{"):
                continue
            sample = json.loads(line)
            sample_time = timestamp(sample["ts_utc"])
            if first_completed is None and sample.get("vulkan_frame_outcome") == "Completed":
                first_completed = sample_time
            if sample_time < start:
                startup_samples += 1
                for field, values in startup_values.items():
                    value = sample.get(field)
                    if isinstance(value, (int, float)) and not isinstance(value, bool) and math.isfinite(value):
                        if field == "gpu_pipeline_frame_ms" and not sample.get("gpu_pipeline_timings_ready"):
                            continue
                        if field in ("gpu_pipeline_frame_ms", "vulkan_frame_gpu_command_buffer_ms") and value <= 0:
                            continue
                        values.append(value)
            if not start <= sample_time <= end:
                continue
            result["capture_samples"] += 1
            capture_cumulative_samples.append(sample)
            outcome = sample.get("vulkan_frame_outcome", "Missing")
            outcomes[outcome] = outcomes.get(outcome, 0) + 1
            if outcome in ("Rejected", "Failed") and len(failed_frame_examples) < 8:
                failed_frame_examples.append({field: sample.get(field) for field in (
                    "ts_utc", "render_frame_id", "vulkan_frame_engine_frame_number", "vulkan_frame_slot",
                    "vulkan_frame_outcome", "vulkan_frame_output_generation", "vulkan_device_lost",
                )})
            if outcome == "Completed":
                for field, histogram in reuse_decisions.items():
                    value = json.dumps(sample.get(field))
                    histogram[value] = histogram.get(value, 0) + 1
                if reuse_policy == "ForceRecording" and (
                        sample.get("vulkan_command_chains_reused", 0) > 0 or
                        sample.get("vulkan_primary_command_buffers_reused", 0) > 0):
                    forced_reuse_samples += 1
                if advanced:
                    scene_outputs = [output for output in sample.get("frame_outputs", {}).get("outputs", [])
                                     if output.get("output_kind") == "DesktopScene" and output.get("rendered")]
                    if (len(scene_outputs) != 1 or
                            scene_outputs[0].get("pipeline_name") != "AdvancedRenderPipeline" or
                            not scene_outputs[0].get("scene_rendered") or
                            scene_outputs[0].get("command_count", 0) <= 0):
                        advanced_failures.add("A completed sample lacks one rendered Advanced desktop scene with commands.")
                    emissions = sample.get("vulkan_advanced_early_visibility_barrier_emissions", 0)
                    advanced_emissions.append(emissions)
                    if emissions <= 0:
                        advanced_failures.add("Exact early-visibility barrier emission evidence is missing.")
                    if (sample.get("vulkan_indirect_api_calls", 0) <= 0 and
                            sample.get("vulkan_command_buffer_clean_reuse_count", 0) <= 0):
                        advanced_failures.add("A completed Advanced sample has neither native indirect recording nor primary reuse.")
                for field, observed in policy.items():
                    value = json.dumps(sample.get(field))
                    observed[value] = observed.get(value, 0) + 1
                # A resize can finish a clear-only frame without submitting meshes.
                # Its reset strategy enum is CpuDirect, which is not a CPU draw.
                has_scene_submission = any(sample.get(field, 0) > 0 for field in (
                    "gpu_scene_command_count", "cpu_direct_draw_calls", "gpu_indirect_draw_calls",
                ))
                if has_scene_submission:
                    strategy = json.dumps(sample.get("occlusion_submission_strategy"))
                    scene_submission_policy[strategy] = scene_submission_policy.get(strategy, 0) + 1
                else:
                    empty_completed_frames += 1
            for field, value in sample.items():
                if field.startswith("vulkan_cpu_") and field.endswith("allocated_bytes"):
                    continue
                if field not in values_by_field and not field.endswith("allocated_bytes"):
                    continue
                if not isinstance(value, (int, float)) or isinstance(value, bool) or not math.isfinite(value):
                    continue
                if field == "gpu_pipeline_frame_ms" and not sample.get("gpu_pipeline_timings_ready"):
                    continue
                if field in ("gpu_pipeline_frame_ms", "vulkan_frame_gpu_command_buffer_ms") and value <= 0:
                    continue
                values_by_field.setdefault(field, []).append(value)
    result["frame_outcomes"] = outcomes
    result["reuse_decision_histograms"] = reuse_decisions
    result["forced_policy_reuse_samples"] = forced_reuse_samples
    if forced_reuse_samples:
        result["valid"] = False
        result["reasons"].append(
            f"Forced-recording policy reported cached chain/primary reuse in {forced_reuse_samples} completed samples.")
    result["failed_frame_examples"] = failed_frame_examples
    result["completed_frame_policy"] = policy
    result["scene_submission_policy"] = scene_submission_policy
    result["empty_completed_frames"] = empty_completed_frames
    rendered_frame_ids = [
        sample.get("render_frame_id") for sample in capture_cumulative_samples
        if sample.get("vulkan_frame_outcome") == "Completed"
    ]
    numeric_frame_ids = [
        frame_id for frame_id in rendered_frame_ids
        if isinstance(frame_id, (int, float)) and not isinstance(frame_id, bool) and math.isfinite(frame_id)
    ]
    if not numeric_frame_ids:
        rendered_frame_id_span = None
        result["rendered_frame_id_span"] = {"available": False, "reason": "No completed rendered frame IDs were retained."}
    elif len(numeric_frame_ids) != len(rendered_frame_ids):
        rendered_frame_id_span = None
        result["rendered_frame_id_span"] = {"available": False, "reason": "A completed retained sample has no numeric rendered frame ID."}
    elif any(current < previous for previous, current in zip(numeric_frame_ids, numeric_frame_ids[1:])):
        rendered_frame_id_span = None
        result["rendered_frame_id_span"] = {"available": False, "reason": "Rendered frame IDs are not monotonic across retained samples."}
    else:
        rendered_frame_id_span = numeric_frame_ids[-1] - numeric_frame_ids[0]
        result["rendered_frame_id_span"] = {
            "available": True, "first": numeric_frame_ids[0], "last": numeric_frame_ids[-1],
            "span": rendered_frame_id_span,
        }
    elapsed_seconds = (end - start).total_seconds()
    result["native_heap_recording_activity"] = {
        field: summarize_process_cumulative_counter(
            capture_cumulative_samples, field, elapsed_seconds, rendered_frame_id_span)
        for field in NATIVE_HEAP_PROCESS_COUNTERS
    }
    result["indirect_secondary_recording_activity"] = {
        field: summarize_process_cumulative_counter(
            capture_cumulative_samples, field, elapsed_seconds, rendered_frame_id_span)
        for field in INDIRECT_SECONDARY_PROCESS_COUNTERS
    }
    indirect_reuses = result["indirect_secondary_recording_activity"]["vulkan_indirect_secondary_reuses_total"]
    if reuse_policy == "ForceRecording" and indirect_reuses.get("available") and indirect_reuses.get("delta", 0) > 0:
        result["valid"] = False
        result["reasons"].append("Forced-recording policy reused indirect secondary artifacts during capture.")
    for field, counter in result["native_heap_recording_activity"].items():
        if not counter["available"]:
            result["unavailable_metrics"][field] = counter["reason"]
    for field, expected in expected_policy.items():
        observed = scene_submission_policy if field == "occlusion_submission_strategy" else policy[field]
        if set(observed) != {json.dumps(expected)}:
            result["valid"] = False
            result["reasons"].append(f"Completed frames did not consistently use {field}={expected!r}.")
    if advanced:
        result["advanced_recording_evidence"] = {
            "early_barrier_emissions": distribution(advanced_emissions),
            "interpretation": "Native recording evidence, not a GPU completion receipt. Accepted completed frames, the actual Advanced output manifest, resolved strategy, and separate viewed synchronization validation are also required.",
        }
        if advanced_failures:
            result["valid"] = False
            result["reasons"].extend(sorted(advanced_failures))
    failed_frames = sum(outcomes.get(outcome, 0) for outcome in ("Rejected", "Failed"))
    if failed_frames:
        result["valid"] = False
        result["reasons"].append(f"{failed_frames} sampled Vulkan frames were rejected or failed.")
    if not outcomes.get("Completed", 0):
        result["valid"] = False
        result["reasons"].append("No completed Vulkan frame was captured.")
    for field, values in sorted(values_by_field.items()):
        result["metrics"][field] = distribution(values)
    result["startup_observation"] = {
        "samples_before_capture": startup_samples,
        "first_observed_completed_frame_latency_ms": (
            (first_completed - process_start).total_seconds() * 1000
            if first_completed is not None and process_start is not None else None),
        "metrics_before_capture": {field: distribution(values) for field, values in sorted(startup_values.items())},
    }
    validate_mutation_workload(directory, result)
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("run_root", type=Path)
    args = parser.parse_args()
    reports = args.run_root.resolve() / "reports"
    runs = [summarize_run(path.parent) for path in sorted(reports.glob("*/invocation.json"))]
    groups = {}
    for run in runs:
        if run["seed"]:
            continue
        key = f'{run["render_pipeline"]}/{run["binding"]}/{run["workload"]}'
        if run["reuse_policy"] is not None:
            key = f'{key}/{run["reuse_policy"]}'
        groups.setdefault(key, []).append(run)
    def summarize_group(group):
        accepted = [run for run in group if run["valid"]]
        metrics = {}
        fields = set(TIMINGS + COUNTERS)
        for run in accepted:
            fields.update(run["metrics"])
        for field in sorted(fields):
            medians = [run["metrics"].get(field, {}).get("p50") for run in accepted]
            medians = [value for value in medians if value is not None]
            middle = distribution(medians)["p50"]
            metrics[field] = {
                "valid_runs": len(medians), "median_of_run_medians": middle,
                "min_run_median": min(medians) if medians else None,
                "max_run_median": max(medians) if medians else None,
                "run_median_spread_percent": 100 * (max(medians) - min(medians)) / middle if middle and len(medians) > 1 else None,
            }
        return {"valid_runs": len(accepted), "attempted_runs": len(group), "metrics": metrics}

    comparisons = {key: summarize_group(group) for key, group in groups.items()}
    reuse_policy_groups = {}
    for run in runs:
        if run["seed"] or run["reuse_policy"] is None:
            continue
        key = f'{run["render_pipeline"]}/{run["binding"]}/{run["workload"]}'
        reuse_policy_groups.setdefault(key, {}).setdefault(run["reuse_policy"], []).append(run)
    reuse_policy_comparisons = {
        key: {policy: summarize_group(policy_runs) for policy, policy_runs in policies.items()}
        for key, policies in reuse_policy_groups.items()
    }
    result = {
        "method": "Linear-interpolated percentiles of retained samples in CaptureStartUtc..CaptureEndUtc; CPU/counter zeros included. GPU zero/unready samples excluded. Counts and means describe strided samples, not all process frames. GPU snapshots may lag CPU frames. Validation combines harness checks with retained frame outcomes; rejected/failed frames invalidate a capture. Companion visual correctness review is still required.",
        "runs": runs, "comparisons": comparisons,
        "reuse_policy_comparisons": reuse_policy_comparisons,
    }
    (reports / "baseline-distributions.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    lines = ["# Vulkan 1.4 baseline sample distributions", "", result["method"], "",
             "| Run | Capture valid | Samples | Render CPU p50 / p95 / p99 ms | GPU command buffer p50 / p95 / p99 ms |",
             "| --- | --- | ---: | ---: | ---: |"]

    def triple(run, field):
        if not run["valid"]:
            return "invalid capture"
        metric = run["metrics"].get(field, {})
        return " / ".join("n/a" if metric.get(key) is None else f'{metric[key]:.3f}' for key in ("p50", "p95", "p99"))

    for run in runs:
        if not run["seed"]:
            lines.append(f'| {run["label"]} | {run["valid"]} | {run["capture_samples"]} | {triple(run, "render_dispatch_ms")} | {triple(run, "vulkan_frame_gpu_command_buffer_ms")} |')
    lines += ["", "## Variation between valid runs", "",
              "| Pipeline / binding / workload / reuse policy (when pinned) | Valid / attempted | Render CPU median range ms | Render CPU spread | GPU median range ms | GPU spread |",
              "| --- | ---: | ---: | ---: | ---: | ---: |"]

    def variation(group, field):
        metric = group["metrics"][field]
        if metric["valid_runs"] < 2:
            return "insufficient repeats | n/a"
        return (f'{metric["min_run_median"]:.3f}–{metric["max_run_median"]:.3f} | '
                f'{metric["run_median_spread_percent"]:.2f}%')

    for key, group in comparisons.items():
        lines.append(f'| {key} | {group["valid_runs"]} / {group["attempted_runs"]} | '
                   f'{variation(group, "render_dispatch_ms")} | {variation(group, "vulkan_frame_gpu_command_buffer_ms")} |')
    if reuse_policy_comparisons:
        lines += ["", "## Reuse-policy contrasts", "",
                  "Each row compares matching pipeline, binding, and workload cohorts. Values are the median of valid run p50s; deltas are ForceRecording relative to Allowed.", "",
                  "| Pipeline / binding / workload | Allowed render / GPU p50 ms | ForceRecording render / GPU p50 ms | Render / GPU delta |",
                  "| --- | ---: | ---: | ---: |"]

        def contrast_value(policy_groups, field):
            allowed = policy_groups.get("Allowed", {}).get("metrics", {}).get(field, {}).get("median_of_run_medians")
            forced = policy_groups.get("ForceRecording", {}).get("metrics", {}).get(field, {}).get("median_of_run_medians")
            if allowed is None or forced is None:
                return None, None, "n/a"
            delta = 100 * (forced - allowed) / allowed if allowed else None
            return allowed, forced, "n/a" if delta is None else f"{delta:+.2f}%"

        for key, policy_groups in reuse_policy_comparisons.items():
            allowed_render, forced_render, render_delta = contrast_value(policy_groups, "render_dispatch_ms")
            allowed_gpu, forced_gpu, gpu_delta = contrast_value(policy_groups, "vulkan_frame_gpu_command_buffer_ms")
            pair = lambda render, gpu: "n/a" if render is None or gpu is None else f"{render:.3f} / {gpu:.3f}"
            lines.append(f"| {key} | {pair(allowed_render, allowed_gpu)} | {pair(forced_render, forced_gpu)} | {render_delta} / {gpu_delta} |")
    lines += ["", "## E5 native descriptor-heap recording activity", "",
              "These are process-cumulative native recording counters sampled at capture endpoints. Deltas include discarded recordings and do not prove GPU execution. Frame-ID normalization is an endpoint span, not calls per frame.", "",
              "| Run / reuse policy | Counter | First | Last | Delta | Delta / elapsed second | Delta / rendered frame-ID span |",
              "| --- | --- | ---: | ---: | ---: | ---: | ---: |"]
    for run in runs:
        if run["seed"]:
            continue
        run_policy = run["reuse_policy"] or "un-pinned legacy"
        for field, counter in run["native_heap_recording_activity"].items():
            if not counter["available"]:
                lines.append(f'| {run["label"]} / {run_policy} | {field} | unavailable: {counter["reason"]} |  |  |  |  |')
                continue

            def native_value(key):
                value = counter.get(key)
                return "n/a" if value is None else f"{value:.6f}" if isinstance(value, float) else str(value)

            lines.append(
                f'| {run["label"]} / {run_policy} | {field} | {native_value("first")} | {native_value("last")} | '
                f'{native_value("delta")} | {native_value("delta_per_elapsed_second")} | '
                f'{native_value("delta_per_rendered_frame_id_span")} |')
    lines += ["", "## Capture exclusions", ""]
    for run in runs:
        if not run["valid"]:
            lines.append(f'- {run["label"]}: {"; ".join(run["reasons"])}')
    (reports / "baseline-distributions.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Summarized {len(runs)} captures into {reports / 'baseline-distributions.json'}")


if __name__ == "__main__":
    main()

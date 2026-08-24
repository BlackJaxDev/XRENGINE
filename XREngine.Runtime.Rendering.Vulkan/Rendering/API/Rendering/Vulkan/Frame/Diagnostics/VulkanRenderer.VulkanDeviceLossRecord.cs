using System;
using Silk.NET.Vulkan;

namespace XREngine.Rendering.Vulkan;

/// <summary>
/// Immutable evidence captured by the thread that first observes device loss.
/// It intentionally snapshots the submission and resource-lifetime state before
/// loss fallout clears timeline values or releases pending work.
/// </summary>
internal sealed record VulkanDeviceLossRecord(
    string Operation,
    Result Result,
    string Reason,
    DateTimeOffset ObservedAtUtc,
    VulkanSubmissionDiagnosticContext Submission,
    VulkanResourceLifetimeSnapshot ResourceLifetime);

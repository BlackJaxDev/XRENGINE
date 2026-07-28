#if VULKAN_PERFORMANCE_TOOL_ONLY
using XREngine.Benchmarks;

Environment.ExitCode = VulkanPerformanceCommand.Run(args);
#else
using BenchmarkDotNet.Running;
using XREngine.Benchmarks;
using XREngine.Benchmarks.SelfIteration;

if (args.Contains("--self-iterate", StringComparer.OrdinalIgnoreCase))
{
	Environment.ExitCode = SelfIterationBenchmarkHarness.RunAsync(args).GetAwaiter().GetResult();
}
else if (args.Contains("--vulkan-perf", StringComparer.OrdinalIgnoreCase))
{
	Environment.ExitCode = VulkanPerformanceCommand.Run(args);
}
else if (args.Contains("--frame-budget", StringComparer.OrdinalIgnoreCase))
{
	AsyncShaderPipelineFrameBudgetHarness.Run(args);
}
else if (args.Contains("--gltf-phase0-report", StringComparer.OrdinalIgnoreCase))
{
	Environment.ExitCode = GltfPhase0BaselineHarness.Run(args);
}
else if (args.Contains("--fbx-phase7-regression", StringComparer.OrdinalIgnoreCase))
{
	Environment.ExitCode = FbxPhase7RegressionHarness.Run(args);
}
else if (args.Contains("--fbx-phase0-report", StringComparer.OrdinalIgnoreCase))
{
	Environment.ExitCode = FbxPhase0BaselineHarness.Run(args);
}
else if (args.Contains("--cpu-bvh-report", StringComparer.OrdinalIgnoreCase))
{
	Environment.ExitCode = CpuSceneBvhReportHarness.Run(args);
}
else
{
	BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
#endif

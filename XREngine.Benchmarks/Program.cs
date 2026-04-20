using BenchmarkDotNet.Running;
using XREngine.Benchmarks;

if (args.Contains("--frame-budget", StringComparer.OrdinalIgnoreCase))
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
else
{
	BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
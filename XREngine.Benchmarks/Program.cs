using BenchmarkDotNet.Running;

if (args.Contains("--frame-budget", StringComparer.OrdinalIgnoreCase))
{
	AsyncShaderPipelineFrameBudgetHarness.Run(args);
}
else if (args.Contains("--fbx-phase0-report", StringComparer.OrdinalIgnoreCase))
{
	Environment.ExitCode = FbxPhase0BaselineHarness.Run(args);
}
else
{
	BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
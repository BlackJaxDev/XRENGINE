using BenchmarkDotNet.Running;

if (args.Contains("--frame-budget", StringComparer.OrdinalIgnoreCase))
{
	AsyncShaderPipelineFrameBudgetHarness.Run(args);
}
else
{
	BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
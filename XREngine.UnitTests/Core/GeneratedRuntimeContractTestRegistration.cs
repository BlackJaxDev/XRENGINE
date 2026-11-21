using System.Reflection;

namespace XREngine.UnitTests.Core;

/// <summary>Installs generated test contracts in either the desktop or headless test assembly.</summary>
internal static class GeneratedRuntimeContractTestRegistration
{
    public static IDisposable Install()
    {
        // The shared fixtures compile into different assemblies. Resolve only the installer;
        // tests call the production registries to exercise the compiled generated accessors.
        Assembly assembly = typeof(GeneratedRuntimeContractTestRegistration).Assembly;
        string typeName = "XREngine.Generated.GeneratedRuntimeContracts_" + assembly.GetName().Name!.Replace('.', '_');
        Type generatedType = assembly.GetType(typeName, throwOnError: true)!;
        MethodInfo install = generatedType.GetMethod("Install", BindingFlags.Static | BindingFlags.NonPublic)!;
        return (IDisposable)install.Invoke(null, null)!;
    }
}

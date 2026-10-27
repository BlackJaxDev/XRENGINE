# Hot-path compiler checks

This standalone Roslyn component contains `HotPathAnalyzer` and
`HotPathAttributeGenerator`. It targets `netstandard2.0` for compiler-host
compatibility. Consumer projects can target .NET 10.

Add this reference to each consumer project. Adjust the relative path:

```xml
<ItemGroup>
  <ProjectReference Include="../XREngine.Diagnostics.Analyzers/XREngine.Diagnostics.Analyzers.csproj"
                    OutputItemType="Analyzer"
                    ReferenceOutputAssembly="false" />
</ItemGroup>
```

Then mark a method:

```csharp
using Engine.Diagnostics;

internal static class Simulation
{
    [HotPath]
    public static void Update(System.Span<float> values)
    {
        for (int index = 0; index < values.Length; index++)
            values[index] *= 0.5f;
    }
}
```

The generator emits the exact contents of `HotPathAttribute.cs` and
`HotPathTrustedAttribute.cs` as generated source. Both attributes are internal
to each consumer assembly. Both use `Microsoft.CodeAnalysis.EmbeddedAttribute`
so that friend assemblies do not import them. The generator emits the embedded
attribute definition as a third source file.
Do not also compile copies of these attributes into that assembly. Their
sources are embedded resources in the analyzer project, not runtime types in
the analyzer assembly.

`Microsoft.CodeAnalysis.CSharp` supplies the C# compiler APIs and depends on
`Microsoft.CodeAnalysis.Common`. The `Microsoft.CodeAnalysis.Diagnostics`
namespace is in that common assembly. It is not a separate package reference.
`Microsoft.CodeAnalysis.Analyzers` checks the analyzer implementation.
All package versions match the existing source-generator project.

Calls now require checked source contracts or explicit trusted boundaries.
`ENG0003` rejects unverified behavior. Mutable borrows use control-flow
analysis; unsupported alias forms produce errors.

See [the policy and limits](../docs/developer-guides/diagnostics/hot-path-analyzer.md)
before marking engine methods. `Directory.Build.targets` attaches the analyzer
to all `XREngine.Runtime.*` projects. Other consumers use the reference above.
Only marked methods are checked. The render and physics dispatch loops are
not yet checked; the policy guide lists the enabled kernels.

Run the isolated compiler tests from the repository root:

```powershell
& ./Tools/Invoke-AgentBuild.ps1 -DotnetArguments @('test', 'XREngine.UnitTests/HotPathAnalyzers/XREngine.HotPathAnalyzerTests.csproj')
```

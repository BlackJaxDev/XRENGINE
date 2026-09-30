# Native subsystem inventory

Run from the repository root after reserving a task run:

```powershell
pwsh Tools/Limit-AgentValidation.ps1 -ReserveTaskRun
pwsh Tools/Reports/Invoke-NativeSubsystemInventory.ps1 -OutputDirectory Build/_AgentValidation/<run>/reports
```

The script requires .NET 10 and writes `native-subsystem-counts.csv`,
`native-subsystem-inventory.md`, `native-subsystem-inventory.json`, and
`native-subsystem-identity.json` in that run's `reports/` directory. Its
temporary compiler output stays in the same run's `temp-build/` directory.

Counts measure files with lexical API matches in physical project directories.
The JSON also lists matched source files, literal package references, and
native asset items declared in project XML. Imported MSBuild items, package
runtime assets, and conditional item evaluation need a build or publish audit.

The identity report uses Roslyn syntax to list explicitly public type and
delegate declarations in matched files. It retains CLR-style full namespace,
nested type, and generic arity names, and merges partial declarations. It
searches textual asset, sample, and test data for full-name and simple-name
occurrences. `serializationEvidence` distinguishes full-name text occurrences,
simple-name candidates, and unobserved names. None proves that an object was
serialized or will deserialize; inspect the matched file and serializer before
moving a type. Conditional compilation, generated declarations, imported
Compile items, aliases, and semantic ownership are outside this report's scope.

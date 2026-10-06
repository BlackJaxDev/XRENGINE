# Windows CI

The `Windows CI / Build and test` job builds the full solution and runs the
unfiltered `XREngine.UnitTests` suite on a GitHub-hosted Windows runner. SDK and
workload versions come from `global.json`.

Shader compilation tests do not require a GPU. The Vulkan backend uses the
checked-in Windows x64 shaderc package. See the
[compiler rebuild instructions](../../../Build/Native/Shaderc/README.md).

The job summary reports all outcomes from `TestResults/*.trx`, including cases
that did not run. Some NUnit outcomes do not appear in the console's skipped
count. Use the TRX summary and the uploaded `windows-ci-test-results` artifact
when reviewing coverage. The summary groups tests that did not run by their
reported reason. It does not convert failures to skipped results.

Some integration tests require resources that the hosted runner does not have:

| Tests | Required environment |
| --- | --- |
| OpenGL compute, drawing, and shared contexts | A working OpenGL 4.6 context and compatible GPU driver. |
| Vulkan presentationless integration | A Vulkan 1.4 loader and compatible device. |
| NAudio playback | An available Windows playback device. |
| Steam Audio | The supported `phonon.dll` native runtime. |
| Optional vendor/native smoke tests | The corresponding native libraries and explicit test selection. |
| Private avatar integration | The private fixture paths named by the test diagnostics. |
| Performance benchmarks | Explicit benchmark selection and its prescribed setup. |
| Live API smoke tests | Explicit selection, credentials, and the configured API budget. |

Run these tests in a configured integration environment. Do not treat a hosted
runner's missing capability as evidence that the feature passes. Do not replace
a requested GPU path with a CPU path to make the test pass.

To summarize a downloaded artifact locally:

```powershell
pwsh Tools/Reports/Write-TestSummary.ps1 -ResultsDirectory <test-results>
```

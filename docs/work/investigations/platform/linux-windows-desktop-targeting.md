# Remove The Audio Windows Forms Dependency

Date: 2026-10-23

## Problem and cause

The editor build on Linux stopped in `XREngine.Runtime.Bootstrap` with
`NETSDK1073`: `Microsoft.WindowsDesktop.App.WindowsForms` was not recognized.
Normal NuGet restore and the pinned OpenVR.NET and OscCore submodules were
already available.

The original framework reference came from this resolved dependency chain:

```text
XREngine.Editor (net10.0-windows7.0)
  -> XREngine.Runtime.Bootstrap (net10.0-windows7.0)
     -> XREngine.Audio.NAudio
        -> NAudio 2.3.0, Windows target dependency group
           -> NAudio.WinForms 2.3.0
              -> Microsoft.WindowsDesktop.App.WindowsForms
```

The original installed NAudio package metadata and both restored graphs
confirmed this chain. The audio project itself targets `net10.0`. NuGet selects the
Windows dependency group again in its Windows-targeted consumers. Those consumers
had not enabled Windows targeting on a non-Windows build host. The Linux SDK did
not resolve the transitive desktop reference pack during the normal restore.

## Actual Windows Forms use

The editor uses ImGui or the native UI components. Its source does not use
Windows Forms controls or dialogs. The only `System.Windows.Forms` imports in
editor/runtime source are commented out in
[ControlExtension.cs](../../../../XREngine.Editor/Extensions/ControlExtension.cs)
and [Function.cs](../../../../XREngine.Runtime.Rendering/Scene/Components/UI/Functions/Function.cs).

The separate [broker tray project](../../../../Tools/LocalAgentBroker.Tray/LocalAgentBroker.Tray.csproj)
uses Windows Forms. `TrayApplicationContext` owns `NotifyIcon`, a tray menu, and
a Forms timer. `Program` starts its Forms message loop. That project is not in
the editor's project-reference graph and does not cause this failure.

The [audio adapter](../../../../XREngine.Audio.NAudio/XREngine.Audio.NAudio.csproj)
uses `WaveOutEvent`, `WaveInEvent`, ASIO driver discovery, Core Audio device
queries, and codecs. These are audio APIs, not Forms controls. `System.Drawing`
and Windows audio/native APIs are separate platform dependencies. Their presence
does not establish a Forms UI dependency.

## Fix and boundary

The first fix enabled `EnableWindowsTargeting` in Bootstrap and the editor. It
resolved the build failure by restoring the Windows desktop reference pack.
Both full builds and 167 headless tests passed. The user then approved removal
of the unnecessary audio package dependency.

The final fix replaces `NAudio` 2.3.0 with its required component packages:

| Package | Version | Used API |
|---|---|---|
| `NAudio.Core` | 2.3.0 | Mixer interfaces, WAV readers/writers, `Mp3FileReaderBase` |
| `NAudio.WinMM` | 2.3.0 | Wave input/output, ACM PCM conversion, `AcmMp3FrameDecompressor` |
| `NAudio.Wasapi` | 2.3.0 | Core Audio device enumeration |
| `NAudio.Asio` | 2.3.0 | ASIO driver discovery |
| `NAudio.Sdl2` | 2.2.6 | SDL audio device queries; unchanged reference |

There is no current MIDI or Forms API use in the adapter or its consumers.
Both MP3 callers now use the supported Core reader with the same ACM factory as
NAudio's umbrella reader. File ownership, supplied-stream ownership, PCM
conversion, and public engine interfaces remain unchanged. See
[the audio architecture](../../../architecture/audio/audio-architecture.md#naudio-component-boundary)
for the exact upstream source and API boundary.

The temporary `EnableWindowsTargeting` settings are removed from Bootstrap and
the editor. Their `net10.0-windows7.0` targets remain unchanged. The separate
broker tray app remains unchanged. Windows audio APIs still require Windows.
This cleanup does not establish native Linux editor support and does not replace
ACM with a different decoder. No package version, native dependency, source
exclusion, renderer default, or Windows workload changed.

## Validation

All checks below ran on headless Linux on 2026-10-23. Build, restore, test, and
MSBuild evaluation commands used `Tools/Invoke-AgentBuild.ps1` sequentially.

| Check | Result |
|---|---|
| Audio adapter build, `--no-restore` | Passed; zero warnings and errors. |
| Full Bootstrap build, `--no-restore` | Passed; zero warnings and errors, with no targeting override. |
| Full editor build, `--no-restore` | Passed; zero warnings and errors, with no targeting override. |
| Existing headless project build | Passed; zero warnings and errors. |
| Existing headless executable, `--workers=1` | Passed: 167 tests; zero failures, warnings, skipped, or inconclusive tests. |
| Desktop unit-test project compilation | Passed as part of the targeted test command; no compile warnings or errors reported. |
| Existing `NAudioTransportTests` through `dotnet test` | Not run: NUnit discovered 49 tests but rejected the Windows-targeted assembly with `Only supported on Windows7.0`. The command returned zero; the TRX recorded zero executed tests. This is not a passing audio test result. |
| Bootstrap/editor target evaluation | Both retain `net10.0-windows7.0` and platform `Windows`. `EnableWindowsTargeting` and `UseWindowsForms` are unset. |
| Editor runtime/dependency files | Only `Microsoft.NETCore.App` is required. All five audio components remain; NAudio umbrella, MIDI, and Forms are absent. |
| Dependency inventory generator | Completed with `-NoPromptForUnknownLicenses`. Added the four MIT component notices and removed the umbrella notice. Unrelated prior license records were retained where local or remote lookup lost metadata. |
| Windows execution, MP3 decode, playback/capture, ASIO/Core Audio enumeration | Not run; requires Windows and the relevant devices/codecs. |

The restored asset audit covered the audio adapter and all ten project-reference
consumers: Bootstrap, Editor, UnitTests, RenderBench, Server, VRClient,
HumanoidConformanceRunner, ProfilerStartupProbe, McpDocsGenerator, and
UnitTestingWorldSettingsGenerator. Each graph has the five exact component
versions above, no `NAudio`, `NAudio.Midi`, or `NAudio.WinForms` package, and no
Windows Forms or Windows Desktop framework requirement. The audit checked both
project framework references and package target framework references.

Server restore first stopped at `NETSDK1100`. Its existing
`net10.0-windows10.0.26100.0` target adds `Microsoft.Windows.SDK.NET.Ref.Windows`.
Restore passed with the command-line property `-p:EnableWindowsTargeting=true`.
This is a separate Windows SDK requirement; the server project is unchanged.
That global restore property can put `Microsoft.WindowsDesktop.App.Ref` in
`downloadDependencies`, even in child graphs. A downloaded reference pack is not
a runtime or framework requirement. The audit distinguishes these entries.

No test code or assertions changed. The Windows assembly restriction was not
bypassed. The editor remains Windows-targeted; no Linux runtime, deployment,
visual, GPU, or audio-device result is claimed. See
[Native UI validation](../../testing/ui/native-ui-validation.md) for the retained
UI contract checks and the pending live acceptance work.

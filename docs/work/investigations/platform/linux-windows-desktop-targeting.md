# Windows Desktop References On Linux Build Hosts

Date: 2026-10-23

## Problem and cause

The editor build on Linux stopped in `XREngine.Runtime.Bootstrap` with
`NETSDK1073`: `Microsoft.WindowsDesktop.App.WindowsForms` was not recognized.
Normal NuGet restore and the pinned OpenVR.NET and OscCore submodules were
already available.

The framework reference comes from this resolved dependency chain:

```text
XREngine.Editor (net10.0-windows7.0)
  -> XREngine.Runtime.Bootstrap (net10.0-windows7.0)
     -> XREngine.Audio.NAudio
        -> NAudio 2.3.0, Windows target dependency group
           -> NAudio.WinForms 2.3.0
              -> Microsoft.WindowsDesktop.App.WindowsForms
```

The installed NAudio package metadata and both projects' `project.assets.json`
confirm this chain. The audio project itself targets `net10.0`. NuGet selects the
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
queries, and codecs. These are audio APIs, not Forms controls. The MP3 decoder
uses `Mp3FileReader` from the NAudio umbrella assembly. Removing that package
requires a separate dependency/API review; deleting its reference would break
the current decoder. `System.Drawing` and Windows audio/native APIs are separate
platform dependencies. Their presence does not establish a Forms UI dependency.

## Fix and boundary

[Bootstrap](../../../../XREngine.Runtime.Bootstrap/XREngine.Runtime.Bootstrap.csproj)
and [the editor](../../../../XREngine.Editor/XREngine.Editor.csproj) now default
`EnableWindowsTargeting` to `true` when the caller did not set it. The setting
lets restore obtain the reference pack for their existing Windows target.
An explicit caller value is preserved. Target frameworks, source files, audio
APIs, package versions, and Windows runtime requirements are unchanged.

This is Windows cross-compilation on Linux. It is not a Linux-compatible editor
port and does not make Windows Forms, WinMM, Core Audio, or Windows native
libraries run on Linux. It installs no Windows workload and excludes no source.
Microsoft documents the [Windows targeting setting](https://learn.microsoft.com/en-us/dotnet/core/tools/sdk-errors/netsdk1100)
and its reference-pack restore behavior.

## Validation

A command-line probe with `EnableWindowsTargeting=true` compiled Bootstrap with
zero warnings and errors. The probe reused already built project references.
The final full-graph Bootstrap build and editor build both passed with zero
warnings and errors, using the project settings and no command-line targeting
override. The existing headless suite passed 167 of 167 tests after the fix.
No test code changed. See [Native UI validation](../../testing/ui/native-ui-validation.md).

After restore with the project settings, both resolved graphs retain
`NAudio` and its ASIO, Core, MIDI, Wasapi, WinMM, and WinForms components at 2.3.0,
plus `NAudio.Sdl2` 2.2.6. Both retain the Windows Forms framework reference and
`net10.0-windows7.0`. No package or source was removed to bypass the failure.
Windows execution, audio-device behavior, and visual/GPU acceptance require a
Windows environment and remain unverified here.

MSBuild evaluation confirms that both projects still target
`net10.0-windows7.0`, identify their platform as `Windows`, and do not set
`UseWindowsForms`. An explicit `EnableWindowsTargeting=false` stays false. The
editor runtime configuration still requires `Microsoft.WindowsDesktop.App`
10.0.0. The default Linux-host build emits an ELF apphost alongside the managed
Windows-target assembly. That host cannot supply the missing Windows runtime.
This build result is not a deployable Windows package or a Linux runtime result;
packaging for Windows requires a Windows runtime identifier and the required
native dependencies. No packaging claim is made here.

# Vulkan shader compiler

The Windows x64 runtime includes `xr_shaderc.dll`, built from shaderc 2026.4.
`sources.json` pins shaderc and its glslang, SPIRV-Tools, and SPIRV-Headers
dependencies by commit and archive SHA-256. The dependency revisions match the
upstream shaderc `DEPS` file.

The engine uses Silk.NET for the managed bindings and loads this DLL by its
exact application-local path. The Silk.NET 2.23.0 native package does not
correctly process the Vulkan 1.4 target. Do not lower the engine target to hide
that mismatch. Shader artifact cache identities include the packaged DLL hash.

Normal builds and publish operations copy the checked-in DLL and its combined
upstream license notices. They do not download or compile shaderc. A clean CI
runner therefore uses the same compiler as a local build.

To rebuild the package, install Visual Studio C++ tools, CMake, and Python, then
run this command from the repository root:

```powershell
pwsh Tools/Build-Shaderc.ps1
```

The script verifies downloaded archives, builds the Release x64 shared library,
and updates `XREngine.Runtime.Rendering.Vulkan/runtimes/win-x64/native/`.
Use `-BuildRoot <task-run>/temp-build/shaderc` for an isolated build.
Commit the DLL, license notices, source manifest, and dependency report together
when the package changes. Regenerate the report with
`pwsh Tools/Reports/Generate-Dependencies.ps1 -NoPromptForUnknownLicenses`.

Shaderc and SPIRV-Tools use Apache-2.0. The glslang runtime and SPIRV headers use
permissive BSD, MIT, and Apache terms. The combined notices retain the upstream
texts, including notices for upstream tools that are not part of this DLL.

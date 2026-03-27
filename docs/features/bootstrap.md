# Bootstrap And First-Time Setup

This is the practical first-time setup guide for XRENGINE.

If you want the shortest answer, use:

```powershell
ExecTool --bootstrap
```

That is the broadest built-in setup path in the repo.

## What `ExecTool --bootstrap` does

The bootstrap flow in `ExecTool.bat` currently does these steps:

1. initializes all git submodules
2. runs every dependency installer in the `Deps` group
3. builds submodules
4. builds the DocFX site
5. launches the DocFX server
6. launches the editor

If all you want is the standard working repo setup, this is the right place to start.

## What it does not cover

`ExecTool --bootstrap` is a strong default, but it is not a guarantee that every optional feature dependency is installed.

It does not automatically guarantee:

- proprietary SDK setup
- every optional dependency exposed only through separate VS Code tasks
- workstation prerequisites such as missing Visual Studio C++ workloads
- every feature-specific external tool you might need for specialized workflows

Examples of things you may still need to install separately depending on what you are working on:

- NVIDIA SDK binaries
- CUDA / TensorRT / Audio2Face-related prerequisites
- optional media or tooling dependencies not included in the `Deps` group

## Recommended first-time setup flow

### Option 1: Use the built-in bootstrap

From the repo root:

```powershell
ExecTool --bootstrap
```

This is the recommended path for most contributors.

### Option 2: Manual setup

If you want to run the steps one by one instead:

```powershell
Tools\Initialize-Submodules.bat
```

Then install the dependency set you need, for example through `ExecTool` or the individual scripts under `Tools/Dependencies/`.

Then build the solution:

```powershell
dotnet restore
dotnet build XRENGINE.slnx
```

## Verifying that bootstrap worked

After bootstrap, you should be able to do the following:

1. build the editor
2. launch the editor
3. launch the Unit Testing World
4. open local docs if DocFX built successfully

Useful checks:

```powershell
dotnet build XRENGINE.slnx
```

```powershell
dotnet run --project .\XREngine.Editor\XREngine.Editor.csproj
```

Or use the VS Code launch/task setup already included in the repo.

## When to go beyond bootstrap

You should go beyond the default bootstrap when you are working on subsystem-specific features such as:

- NVIDIA integrations
- advanced audio / Audio2Face flows
- optional native tooling for content import or processing
- any workflow that explicitly mentions a dependency installer or vendor SDK in its docs

In those cases, treat bootstrap as the base layer and then install the extra dependencies required for your feature.

## Related setup docs

- [Native Dependencies](native-dependencies.md)
- [Unit Testing World](unit-testing-world.md)
- [Getting Started In The Codebase](../architecture/getting-started-in-codebase.md)
- [Documentation Index](../README.md)
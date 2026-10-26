# Continuous Integration And Release Branches

XRENGINE uses GitHub Actions for pull-request validation and a gated `deploy` to `release` promotion flow.

## Pull Requests

`windows-ci.yml` builds the managed and native submodules, generates the Unit Testing World settings, builds `XRENGINE.slnx`, and runs the NUnit suite on `windows-latest`.

All build workflows select the SDK through `global.json`. Windows CI, deploy, and release jobs run `dotnet workload restore XRENGINE.slnx` before building submodules so a fresh runner installs the pinned workload set before MSBuild evaluates any projects. Installing the SDK alone does not provision that workload set. The browser workflow installs `wasm-tools` from the repository root, honoring the same pin.

The Editor clears its in-process MSBuild paths, including `MSBuildSDKsPath`, when it starts a child `dotnet msbuild` process. The child selects its SDK from its working directory and the applicable `global.json`. This prevents an installed newer SDK selected by the Editor from overriding the child build's SDK imports and task hosts.

The submodule script discovers Visual Studio MSBuild and C++ targets through `vswhere`, including Enterprise and standalone Build Tools installations. On runners without the Vulkan SDK, the Vulkan project reuses its packaged native bridge DLL; a generated PDB is optional. Native builds register available symbols after bridge preparation so the first clean build, consuming projects, and publish outputs can include them.

Before compiling native Rive, the script applies the tracked patch in
`Tools/Patches/rive-sharp/`. It accepts an already applied patch and stops if
the source does not match. The patch fixes the two Clang unused-variable
errors without disabling compiler warnings. See the
[patch notes](../../Tools/Patches/rive-sharp/README.md) before updating Rive.

Windows CI runs for every pull request, regardless of its author or branch name. Dependency review runs separately to check for newly introduced vulnerabilities.

`dependency-review.yml` rejects pull requests that introduce dependencies with known vulnerabilities at moderate severity or higher. GitHub's dependency graph must be enabled. Private repositories also require the applicable GitHub Code Security entitlement.

`portable-browser-compile.yml` compiles the portable project manifest, publishes the browser host, and runs browser qualification. Its Windows job publishes game bundles through the Editor CLI. Downstream Linux jobs qualify those exact bundles in Chromium. These checks cover the untrimmed interpreter; they do not qualify trimming or browser AOT. See [Portable Project Rules](runtime/portable-projects.md) for the enforced boundaries.

## Deploy Gate

Push a candidate commit to `deploy` to run the full promotion gate:

1. Build the solution and run the full unit-test suite on a GitHub-hosted Windows runner.
2. Run CodeQL's C# `security-extended` query suite.
3. Build and run the targeted GPU regressions on a self-hosted Windows GPU runner.
4. Run Vulkan validation-layer and OpenGL/Vulkan editor smoke passes, capture screenshots, and retain logs and measurement reports as workflow artifacts.
5. Fast-forward `release` to the exact validated `deploy` commit.
6. Dispatch the release-branch documentation build.

Promotion is fast-forward-only. If `release` contains commits that are not ancestors of the validated `deploy` commit, the workflow stops instead of overwriting release history.

The deploy workflow needs repository workflow permissions that allow `contents: write` and `actions: write`. Branch protection for `release` must permit the GitHub Actions bot to perform the promotion, while still preventing direct unreviewed pushes from other actors.

## GPU Runner

Register the GPU validation machine as a repository or organization self-hosted runner with these labels:

- `self-hosted`
- `Windows`
- `X64`
- `gpu`

The runner must execute in an interactive Windows desktop session with the production Vulkan/OpenGL drivers installed. Do not run this job from a Windows service session: the editor needs a visible desktop to create rendering windows and capture smoke-test screenshots.

Because the GPU workflow only runs for pushes to the trusted `deploy` branch, untrusted pull-request code is never executed on the self-hosted machine.

## Release Branch And Tags

Every update to `release` builds and archives the DocFX site. Promotions use `workflow_dispatch` after pushing because GitHub intentionally does not start another push-triggered workflow for commits pushed with the repository `GITHUB_TOKEN`.

Tags are deliberate rather than generated automatically. Push a semantic-version tag that points to a commit reachable from `release`, for example:

```powershell
git tag v0.1.0 release
git push origin v0.1.0
```

The accepted format is `vMAJOR.MINOR.PATCH` with an optional prerelease suffix. The release workflow verifies that the tag belongs to `release`, applies that version to the published assemblies and packages, builds DocFX, publishes Windows x64 Editor, Server, and VRClient archives, and creates or updates the corresponding GitHub Release. GitHub also provides the standard source archives for the tag.

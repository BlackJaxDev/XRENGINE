# Dedicated server startup

For local development, run `dotnet run --project XREngine.Server/XREngine.Server.csproj -- --development` from the repository root. This starts the existing headless server-default world. Add `--unit-testing` after `--development` to select the world configured by `Assets/UnitTestingWorldSettings.jsonc`:

```powershell
dotnet run --project XREngine.Server/XREngine.Server.csproj -- --development --unit-testing
```

Set `XRE_UNIT_TEST_WORLD_SETTINGS_PATH` to an existing JSONC settings file when using another configuration, such as `Build/_AgentValidation/<run>/scratch/UnitTestingWorldSettings.jsonc`. The unit-world mode retains the dedicated server's headless physics and networking startup. It has no local input, window, renderer, or VR services; selected content that requires those services must be validated in a desktop client instead.

Managed workers continue to load their verified world package. `--unit-testing` is rejected for managed startup and does not alter that security path.

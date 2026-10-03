# VRClient startup

Run `dotnet run --project XREngine.VRClient/XREngine.VRClient.csproj -- --unit-testing` from the repository root to load the configured unit-testing world in a native desktop window. This mode uses a plain `GameStartupSettings` world lifecycle, the renderer selected by the unit-testing settings, and 90 Hz update/render with 45 Hz fixed ticks. It does not initialize an HMD, start the paired game process, or exercise VR input/render proxy communication.

The settings file defaults to `Assets/UnitTestingWorldSettings.jsonc`. Set `XRE_UNIT_TEST_WORLD_SETTINGS_PATH` to an existing JSONC file to select a different one, for example `Build/_AgentValidation/<run>/scratch/UnitTestingWorldSettings.jsonc`. The mode fails with a named missing-file error if the selected file does not exist.

Launching without arguments retains the paired-game VR client flow.

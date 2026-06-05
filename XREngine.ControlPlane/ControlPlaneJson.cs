using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.Networking;

namespace XREngine.ControlPlane;

// VS Code's design-time source-generator pass can report SYSLIB1030 here even
// though CLI builds emit metadata for these contracts. ControlPlaneTests verifies
// the generated context has metadata for every public contract listed below.
#pragma warning disable SYSLIB1030

[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    NumberHandling = JsonNumberHandling.AllowReadingFromString,
    WriteIndented = true)]
[JsonSerializable(typeof(Dictionary<string, string>), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RealtimeJoinHandoffPayload), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorldAssetIdentity), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RealtimeEndpointDescriptor), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ControlPlaneOptions), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ControlPlaneHostRegistration), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ControlPlaneHostSnapshot), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(CreateMultiplayerInstanceRequest), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JoinMultiplayerInstanceRequest), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(LeaveMultiplayerInstanceRequest), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(MultiplayerInstanceInfo), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(MultiplayerPlayerInfo), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(JoinMultiplayerInstanceResult), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ServerLaunchPlan), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorldPackageManifest), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorldPackageFile), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorldPackageVerificationResult), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<string>), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<ControlPlaneHostSnapshot>), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<MultiplayerInstanceInfo>), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<MultiplayerPlayerInfo>), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(List<WorldPackageFile>), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ControlPlaneResult<MultiplayerInstanceInfo>), GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(ControlPlaneResult<JoinMultiplayerInstanceResult>), GenerationMode = JsonSourceGenerationMode.Metadata)]
public sealed partial class XreControlPlaneJsonContext : JsonSerializerContext
{
}

#pragma warning restore SYSLIB1030

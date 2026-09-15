using System.Text.Json;
using System.Text.Json.Serialization;
using XREngine.ControlPlane;

namespace XREngine.Runtime.Bootstrap;

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, UseStringEnumConverter = true)]
[JsonSerializable(typeof(ManagedServiceCreateInstanceRequest))]
[JsonSerializable(typeof(ManagedServiceReservationRequest))]
[JsonSerializable(typeof(ManagedAdmissionReservation))]
[JsonSerializable(typeof(ManagedClientLaunch))]
internal sealed partial class ManagedInstanceServiceClientJsonContext : JsonSerializerContext;

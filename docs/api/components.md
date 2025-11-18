# Component Architecture

XRENGINE composes behaviour through components that attach to `SceneNode`s. Each component inherits from `XRComponent`, gains access to the world tick scheduler, and reacts to transform changes without owning its own hierarchy. This document explains how the runtime actually builds, activates, and executes components.

## Anatomy of `XRComponent`
- Components are instantiated through `SceneNode.AddComponent`. The engine creates the object with `FormatterServices.GetUninitializedObject`, binds it to the node, calls its private constructor, fires `OnTransformChanged`, and finally assigns `World` so replication and ticks are available before any user code runs.
- `SceneNode.ComponentAdded` / `ComponentRemoved` invoke the component’s `AddedToSceneNode` / `RemovedFromSceneNode` methods, which default to no-op but allow subclasses to hook world-level resources.
- `IsActive` controls whether the component participates in ticks and interface binding. Flipping the flag drives `OnComponentActivated` and `OnComponentDeactivated`. The helper property `IsActiveInHierarchy` additionally checks the owning node’s activation state.
- Transform changes propagate through `OnTransformChanging` / `OnTransformChanged`. By default the component subscribes to `Transform.RenderMatrixChanged`, so renderable components receive render-thread matrices without polling. See [Transform Architecture](transforms.md) for device- and thread-safe matrix publication details.

## Tick Scheduling
- All world objects, including components, inherit `RegisterTick` / `UnregisterTick` from `XRWorldObjectBase`. Components typically call `RegisterTick(ETickGroup, ETickOrder, Engine.TickList.DelTick)` inside `OnComponentActivated` and rely on the default `UnregisterTicksOnStop=true` to remove them when deactivated.
- Tick groups reflect the engine’s frame stages: `Normal`, `Late`, `PrePhysics`, `DuringPhysics`, and `PostPhysics`. Each group is ordered by large integer bands (`Timers`, `Input`, `Animation`, `Logic`, `Scene`). Custom orders can be supplied by passing an explicit integer instead of `ETickOrder`.
- `VerifyInterfacesOnStart` automatically binds `IRenderable.RenderedObjects` to the active world when a component becomes active; `VerifyInterfacesOnStop` clears those bindings on shutdown.

## Component Attributes and Dependencies
- `RequireComponentsAttribute` automatically ensures required sibling components exist. When a component with this attribute is added, missing dependencies are instantiated through the same `SceneNode.AddComponent` path before the new component is finalised.
- `OneComponentAllowedAttribute` prevents multiple instances of the same type on a node. If the node already contains one, the engine aborts the add and returns the existing instance.
- Custom attributes can inherit from `XRComponentAttribute` to enforce additional policies when components are attached.

## Integration with the Scene Graph
- `SceneNode` keeps a thread-safe `EventList<XRComponent>`; components are removed automatically when destroyed or when `RemoveComponent` is called.
- Sibling lookups (`GetSiblingComponent`, `TryGetSiblingComponent`, `GetSiblingComponents`) are wrappers over the node collection and honour the attributes described above.
- Root systems (rendering, physics, UI) subscribe to `SceneNode.ComponentAdded` and `ComponentRemoved` to maintain caches. Components should therefore perform their world registrations inside `OnComponentActivated` to avoid dependence on construction order.

## Lifecycle Summary
1. `SceneNode.AddComponent<T>` constructs the component, attaches it to the node, validates attributes, and sets `World`.
2. If the node and component are active, `OnComponentActivated` fires immediately, registering ticks and binding interfaces.
3. Each frame the engine executes registered ticks according to group/order. Components may toggle `IsActive` or `UnregisterTicksOnStop` to refine participation.
4. When the node is deactivated or the component is removed, `OnComponentDeactivated` fires, ticks are cleared (unless configured otherwise), and `ComponentDestroyed` raises before the object is disposed.

## Extending the System
- Derive from `XRComponent` and override lifecycle hooks to manage resources. Always call base implementations when overriding `OnComponentActivated` / `OnComponentDeactivated` unless you intend to bypass interface verification.
- Use `RegisterTick` overloads that accept generic callbacks (`RegisterAnimationTick<T>`) when you need strongly typed `this` references without allocations.
- Prefer interfacing via `IRenderable`, `IPhysicsBody`, or other engine interfaces instead of hard-coding subsystem dependencies. `VerifyInterfacesOnStart` handles binding for known interfaces.
- Combine attributes to enforce policies: e.g., `[OneComponentAllowed]` plus `[RequireComponents(typeof(CameraComponent))]` yields a camera rig that ensures only one controller exists per node.

## Best Practices
- Keep tick handlers lightweight and avoid per-frame allocations. If work is sporadic, unregister the tick until needed.
- Treat `OnTransformRenderWorldMatrixChanged` (documented in [Transform Architecture](transforms.md)) as the preferred hook for responding to transform motion; it provides the final render matrix after interpolation.
- Use `GetOrAddComponent` or `TryAddComponent` when building prefab graphs so that repeated initialisation remains idempotent.

## Networking Helpers
### `RestApiComponent`
- Lives in `XRENGINE/Scene/Components/Networking/RestApiComponent.cs` and exposes an `HttpClient`-backed bridge for REST APIs.
- Handles default headers, request queueing, and cancellation when a component or node is deactivated.
- Emits `RequestStarted`, `ResponseReceived`, `RequestFailed`, and `DataUpdated` events on the main thread so UI or gameplay systems can react immediately.
- Maintains a keyed JSON data store; set `RestApiRequest.ResponseDataKey` to cache structured responses for later queries via `TryGetData`.

```csharp
// Attach to a scene node.
var rest = node.AddComponent<RestApiComponent>();
rest.BaseUrl = "https://api.example.com/";
rest.SetDefaultHeader("Authorization", $"Bearer {token}");
rest.ResponseReceived += (_, response) => Debug.Out($"Fetched {response.Request.Resource}: {response.StatusCode}");

var request = new RestApiRequest
{
	Resource = "v1/world-state",
	Query = new Dictionary<string, string> { ["region"] = "us-east" },
	ResponseDataKey = "world-state"
};

await rest.SendAsync(request, cancellationToken);
if (rest.TryGetData("world-state", out JsonNode? json))
{
	string? status = json?["status"]?.GetValue<string>();
	Debug.Out($"Current status: {status}");
}
```

### `WebSocketClientComponent`
- Located at `XRENGINE/Scene/Components/Networking/WebSocketClientComponent.cs`; maintains a resilient `ClientWebSocket` connection.
- Supports handshake headers, auto-reconnect with configurable delay, and main-thread events for connection state, errors, and incoming messages.
- Provide UTF-8 text or binary payloads using `SendTextAsync` / `SendBinaryAsync`.

```csharp
var socket = node.AddComponent<WebSocketClientComponent>();
socket.Endpoint = "wss://example.com/realtime";
socket.MessageReceived += (_, msg) => Debug.Out($"WS text: {msg.Text}");
socket.ConnectionError += (_, err) => Debug.LogWarning($"WS error: {err.Message}");

// Initiate the connection manually (auto-connect can also be enabled via property)
await socket.ConnectAsync();
await socket.SendTextAsync("{\"op\":\"subscribe\"}");
```

### `WebhookListenerComponent`
- Located at `XRENGINE/Scene/Components/Networking/WebhookListenerComponent.cs`; wraps an `HttpListener` to ingest inbound webhook callbacks.
- Emits events on the main thread, queues pending payloads for polling, and responds with configurable status/headers.
- Remember that HTTP prefixes must be registered with `netsh http add urlacl ...` when binding to non-admin ports on Windows.

```csharp
var listener = node.AddComponent<WebhookListenerComponent>();
listener.Prefix = "http://localhost:5055/webhooks/payments/";
listener.WebhookReceived += (_, evt) =>
{
	var payload = evt.DeserializeJson<JsonNode>();
	Debug.Out($"Webhook {evt.Method} {evt.Url}: {payload}");
};

listener.StartListening();
```

### `TcpClientComponent`
- Located at `XRENGINE/Scene/Components/Networking/TcpClientComponent.cs`; keeps a resilient TCP connection alive with optional TLS negotiation.
- Supports auto-reconnect, configurable timeouts, optional text dispatch, and main-thread events for connection, data, and errors.
- Call `SendAsync` for arbitrary bytes or `SendStringAsync` for UTF-8 payloads; enable TLS by toggling `UseTls` and providing `TlsHostName` when required.

```csharp
var client = node.AddComponent<TcpClientComponent>();
client.Host = "telemetry.internal";
client.Port = 9000;
client.UseTls = true;
client.ConnectionTimeout = TimeSpan.FromSeconds(5);
client.DataReceived += (_, data) => Debug.Out($"RX bytes: {data.Length}");
client.TextReceived += (_, text) => Debug.Out($"RX text: {text}");

await client.ConnectAsync();
await client.SendStringAsync("ping\n");
```

### `UdpSocketComponent`
- Located at `XRENGINE/Scene/Components/Networking/UdpSocketComponent.cs`; binds to a local port, listens for datagrams, and can broadcast to peers.
- Offers optional multicast join, auto-rebind on failure, and helper events for raw payloads plus decoded text.
- Use `SendAsync`/`SendStringAsync` for unicast traffic or override the host/port per call when broadcasting.

```csharp
var udp = node.AddComponent<UdpSocketComponent>();
udp.LocalPort = 5005;
udp.RemoteHost = "239.10.0.5";
udp.RemotePort = 5005;
udp.MulticastAddress = "239.10.0.5";
udp.DatagramReceived += (_, packet) => Debug.Out($"RX from {packet.RemoteEndPoint}: {packet.Payload.Length} bytes");

await udp.BindAsync();
await udp.SendStringAsync("discover");
```

### `TcpServerComponent`
- Located at `XRENGINE/Scene/Components/Networking/TcpServerComponent.cs`; accepts multiple clients and dispatches events per connection.
- Exposes `ClientConnected`, `ClientDisconnected`, and data events on the main thread while providing `SendAsync`/`BroadcastAsync` helpers.
- Configure `ListenAddress`, `ListenPort`, and `AutoStartOnActivate` for quick local test servers or in-game debugging consoles.

```csharp
var server = node.AddComponent<TcpServerComponent>();
server.ListenPort = 7777;
server.ClientTextReceived += (_, client, text) => Debug.Out($"[{client.Id}] {text.Trim()}");
server.ClientConnected += (_, client) =>
{
	Debug.Out($"Client {client.RemoteEndPoint} connected");
	_ = server.SendAsync(client.Id, Encoding.UTF8.GetBytes("hello\n"));
};

await server.StartAsync();
```
- When authoring editor tools, rely on the global `XRComponent.ComponentCreated` / `ComponentDestroyed` events instead of scanning the scene graph.

## Related Documentation
- [Scene Architecture](scene.md)
- [Transform Architecture](transforms.md)
- [Rendering Architecture](rendering.md)
- [Physics Architecture](physics.md)
- [Animation Architecture](animation.md)
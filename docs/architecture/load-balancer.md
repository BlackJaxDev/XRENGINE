# Load Balancer Boundary Note

XRENGINE no longer contains an in-process HTTP load balancer.

The earlier `LoadBalancerService`, selection strategies, `Server` catalog model, and `LoadBalancerController` were removed from `XREngine.Server` during the 2026-04-24 networking boundary cleanup. Host registration, capacity, allocation, instance ownership, and public directory routing now belong to the adjacent control-plane app.

For local development, `XREngine.ControlPlane` provides an in-process DLL with basic host capacity, instance lifecycle, join token, realtime handoff, and local world package manifest helpers. It is not an HTTP load balancer; it is the shared local implementation that editor tooling, tests, or a future service wrapper can call.

The engine-side server is a realtime worker only:

- starts a local XR world
- accepts direct UDP joins for a concrete endpoint/session
- validates build compatibility
- validates exact local `WorldAssetIdentity`
- assigns realtime players and replicates state

Any future load-balancing design should live in the control-plane documentation, with XRENGINE receiving only concrete realtime endpoint/session data.

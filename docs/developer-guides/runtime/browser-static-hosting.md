# Production static hosting for BrowserWebGPU

This guide covers HTTP delivery for the static site emitted by the
`BrowserWebGPU` publishing flow. It describes deployment requirements; it does
not configure a server, certify a hosting provider, or claim that production
headers or physical browsers/devices were tested. Browser/device support and
the remaining validation limits are tracked separately in the
[active browser runtime plan](../../work/todo/platform/unified-desktop-browser-runtime-todo.md)
and [publishing record](../../work/progress/rendering/browser-project-publishing.md).

## Publish the whole site over HTTPS

Deploy the complete published output as one site and preserve its relative paths.
The authored player starts at `index.html` and loads same-origin JavaScript,
WebAssembly runtime files, `browser-publish.json`, `content/manifest.json`, and
content-addressed payloads. Do not publish only the HTML shell or open it as a
`file://` URL. Production requires HTTPS: WebGPU is a secure-context API, and
the browser content loader also requires Web Crypto SHA-256. HTTP on loopback
(`localhost`, `127.0.0.1`, or `[::1]`) is accepted for local development only.

The current browser profile is an untrimmed, interpreted .NET 10 WebAssembly
application with native Jolt. Its Jolt browser archive is single-threaded and
the build targets reject `WasmEnableThreads=true`; no thread enablement is
implied by thread-related framework assemblies in the output. Use ordinary
same-origin HTTPS static hosting today. Do not add cross-origin isolation
headers preemptively.

Static output is public content. The content loader fetches engine/game content
with same-origin mode, omitted credentials, and redirects rejected. It verifies
declared lengths and SHA-256 digests before consumption. Do not put credentials,
tokens, or private configuration in the launch descriptor, manifest, or
payloads. An optional managed WebSocket connection is separate from static
content delivery; allow its exact trusted `wss://` origin only when that
integration is used.

## MIME types

Serve the original asset's correct `Content-Type`, including when its body is a
precompressed representation. In particular, `.wasm` must be
`application/wasm`; a wrong type prevents streaming WebAssembly compilation.
Use the exact extensions in the published output, since the SDK can change the
runtime asset set between target frameworks.

| File extensions | Suggested `Content-Type` |
| --- | --- |
| `.html`, `.htm` | `text/html` |
| `.js`, `.mjs` | `application/javascript` |
| `.json` | `application/json` |
| `.wasm` | `application/wasm` |
| `.css` | `text/css` |
| `.wgsl` (if present) | `text/wgsl` or `text/plain` |
| `.bin`, `.dat`, `.webcil` (if present) | `application/octet-stream` |

The official .NET 10 WebAssembly/Nginx example maps JavaScript, JSON, WebAssembly
and binary runtime extensions explicitly. Check all extensions in the actual
publish directory rather than relying on a host's fallback mapping. Returning
`index.html` with status 200 for a missing `.js`, `.wasm`, manifest, or payload
is not a valid substitute for a 404; limit any navigation fallback to intended
page routes. A diagnostic header such as `X-Content-Type-Options: nosniff` is
useful when supported, but it makes correct MIME mappings more important.

## Compression

The .NET publish can contain `.br` and `.gz` sidecars for framework and static
assets. A static host or CDN must negotiate a representation from the request's
`Accept-Encoding` and send the matching `Content-Encoding: br` or
`Content-Encoding: gzip` while retaining the original file's `Content-Type`.
Where negotiation occurs, vary caches by `Accept-Encoding`. Do not send a raw
`.br`/`.gz` file as if it were uncompressed, label Brotli as gzip, or compress a
body twice. If a provider cannot serve these sidecars correctly, use its
supported on-the-fly compression or deliver uncompressed originals; compression
is an optimization, not a reason to mislabel a response. The browser reads
decoded `fetch()` response bytes and validates payload length/hash, so the
content-addressed identity remains the decoded original payload.

## Cache and release ordering

Separate stable entry points from URLs that include content identity:

| Resource | Hosting policy |
| --- | --- |
| `/`, `index.html`, stable-name player/runtime `.js` files and other unhashed bootstrap code | Revalidate on use (`Cache-Control: no-cache`) or use a deliberately short freshness lifetime. |
| `browser-publish.json` | Prefer `Cache-Control: no-store`; the player also fetches this launch descriptor with `cache: "no-store"`. |
| `content/manifest.json` and any other changing content catalogs | `Cache-Control: no-cache` with validators such as `ETag` or `Last-Modified`, so the browser can revalidate the current catalog. The loader requests the manifest with `cache: "no-cache"`. |
| Payloads whose URL embeds the verified content hash, such as `content/payload/<sha256>.bin` in an engine publish or `payloads/<sha256>.bin` in a cooker package | `Cache-Control: public, max-age=31536000, immutable` is appropriate because changing bytes require a new content-addressed URL. The loader may use HTTP `force-cache` for these payloads and verifies the declared byte count and SHA-256 even when a cached response is used. |
| Fingerprinted/hash-suffixed .NET framework assets | Long-lived immutable caching is appropriate only for files whose published URL actually carries a content identity. Revalidate stable-name entry points such as `/_framework/dotnet.js` unless the exact publish layout versions that URL. |

Do not classify files as immutable from their directory or extension alone.
Confirm the URL naming in the release being deployed. Publish new hash-addressed
payloads first, then update the content manifest and launch descriptor/shell;
keep old hashed payloads available long enough for clients still holding an
older manifest. If a request can race with activation, missing assets should
fail explicitly rather than fall through to HTML. Persistent CacheStorage or
IndexedDB support is not part of this path; HTTP-cache eviction only causes a
new verified download.

## Content Security Policy

Start from a deny-by-default HTTP `Content-Security-Policy` response header and
allow only resources the chosen player actually uses. For a deployment serving
the current static player, the policy usually needs same-origin modules and
fetches plus WebAssembly execution. If `script-src` or its `default-src`
fallback is restrictive, CSP requires the more specific `'wasm-unsafe-eval'`
source expression for WebAssembly compilation. It is narrower than
`'unsafe-eval'`; do not add `'unsafe-eval'` as a speculative workaround.

An illustrative starting point, not a copy/paste deployment policy:

```text
default-src 'none';
base-uri 'self';
object-src 'none';
frame-ancestors 'none';
script-src 'self' 'wasm-unsafe-eval';
connect-src 'self' wss://<exact-trusted-gateway-host>;
style-src 'self' 'sha256-<hash-of-the-exact-inline-style-block>'
```

Omit the `wss://` source when the WebSocket gateway is not used; if used, replace
it with the exact trusted gateway origin rather than a scheme-wide or wildcard
allowlist. The shipped `engine-player.html` currently contains an inline `<style>`
block. Either move those rules to a same-origin stylesheet and keep
`style-src 'self'`, or calculate the CSP hash from that exact block and update
it when the HTML changes. A per-response nonce is also valid only when the
response HTML and CSP header share the same fresh nonce. Do not broadly allow
`'unsafe-inline'` just to avoid this maintenance. If the player must be embedded,
adjust `frame-ancestors` for named trusted parents and review the embedding
impact. Additional app-specific assets or endpoints may require narrowly scoped
directives; determine those from the published files and network trace.

Deploy the candidate in `Content-Security-Policy-Report-Only` first where the
host permits it, inspect the browser's policy violations for the actual release,
then enforce an appropriately reviewed policy. No CSP header or policy value was
applied or tested by this documentation change.

## Cross-origin isolation, only if threads are adopted

The current browser/Jolt profile does not enable WebAssembly threads, so it does
not currently require cross-origin isolation. If a later profile deliberately
adopts shared-memory/WebAssembly threads, re-qualify that build and its complete
resource graph. Shared-memory features generally require the document to be
cross-origin isolated, commonly through
`Cross-Origin-Opener-Policy: same-origin` and
`Cross-Origin-Embedder-Policy: require-corp` (or `credentialless`, if compatible
with the deployment), and may be blocked by Permissions Policy. Every embedded
cross-origin resource must then satisfy the embedder policy. Verify
`self.crossOriginIsolated` in the target browser and recheck popup/opener,
embedding, CDN, and gateway behavior before adopting those headers; COOP/COEP
change browsing-context/resource behavior and are not a routine hosting default.

## Deployment check before release

- Confirm the HTTPS certificate, redirect policy, host origin, and complete
  published relative file layout.
- Check response status and `Content-Type` for `index.html`, a player module,
  one actual `.wasm` asset, the launch descriptor, the manifest and a hash URL.
- Request compressed and uncompressed representations and confirm body decoding,
  `Content-Encoding`, original `Content-Type`, and `Vary: Accept-Encoding` when
  negotiated.
- Confirm stable entry points revalidate, content manifests revalidate, only
  content-addressed/fingerprinted URLs receive immutable caching, and old
  payload URLs remain present through the release window.
- Check that a missing static asset returns an asset error instead of the HTML
  shell; verify CSP in report-only mode before enforcing the reviewed policy.
- If a WebSocket gateway is enabled, verify only its documented secure origin is
  allowed and test its authenticated application flow separately.

These are deployment checks to perform on the selected host. This guide and its
example policy do not claim that any physical browser/device matrix, real host,
production CSP, WebGPU device, gateway, or browser security header was tested.

## References

- [.NET 10 WebAssembly hosting with Nginx](https://learn.microsoft.com/en-us/aspnet/core/blazor/host-and-deploy/webassembly/nginx?view=aspnetcore-10.0) (MIME types, cache examples, static compressed assets)
- [.NET static asset delivery](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files?view=aspnetcore-10.0) (content types, ETags, build/publish compression)
- [WebAssembly `instantiateStreaming()`](https://developer.mozilla.org/en-US/docs/WebAssembly/Reference/JavaScript_interface/instantiateStreaming_static) (`application/wasm` requirement)
- [CSP `script-src`](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Content-Security-Policy/script-src) (`'wasm-unsafe-eval'` and least-privilege distinction)
- [Cross-Origin-Opener-Policy and isolation](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Cross-Origin-Opener-Policy) (COOP/COEP and `crossOriginIsolated`)
- [WebGPU API](https://developer.mozilla.org/en-US/docs/Web/API/WebGPU_API) (secure-context requirement)

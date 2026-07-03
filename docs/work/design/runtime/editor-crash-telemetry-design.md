# Editor Exception And Crash Telemetry Design

Last Updated: 2026-07-02
Status: design proposal
Scope: opt-in editor exception reporting, crash reporting, diagnostic package
submission, and generic ASP.NET Core server intake.

Related docs:

- [Profiler diagnostics](../../../developer-guides/diagnostics/profiler.md)
- [Runtime GC and hot-path memory control TODO](../../todo/runtime/gc-and-hot-path-memory-control-todo.md)
- [XR Job Manager](../../../developer-guides/runtime/job-system.md)
- [Networking guide](../../../developer-guides/networking/networking.md)

## 1. Summary

XRENGINE should add an opt-in editor telemetry path for exception and crash
diagnostics. The purpose is to help diagnose real end-user editor failures
without requiring users to manually find logs, describe hardware, zip crash
data, or reproduce a transient graphics-driver failure.

The system should be explicit and user-controlled:

1. Telemetry is off by default.
2. The editor asks for consent before sending anything.
3. The user can choose exactly which data classes may be included.
4. Crash reports are captured locally first, then sent only after consent and
   review policy allow it.
5. Network sending runs outside render/update hot paths.
6. The server intake is a generic HTTPS API; this repository does not need to
   own the backend project to define the client/server contract.

This is not the same as the existing UDP profiler. The profiler streams local
developer diagnostics to a local profiler process. Crash telemetry is a durable,
privacy-aware, HTTPS upload path for rare exception/crash reports and optional
supporting evidence.

## 2. Current Engine Shape

Useful existing pieces:

- `XREngine.Editor/Program.cs` installs global editor crash diagnostics:
  `UnhandledException`, `UnobservedTaskException`, `ProcessExit`, and a narrow
  first-chance trace path.
- `XREngine.Runtime.Core/Core/Diagnostics/Debug.cs` already centralizes logging,
  category logs, console entries, exception logging, first-chance exception
  filtering, and file log directories.
- Editor preferences already expose many diagnostic toggles, including crash
  breadcrumbs, first-chance exception filters, profiler frame logging, render
  statistics, thread allocation tracking, and profiler UDP sending.
- Runtime logs and profiler artifacts are written under
  `Build/Logs/<configuration>_<tfm>/<platform>/<session>/`.
- `XREngine.Data/Profiling/UdpProfilerSender.cs` demonstrates a low-impact
  background sender pattern, but it is localhost UDP and should not be reused as
  the internet telemetry transport.

Missing pieces:

- No persisted user consent model for outbound crash telemetry.
- No local outbox for reports captured during a crash and sent later.
- No report schema that separates required diagnostic metadata from optional
  user-approved data.
- No sanitization/redaction layer for logs, stack traces, machine details, or
  project paths before upload.
- No server intake contract, deduplication strategy, storage model, or
  triage workflow.

## 3. Goals

- Make real editor failures diagnosable from opt-in end-user reports.
- Keep telemetry disabled unless the user explicitly enables it or sends a
  single report.
- Provide granular consent checkboxes for common data classes.
- Let users preview what will be sent when practical.
- Capture crash evidence locally even when the process is unstable, but upload
  it only after consent allows upload.
- Avoid per-frame allocations, blocking waits, or network calls from render,
  update, collect/swap, VR input, or networking hot paths.
- Use HTTPS with bounded payload sizes, retry limits, and an outbox.
- Redact secrets, local usernames, project roots, session tokens, and raw file
  paths by default.
- Keep project assets, source files, scene files, and proprietary content out of
  automatic reports unless a future explicit manual attachment workflow is added.
- Provide a generic ASP.NET Core C# intake design that can be implemented in a
  separate server repository.

## 4. Non-Goals

- Do not add always-on analytics, behavioral tracking, feature-usage tracking,
  or advertising-style event streams.
- Do not upload project assets, source code, scene files, imported model files,
  screenshots, RenderDoc captures, or minidumps by default.
- Do not send environment variables wholesale.
- Do not send API keys, access tokens, session tokens, command-line secrets, or
  full local filesystem paths.
- Do not let telemetry sending alter renderer fallback behavior, crash behavior,
  or editor launch behavior.
- Do not rely on a server being available for crash capture. Local capture and
  editor recovery should work offline.
- Do not put telemetry transport into the existing profiler UDP protocol.

## 5. Privacy And Consent Principles

The consent UI should be built around data classes, not a vague "send telemetry"
toggle. The editor may require a tiny envelope for any submission, but every
optional data category must be user-visible and independently controlled.

Required for any submitted report:

- report id
- schema version
- editor build/version
- report kind
- UTC capture time
- anonymous install id or anonymous machine id
- consent version and consent bits active for this report
- payload size and attachment manifest

The required envelope should be shown as "required report routing data" in the
UI. If the user does not accept that minimum, no report can be sent.

Recommended consent categories:

| Category | Default | Purpose | Notes |
|---|---:|---|---|
| Exception summary and stack traces | on after enabling telemetry | Identify failing code path | Sanitize paths and usernames. |
| Recent editor logs | off or ask per report | Correlate errors with renderer, asset, or startup state | Category and byte capped. |
| Machine specs | off | Diagnose hardware/driver/runtime patterns | CPU model, core count, RAM, GPU, driver, OS, VR runtime. |
| Renderer and runtime settings | on after enabling telemetry | Diagnose OpenGL/Vulkan/OpenXR/OpenVR path | Include selected backend and key feature flags. |
| Performance snapshot | off | Diagnose stalls around crashes | Summaries only, not continuous profiling streams. |
| Project metadata | off | Identify whether scene scale or asset counts matter | Counts/hashes only; no raw asset names by default. |
| Crash minidump | off, ask per crash | Native crash analysis | High sensitivity; never send silently. |
| Viewport screenshot | off, manual only | Visual bug reports | Must be user-reviewed. |
| User comment/contact | off, manual only | Follow-up and reproduction notes | Stored separately from anonymous reports. |

Data that should never be automatically sent:

- raw project assets or source files
- private model/textures/audio data
- full environment-variable dumps
- process command lines containing tokens
- API keys such as `OPENAI_API_KEY`, `ANTHROPIC_API_KEY`, `GEMINI_API_KEY`, or
  `GITHUB_TOKEN`
- `XRE_SESSION_TOKEN` or other networking/session secrets
- raw `Assets/UnitTestingWorldSettings.jsonc` if it contains local paths
- RenderDoc captures unless a future manual upload flow makes the size,
  contents, and sensitivity obvious

## 6. User Experience

### First-Run And Settings

Do not interrupt first launch with telemetry consent. Add an editor preferences
section such as `Diagnostics -> Crash Reports And Telemetry` with:

- master switch: `Enable crash and exception report sending`
- `Ask before sending each report`
- `Automatically send reports matching current consent after restart`
- `Send non-fatal exception reports`
- data-class checkboxes
- retention controls for local pending reports
- `View pending reports`
- `Delete pending reports`
- `Send test report`

The first time the editor catches a fatal crash or restart-after-crash state, it
can show a focused crash report prompt:

- short description of the failure
- data categories available for that report
- preview button
- text box for user notes
- `Send Report`
- `Do Not Send`
- `Always ask`
- `Do not ask again`

### Report Preview

For JSON payloads and logs, preview should show the sanitized payload. For
minidumps, the preview should show metadata only:

- dump file size
- dump type
- captured process id
- captured modules summary
- warning that memory dumps may contain sensitive in-process data

### Telemetry Center

Add a lightweight editor panel later if needed:

- pending reports
- sent report ids
- last send status
- local disk usage
- purge button

This keeps trust visible. Silent outbox retries are technically convenient but
bad product posture for crash data.

## 7. Client Architecture

### 7.1 Settings

Add an editor preference model, probably under the existing preferences system:

```csharp
public sealed class TelemetryConsentSettings : XRBase
{
    private bool _enabled;
    private bool _askBeforeSending = true;
    private TelemetryDataClass _allowedDataClasses;
    private Uri? _endpoint;

    public bool Enabled
    {
        get => _enabled;
        set => SetField(ref _enabled, value);
    }

    public bool AskBeforeSending
    {
        get => _askBeforeSending;
        set => SetField(ref _askBeforeSending, value);
    }

    public TelemetryDataClass AllowedDataClasses
    {
        get => _allowedDataClasses;
        set => SetField(ref _allowedDataClasses, value);
    }

    public Uri? Endpoint
    {
        get => _endpoint;
        set => SetField(ref _endpoint, value);
    }
}

[Flags]
public enum TelemetryDataClass
{
    None = 0,
    RequiredEnvelope = 1 << 0,
    ExceptionSummary = 1 << 1,
    RecentLogs = 1 << 2,
    MachineSpecs = 1 << 3,
    RendererSettings = 1 << 4,
    PerformanceSnapshot = 1 << 5,
    ProjectMetadata = 1 << 6,
    CrashMiniDump = 1 << 7,
    Screenshot = 1 << 8,
    UserComment = 1 << 9,
}
```

Use `SetField(...)` because this is editor preference state and should preserve
normal change notification semantics.

Recommended environment overrides:

- `XRE_TELEMETRY_DISABLE=1`: hard-disable all telemetry sending for this
  process, useful for studios, CI, and private project work.
- `XRE_TELEMETRY_ENDPOINT=<url>`: developer/staging endpoint override.
- `XRE_TELEMETRY_OUTBOX=<path>`: local validation override.

Environment overrides should not silently grant consent. They may only disable
or redirect the transport when consent already permits sending.

### 7.2 Main Types

Suggested client-side components:

- `TelemetryEventRecorder`: receives exception/crash/manual-report events and
  builds report drafts.
- `TelemetryConsentService`: resolves effective consent from preferences,
  environment hard-disable, and per-report user choices.
- `TelemetrySanitizer`: redacts paths, usernames, tokens, URLs, env-like
  strings, and project roots.
- `TelemetryOutbox`: writes reports to a durable local queue.
- `TelemetryHttpSender`: background HTTPS sender with retry/backoff.
- `TelemetryReportPreview`: generates UI-friendly preview models.
- `CrashDumpCapture`: Windows-specific minidump/WER/helper-process integration.

The outbox should live outside project content, for example:

```text
%LocalAppData%/XREngine/Telemetry/Outbox/
%LocalAppData%/XREngine/Telemetry/Sent/
```

Do not put crash reports under `Build/_AgentValidation/` because these are
end-user product artifacts, not AI validation scratch data.

### 7.3 Report Envelope

The envelope should be schema-versioned and stable enough for a server to index
without knowing every future optional payload.

```json
{
  "schema": "xrengine.telemetry.report.v1",
  "reportId": "01JZ...ULID",
  "installId": "anonymous-stable-id",
  "reportKind": "UnhandledException",
  "capturedUtc": "2026-07-02T20:32:10.1234567Z",
  "editor": {
    "version": "0.0.0-dev",
    "configuration": "Debug",
    "targetFramework": "net10.0-windows7.0",
    "commit": "optional-if-known"
  },
  "consent": {
    "consentVersion": 1,
    "allowedDataClasses": [
      "RequiredEnvelope",
      "ExceptionSummary",
      "MachineSpecs",
      "RendererSettings"
    ]
  },
  "exception": {
    "type": "System.InvalidOperationException",
    "messageHash": "sha256:...",
    "messagePreview": "sanitized, capped text",
    "stackTrace": "sanitized, capped stack trace",
    "threadName": "RenderThread"
  },
  "machine": {
    "os": "Windows 11",
    "cpu": "sanitized CPU brand",
    "logicalProcessorCount": 32,
    "memoryMegabytes": 65536,
    "gpus": [
      {
        "name": "GPU name",
        "driver": "driver version",
        "vendorId": "0x10DE",
        "deviceId": "0x2684"
      }
    ]
  },
  "renderer": {
    "backend": "Vulkan",
    "vrRuntime": "OpenVR",
    "featureProfile": "Auto",
    "validationLayers": false
  },
  "attachments": [
    {
      "attachmentId": "logs-main",
      "kind": "RecentLogExcerpt",
      "contentType": "text/plain",
      "sizeBytes": 64123,
      "sha256": "..."
    }
  ]
}
```

Notes:

- `messageHash` supports deduplication even when the visible message is capped.
- Optional sections must be omitted when consent does not include their data
  class.
- Attachment metadata lives in the envelope; attachment bytes can be sent in the
  same multipart request or uploaded separately.
- The report should include active consent bits so the server can reject payloads
  that contain unapproved sections.

### 7.4 Event Classification

Recommended event kinds:

- `UnhandledException`: fatal managed exception.
- `UnobservedTaskException`: background task exception, normally non-fatal.
- `FirstChanceExceptionSample`: optional, throttled, disabled by default.
- `NativeCrashDump`: captured by helper/WER/minidump path.
- `ManualBugReport`: user-triggered report from the editor.
- `StartupRecoveryReport`: generated on next launch after detecting a previous
  abnormal termination.

First-chance exceptions are noisy and often expected. Do not upload every
first-chance event. If enabled, aggregate them by exception type and top frame,
then sample only heavily repeated or configured exception filters.

### 7.5 Crash Capture

Managed exceptions can be captured from current hooks and written to the outbox.
Hard native crashes need a separate plan because the crashing process may be too
damaged to serialize JSON or send HTTP.

Recommended progression:

1. Phase 1: managed fatal exceptions and unobserved task exceptions write a JSON
   draft to the outbox.
2. Phase 2: on startup, detect previous abnormal exit and offer to send the
   saved report plus latest logs.
3. Phase 3: add Windows minidump capture using a crash reporter helper process
   or Windows Error Reporting LocalDumps. Capture locally only.
4. Phase 4: ask before sending minidumps unless the user has explicitly enabled
   automatic minidump upload.

The in-process fatal exception handler should do minimal work:

- gather exception type/message/stack
- reference current log session path
- write a compact JSON file with preallocated or simple APIs
- return control to normal crash/shutdown behavior

Do not block a fatal exception handler on network I/O.

### 7.6 Logs And Diagnostics Packaging

Log capture should be bounded:

- default maximum report JSON size: 256 KB
- default maximum logs attachment size: 1-2 MB compressed
- default maximum minidump size: configurable, but warn above 20 MB
- cap stack traces by frame count and total bytes
- include the latest log session path only after redaction
- include category logs only when `RecentLogs` is allowed

Prefer recent excerpts around the failure over whole logs:

- last N lines from `log_general.log`
- relevant category logs such as `log_rendering.log`, `log_vulkan.log`,
  `log_opengl.log`, `log_vr.log`, or `log_assets.log`
- profiler FPS-drop/render-stall snippets when performance snapshot consent is
  enabled

### 7.7 Redaction

Sanitization should happen before preview, before outbox write when possible,
and again before upload as defense in depth.

Required redactions:

- replace project root with `%PROJECT_ROOT%`
- replace user profile root with `%USERPROFILE%`
- replace temp directory with `%TEMP%`
- replace absolute drive paths with `%PATH%/<tail>` when they are not under a
  recognized safe root
- strip URL query parameters named `token`, `key`, `secret`, `signature`,
  `password`, `auth`, or `session`
- redact environment-variable names known to hold secrets
- redact values that match common token shapes
- cap long lines and binary-looking text

The sanitizer should emit a small `redactionSummary`:

```json
{
  "redactedPathCount": 14,
  "redactedSecretCount": 2,
  "truncatedLineCount": 3,
  "truncatedAttachmentCount": 0
}
```

### 7.8 Sending And Retry

Use `HttpClient` over HTTPS. The sender should:

- run on a background thread or hosted engine service outside hot paths
- wake on new outbox item and also periodically retry pending reports
- use exponential backoff with jitter
- respect user deletion of pending reports
- mark reports as `pending`, `sending`, `sent`, `failed-retryable`, or
  `failed-final`
- avoid retry storms on server outage
- add an idempotency key equal to `reportId`
- gzip or Brotli-compress JSON and text attachments
- never send while `XRE_TELEMETRY_DISABLE=1`

Suggested client headers:

```text
User-Agent: XREngine.Editor/<version>
X-XRE-Telemetry-Schema: xrengine.telemetry.report.v1
X-XRE-Report-Id: <reportId>
Idempotency-Key: <reportId>
Content-Encoding: gzip
```

Authentication can be anonymous at first, but the endpoint should use HTTPS,
rate limiting, size limits, and abuse controls. Do not embed a high-value secret
in the editor binary.

## 8. Generic ASP.NET Core Server Intake

This repository does not include the telemetry server. A generic ASP.NET Core
service should receive reports, validate them, store raw payloads, index useful
fields, group duplicates, and expose triage views to maintainers.

### 8.1 Endpoints

Recommended minimal endpoints:

```text
POST /api/telemetry/v1/reports
GET  /api/telemetry/v1/reports/{reportId}/status
```

The `POST` endpoint can support either:

- `application/json` for envelope-only reports.
- `multipart/form-data` with one `envelope` JSON part and zero or more
  attachment file parts.

Keep the first implementation simple. Envelope-only JSON plus optional logs
attachment is enough before minidumps.

### 8.2 Intake Flow

1. Terminate TLS at the app host or reverse proxy.
2. Apply request size limits before reading the body.
3. Rate-limit by IP, anonymous install id, and report kind.
4. Parse the envelope.
5. Validate schema version and required fields.
6. Verify that optional sections and attachments are permitted by consent bits.
7. Sanitize again on the server.
8. Compute server-side fingerprint and attachment hashes.
9. Store the raw sanitized envelope and attachments in object/blob storage.
10. Insert an indexed row into a relational database.
11. Group the report into an issue bucket by fingerprint.
12. Return a stable receipt containing `reportId`, `serverReceivedUtc`, and
    grouping information if public.

### 8.3 Storage Model

Use two storage layers:

- Object storage for raw sanitized JSON and attachments.
- Relational database for searchable/indexed fields.

Suggested relational tables:

```text
TelemetryReport
- ReportId
- ReceivedUtc
- CapturedUtc
- Schema
- ReportKind
- EditorVersion
- EditorCommit
- InstallIdHash
- ExceptionType
- ExceptionMessageHash
- TopFrameHash
- Fingerprint
- OsFamily
- OsVersion
- CpuSummaryHash
- GpuVendorId
- GpuDeviceId
- GpuDriverVersion
- RendererBackend
- VrRuntime
- ConsentBits
- HasLogs
- HasMiniDump
- HasScreenshot
- RawObjectKey
- Status

TelemetryAttachment
- AttachmentId
- ReportId
- Kind
- ContentType
- SizeBytes
- Sha256
- ObjectKey
- QuarantineStatus

TelemetryFingerprintGroup
- Fingerprint
- FirstSeenUtc
- LastSeenUtc
- Count
- LatestEditorVersion
- RepresentativeReportId
- TriageStatus
```

Do not store contact information directly on the anonymous report row. If user
comments or contact emails are allowed, store them in a separate table with
stricter access controls.

### 8.4 Fingerprinting

A server-side fingerprint should be stable across local path differences:

```text
fingerprint = SHA256(
    reportKind + "|" +
    exceptionType + "|" +
    normalizedTopFrame + "|" +
    normalizedSecondFrame + "|" +
    rendererBackend + "|" +
    nativeCrashCode
)
```

For native crashes:

- include exception code
- crashing module name
- normalized top native frames if available
- GPU driver module if relevant

Keep a separate "similarity" grouping later for crashes with the same GPU driver
or renderer backend but different stacks.

### 8.5 ASP.NET Core Sketch

The real service can be MVC controllers, minimal APIs, or a worker-backed ingest
pipeline. This sketch shows the shape, not final production code.

```csharp
using System.IO.Compression;
using System.Text.Json;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("telemetry-ingest", limiter =>
    {
        limiter.PermitLimit = 60;
        limiter.Window = TimeSpan.FromMinutes(1);
    });
});

builder.Services.AddSingleton<TelemetryReportValidator>();
builder.Services.AddSingleton<TelemetrySanitizer>();
builder.Services.AddSingleton<TelemetryStorage>();

var app = builder.Build();

app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapPost("/api/telemetry/v1/reports", async (
    HttpRequest request,
    TelemetryReportValidator validator,
    TelemetrySanitizer sanitizer,
    TelemetryStorage storage,
    CancellationToken cancellationToken) =>
{
    if (!request.HasJsonContentType() && !request.HasFormContentType)
        return Results.BadRequest(new { error = "unsupported_content_type" });

    TelemetryEnvelope envelope;
    IReadOnlyList<TelemetryUploadAttachment> attachments;

    if (request.HasJsonContentType())
    {
        envelope = await JsonSerializer.DeserializeAsync<TelemetryEnvelope>(
            request.Body,
            TelemetryJson.Options,
            cancellationToken) ?? throw new BadHttpRequestException("Invalid envelope.");
        attachments = [];
    }
    else
    {
        IFormCollection form = await request.ReadFormAsync(cancellationToken);
        string? envelopeJson = form["envelope"];
        if (string.IsNullOrWhiteSpace(envelopeJson))
            return Results.BadRequest(new { error = "missing_envelope" });

        envelope = JsonSerializer.Deserialize<TelemetryEnvelope>(
            envelopeJson,
            TelemetryJson.Options) ?? throw new BadHttpRequestException("Invalid envelope.");

        attachments = form.Files
            .Select(file => new TelemetryUploadAttachment(file.FileName, file.ContentType, file.Length, file))
            .ToArray();
    }

    TelemetryValidationResult validation = validator.Validate(envelope, attachments);
    if (!validation.Accepted)
        return Results.BadRequest(new { error = validation.ErrorCode });

    TelemetryEnvelope sanitized = sanitizer.Sanitize(envelope);
    TelemetryReceipt receipt = await storage.StoreAsync(
        sanitized,
        attachments,
        request.HttpContext.Connection.RemoteIpAddress,
        cancellationToken);

    return Results.Accepted($"/api/telemetry/v1/reports/{receipt.ReportId}/status", receipt);
})
.RequireRateLimiting("telemetry-ingest")
.RequireAuthorization("TelemetryIngest")
.WithName("IngestTelemetryReport")
.WithOpenApi();

app.MapGet("/api/telemetry/v1/reports/{reportId}/status", async (
    string reportId,
    TelemetryStorage storage,
    CancellationToken cancellationToken) =>
{
    TelemetryReceipt? receipt = await storage.GetReceiptAsync(reportId, cancellationToken);
    return receipt is null ? Results.NotFound() : Results.Ok(receipt);
});

app.Run();
```

Production hardening:

- configure `KestrelServerLimits.MaxRequestBodySize`
- use per-endpoint `[RequestSizeLimit]` or minimal API metadata
- reject unexpected attachment MIME types
- stream attachments directly to quarantine/object storage
- scan attachments before making them available to humans
- put a queue between HTTP intake and expensive symbolication/grouping work
- use authenticated maintainer access for triage views
- add audit logs for reading reports and downloading attachments

### 8.6 Server DTO Shape

DTOs should tolerate missing optional fields and unknown future properties:

```csharp
public sealed record TelemetryEnvelope(
    string Schema,
    string ReportId,
    string ReportKind,
    DateTimeOffset CapturedUtc,
    TelemetryEditorInfo Editor,
    TelemetryConsent Consent,
    TelemetryExceptionInfo? Exception,
    TelemetryMachineInfo? Machine,
    TelemetryRendererInfo? Renderer,
    TelemetryAttachmentManifestItem[] Attachments);

public sealed record TelemetryConsent(
    int ConsentVersion,
    string[] AllowedDataClasses);

public sealed record TelemetryReceipt(
    string ReportId,
    DateTimeOffset ServerReceivedUtc,
    string Fingerprint,
    string Status);
```

The server must not trust the client's consent bits blindly. Consent bits are a
contract validation input: if the payload includes a machine section but
`MachineSpecs` is absent, reject or strip that section and flag the client bug.

### 8.7 Retention And Access

Recommended defaults:

- envelope-only reports: keep 180 days
- logs attachments: keep 90 days
- minidumps: keep 30 days unless promoted to an active investigation
- contact info: keep only while the issue is active
- delete by install id if a user requests removal and the install id is known

Access rules:

- raw reports and attachments are maintainer-only
- minidumps require elevated permission and audit logging
- aggregate dashboards should use grouped counts without exposing raw logs
- public issue creation should use manually curated summaries, not raw payloads

## 9. Client Implementation Phases

### Phase 0 - Decisions And Schema

- [ ] Confirm initial telemetry endpoint ownership outside this repo.
- [ ] Decide whether anonymous reports are enough or whether editor accounts
  will later attach identity.
- [ ] Finalize `TelemetryDataClass` names and consent copy.
- [ ] Finalize envelope schema v1 and size limits.
- [ ] Add documentation for what is and is not collected.

Acceptance criteria:

- [ ] A schema fixture can be reviewed without needing the server.
- [ ] Consent categories map one-to-one to payload sections.

### Phase 1 - Local Drafts And Preview

- [ ] Add `TelemetryConsentSettings` to editor preferences.
- [ ] Add `TelemetryEnvelope` DTOs under a runtime/editor diagnostics namespace.
- [ ] Implement `TelemetrySanitizer` with unit tests.
- [ ] Implement `TelemetryOutbox` writing sanitized JSON drafts locally.
- [ ] Add a manual "Create Test Report" and preview UI.

Acceptance criteria:

- [ ] With telemetry disabled, no report is created or sent.
- [ ] With test report enabled, the preview contains only consented sections.
- [ ] Redaction tests cover project root, user profile, secret env names, token
  URL query parameters, and long-line truncation.

### Phase 2 - Managed Exception Reports

- [ ] Integrate unhandled managed exceptions and unobserved task exceptions with
  `TelemetryEventRecorder`.
- [ ] Capture latest log session references and consented excerpts.
- [ ] Detect previous abnormal shutdown on next startup.
- [ ] Add ask-before-send crash prompt.

Acceptance criteria:

- [ ] Fatal managed exception writes a local report draft without network I/O.
- [ ] Next editor launch prompts according to consent settings.
- [ ] First-chance exceptions are not uploaded unless explicitly enabled and
  throttled.

### Phase 3 - HTTPS Sender

- [ ] Implement `TelemetryHttpSender` with retry, backoff, idempotency key, and
  endpoint override.
- [ ] Support JSON-only upload first.
- [ ] Add logs attachment upload after envelope-only path is stable.
- [ ] Expose send status in preferences.

Acceptance criteria:

- [ ] Sender uses no render/update hot-path hooks.
- [ ] Offline mode leaves reports pending without startup stalls.
- [ ] Server 4xx failures become final failures with clear status.
- [ ] Server 5xx/network failures retry with bounded backoff.

### Phase 4 - Native Crash Dumps

- [ ] Choose crash dump approach:
  - helper process watchdog, or
  - Windows Error Reporting LocalDumps integration, or
  - direct `MiniDumpWriteDump` where safe.
- [ ] Capture minidumps locally only.
- [ ] Prompt per crash before upload by default.
- [ ] Add size warnings and separate retention controls.

Acceptance criteria:

- [ ] Native crash produces a local dump or a clear reason why no dump was
  captured.
- [ ] Minidump upload never occurs without `CrashMiniDump` consent.
- [ ] User can delete pending dumps from the UI.

### Phase 5 - Triage And Feedback Loop

- [ ] Add server-side grouping/fingerprinting.
- [ ] Add maintainer dashboard or export.
- [ ] Add symbolication for managed and native frames.
- [ ] Add release note workflow for fixed crash fingerprints.

Acceptance criteria:

- [ ] Reports group by normalized top frames and renderer context.
- [ ] Maintainers can find "top crashes by editor version and GPU driver".
- [ ] Fix verification can search for absence/reduction of a fingerprint in
  later builds.

## 10. Test Plan

Client unit tests:

- consent matrix omits forbidden sections
- `XRE_TELEMETRY_DISABLE=1` prevents sending
- sanitizer redacts user paths, project paths, URL tokens, and known secret
  names
- outbox writes atomically and survives partial files
- envelope serialization round-trips with optional sections missing
- report size caps truncate logs deterministically
- first-chance exception sampling is throttled

Client integration tests:

- local fake HTTP server receives a test report
- retry/backoff works for 500 and connection failure
- 400 response marks report final failed
- deleting pending report stops retries
- managed unhandled exception writes local draft in a subprocess harness

Server tests:

- rejects unknown schema when configured strict
- rejects optional payload sections missing matching consent bits
- rejects oversized payloads and unexpected attachment kinds
- deduplicates repeated idempotency keys
- computes stable fingerprint independent of local paths
- stores attachments in quarantine and indexes metadata

Manual validation:

- enable telemetry, send test report, inspect preview and server receipt
- disable telemetry, confirm no network calls
- simulate offline server, confirm no startup stall and pending status is clear
- simulate crash restart flow
- verify logs are redacted before leaving the client

## 11. Open Questions

- Who owns the production telemetry endpoint and data retention policy?
- Should reports be purely anonymous for v1, or should a future editor account
  flow support optional authenticated reports?
- Should the first version support minidumps, or should that wait until the JSON
  report path has earned trust?
- Which machine-spec provider should be used for GPU driver information:
  renderer backend queries, WMI, DXGI, Vulkan physical-device properties, or a
  layered approach?
- Should project metadata use salted hashes so maintainers can correlate repeated
  crashes within one project without learning project names?
- Should studios get a policy file to force-disable telemetry across a team
  checkout regardless of user preferences?

## 12. Recommended V1

For the first implementation, keep it small:

1. Preferences consent UI.
2. Sanitized JSON envelope.
3. Managed unhandled/unobserved exception capture.
4. Local outbox and preview.
5. Manual send and ask-before-send.
6. HTTPS JSON upload to a generic ASP.NET Core endpoint.

Leave minidumps, screenshots, automatic sends, and rich dashboards for later.
That gives XRENGINE useful crash intelligence while keeping trust, scope, and
implementation risk under control.

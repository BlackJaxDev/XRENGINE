# Maintaining Poiyomi Toon Support

Poiyomi support is versioned. A new upstream release is unsupported until its
source identity, catalog diff, classifications, fixtures, references, and full
validation have been reviewed together.

## Update Procedure

1. Check out the candidate Poiyomi repository at an immutable commit. Confirm
   its license permits open-source and commercial engine use.
2. Run the compact source audit against the currently embedded catalog:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Reports\Test-PoiyomiSourceVersion.ps1 `
     -PoiyomiRoot C:\src\PoiyomiToonShader `
     -ReportPath Build\_AgentValidation\00000000-000000-shared\reports\poiyomi-source-version-audit.json `
     -FailOnChanges
   ```

   `-FailOnChanges` intentionally fails for any property, pass, annotation, or
   workflow change while still writing a concise JSON report.
3. Review every addition, removal, and changed signature in the report. Do not
   infer compatibility from the package version string alone; the embedded
   ThryEditor grammar can change independently.
4. Update `PoiyomiToon93Catalog` identity constants or introduce a separately
   named version catalog. Never silently reuse the old matcher for a new source.
5. Regenerate the candidate catalog:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Reports\Generate-PoiyomiToon93Catalog.ps1 `
     -PoiyomiRoot C:\src\PoiyomiToonShader
   ```
6. Classify every changed runtime property, render state, annotation, display
   option, action, and workflow as exact, native equivalent, preserved inactive,
   inspector-only, or internal data. No runtime-visible entry may remain
   unclassified.
7. Update focused and maximal corpus materials, schema snapshots, animation
   bindings, render presets, and affected Unity reference images. Record source
   values, source version, Unity version, and redistribution license.
8. Regenerate the parity table and review its diff:

   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\Tools\Reports\Generate-PoiyomiParityReport.ps1
   ```
9. Update the public conversion guide, diagnostic remediation, native semantic
   differences, and attribution when behavior or upstream code changes.
10. Run `Tools/Validate-PoiyomiParity.ps1` and review OpenGL/Vulkan images. Use
    RenderDoc when a pass/resource discrepancy is not explained by captures or
    logs.

A support declaration is invalid without both a reviewed catalog diff and
updated fixtures for every affected feature family.

## Review Requirements

Poiyomi changes must include:

- what changed and why;
- old and new immutable source identities;
- the compact source-audit report;
- property/pass/UI/workflow classifications;
- fixture and reference changes;
- automated and live-backend validation;
- native-equivalent behavior and semantic differences;
- remaining preserved-inactive integrations and risks;
- license/attribution changes.

Generated MCP documentation is updated only when a public MCP tool or editor
workflow changes. Catalog-only or shader-only changes do not regenerate MCP
docs.

## Safety Invariants

- Raw source values survive unsupported mappings and reimport.
- Unknown versions fail safely.
- External providers are explicit service contracts and report absence once per
  material/import, never once per frame.
- Imported metadata never executes reflection, arbitrary code, implicit network
  requests, or unapproved filesystem writes.
- Repeated-slot support uses shared generated code and dependency pruning rather
  than unbounded source duplication.
- Per-frame binding and submission remain allocation-free.

# Poiyomi Import Reporting And Reimport

import reporting makes material conversion inspectable, deterministic, and safe to
repeat. Every Unity material import now returns a `MaterialConversionReport`
alongside the generated `XRMaterial`.

## Conversion report

The report records:

- converter and source-descriptor format versions;
- source path and SHA-256, shader family/version, and lock state;
- conversion outcome, generated features and passes;
- exact, native-equivalent, or preserved-inactive parity per enabled source
  feature;
- grouped diagnostics, warnings, failures, and preserved inactive values;
- sampler pressure, generated variant/pass, feature, and unsupported-integration
  counters.

`MaterialConversionReport.ToJson()` excludes timestamps and orders all derived
collections, producing stable JSON for equal source and converter inputs. The
editor's Rendering tools tab presents the same report without requiring access
to logs or shader source. Dormant manifest properties are not synthesized as
features or controls; preserved source data is read-only.

## Imported state and local overrides

`UnityMaterialAsset` stores three separate records:

- `ImportedState` is the converter-owned baseline captured before local edits.
- `LocalOverrides` is the deterministic difference between the live material
  and that baseline.
- `LastConversionReport` records the converter/source versions which produced
  the asset.

Normal reconversion imports a clean material, captures the new converter-owned
baseline, and then reapplies the separated local overrides. Parameter values,
texture slots, feature switches, static/animated property modes, render pass,
and transparency controls participate in this process.

The inspector exposes two operations:

- **Reconvert (preserve overrides)** updates converter-owned state and reapplies
  local edits.
- **Reset overrides and reconvert** discards the separated override set and
  requires an explicit confirmation checkbox.

## Batch audits

`UnityMaterialBatchConversionService.AuditProjectAsync` recursively discovers
Unity `.mat` files below a project root. `AuditAvatarAsync` accepts the material
paths selected by an avatar import. Each material is isolated, so one failure
becomes a failure report instead of aborting the corpus. Aggregate counters and
per-material reports can be written as deterministic JSON with
`WriteJsonAsync`.

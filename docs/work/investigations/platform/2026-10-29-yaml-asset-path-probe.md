# YAML asset path probe on caller-thread hosts

Status: open. `XRAssetDeserializer.AssetPathExists` uses catalog metadata for a
catalog source, but calls `File.Exists` for all other sources. Scalar path
resolution and external-reference resolution call this helper.

At `a11e359dd9f837d885f04d1c7f324bf543648597`, a disposable executable invoked
the helper with no installed source and caller-thread mode active. It returned
true for a real temporary file. A `RuntimeAssetReadLease.Exists` call for the
same file rejected host-file access in that mode. This confirms the helper can
perform a direct host-file probe without the raw-read admission check. The probe
does not establish which complete browser deserialization flows reach the helper.
The executable and log are under the `YamlPathProbe` scratch directory and
`yaml-path-probe.log` in the active agent validation run.

The replacement must preserve catalog lookup and desktop scalar resolution,
including rebasing and missing-file behavior. `TryResolveAssetPathFromScalar`
catches all exceptions and reports an unresolved path, while external-reference
resolution also calls the helper directly. A provider change must account for
both call sites, source replacement, and custom host-file providers. This
finding does not complete the remaining host-file inventory.

# YAML asset path probe on caller-thread hosts

Status: the reproduced caller-thread host probe is fixed. Scalar path
resolution and external-reference resolution call `AssetPathExists`.

At `a11e359dd9f837d885f04d1c7f324bf543648597`, a disposable executable invoked
the helper with no installed source and caller-thread mode active. It returned
true for a real temporary file. A `RuntimeAssetReadLease.Exists` call for the
same file rejected host-file access in that mode. This confirms the helper can
perform a direct host-file probe without the raw-read admission check. The probe
does not establish which complete browser deserialization flows reach the helper.
The executable and log are under the `YamlPathProbe` scratch directory and
`yaml-path-probe.log` in the active agent validation run.

The replacement had to preserve catalog lookup and desktop scalar resolution,
including rebasing and missing-file behavior. `TryResolveAssetPathFromScalar`
catches all exceptions and reports an unresolved path, while external-reference
resolution also calls the helper directly. This finding does not complete the
remaining host-file inventory.

## Fix and validation

The helper now captures a raw-read lease before it selects the source. Catalog
lookups use that lease and retain metadata-only behavior. Other lookups require
host-file admission, then use the installed host-file read backend. If no such
backend is installed, the raw-read service supplies the desktop fallback. The
lease rejects a result when the raw source changes during a probe. Scalar path
resolution still maps lookup exceptions to an unresolved path; direct external
reference lookup can propagate them.

Eight focused tests pass on Linux. They cover a custom provider, the desktop
fallback, missing files, a source replacement during a probe, caller-thread
rejection, catalog metadata, and desktop and caller-thread YAML deserialization.
The test build has zero warnings and errors. This does not verify a live browser
deserialization flow or provider lifetime after an unrelated filesystem swap.

# Catalog prefab loading and non-blocking entry points

The prefab convenience loader previously checked host `File.Exists` before and
after asking the asset manager to load a packaged prefab. A valid browser catalog
asset has no host file at its virtual path, so the helper could return null after
a successful fetch. `LoadPrefabWithReferencesAsync` now delegates catalog-owned
assets to the existing asynchronous catalog loader, including its dependency
closure, type checks, cancellation and session ownership. It does not deserialize
a second graph or start thread-pool workers. Partial YAML inspection stays an
authoring operation and reports a named diagnostic for cooked catalogs.

Catalog dispatch recognizes both an owner constructed with a catalog and a
catalog bound afterward. The original catalog owner remains non-file-backed
when it is temporarily unbound. Synchronous authoring remote-job wrappers reject
non-blocking/catalog hosts before starting work; explicit asynchronous local-mode
requests use the catalog loader. Unloaded asset-ID lookup cannot scan desktop
metadata or use authoring remote jobs in this mode: it requires the packaged
path. Already loaded IDs remain resolvable, including the synchronous cache-only fast
path. Catalog write rejection occurs both before save jobs are admitted and
inside their work, so an intervening unbind cannot authorize a queued host write.

The blocking asset-job helper now rejects waiting on a caller-thread executor
instead of enqueueing work that the blocked caller must pump. Explicit inline
CPU work and desktop worker execution retain their existing routes. The generic
`RunSync` extension rejects browser calls before scheduling asynchronous work.
These are bounded corrections, not a claim that every runtime-reachable file,
thread or blocking site has been eliminated. Whole-inventory and live prefab
qualification remain open. Validation results will be recorded for the final
coherent source snapshot.

## Targeted validation

The integrated Browser build includes these changes and reports zero warnings
and errors. A disposable native probe uses the real generic cooked serializer,
`AssetManager`, and an asynchronously yielding catalog source: the prefab
hydrates, its external text dependency is loaded first, repeated path and loaded-
ID requests preserve cached identity, cancellation is honored, and named guards
reject partial YAML, remote-job and synchronous wrapper paths without starting
extra I/O. The probe's initial name assertion was corrected to account for the
existing `PostLoaded` behavior that normalizes an asset name from its file path;
production behavior was unchanged. Evidence is under the active run's
`scratch/catalog-prefab-probe/` directory. This is CPU asset-path evidence, not
browser transport, a full populated prefab-world run or the complete I/O audit.

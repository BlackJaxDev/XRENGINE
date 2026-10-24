# Native Window Layout Key Compatibility

Status: Proposed; owner approval pending
Scope: Saved layout keys for the current single-primary editor profile
Related work: [Native UI TODO](../../todo/ui/native-ui-retained-performance-and-editor-parity-todo.md)

## Problem

`EditorPanelHost.GetWindowKey` derives the primary key from its runtime window
index and title. A title override or changed window order can select a different
saved layout. Detached hosts already use explicit `detached:<guid>` keys.

The dictionary is `NativeDockLayoutsJson` in the global editor preferences asset:
`<user-data-root>/Global/Config/editor_preferences_global.asset`. The normal
desktop user-data root is `%LOCALAPPDATA%/XREngine`. Isolated sessions use
`<session-root>/user-data`. The proposal does not change either location.

## Proposed Compatibility Rule

Use `primary` for the current single-primary editor profile. During load:

1. Prefer an existing valid `primary` layout.
2. Otherwise, read the exact current legacy key, `0:<current title>`.
3. Otherwise, read the known default key, `0:XRE Editor`.
4. Preserve unmatched keys. Do not infer a mapping from dictionary order or from
   several old index-zero entries. Report that the old layout needs an explicit
   selection instead of silently replacing it.

After approval, a normal save can copy the selected layout under `primary`.
Keep all old keys and values. Never delete an old record as part of this change.
Malformed preference data cannot authorize an automatic rewrite. Detached keys
and per-window close/reopen semantics remain unchanged. This proposal does not
make panel types globally unique across windows.

If a profile creates more than one primary editor host, retain its existing
behavior until it has explicit durable host identities. Do not assign both hosts
the same new key. New startup identifiers are outside this proposal.

## Validation

Pure tests cover canonical-key preference, exact legacy lookup, default-title
lookup, title overrides, unmatched and ambiguous records, malformed data,
preservation of old entries, and unchanged detached keys. A Windows check must
confirm layout restore after restart and a title override. No platform result
is claimed by this proposal.

## Approval Boundary

Copying a saved layout to a new key is a data migration. `AGENTS.md` requires
owner approval before data/storage migrations. The lookup/copy rule above is
proposed for that approval; no key migration is implemented yet.

Optional detached-window geometry fields are a separate additive change. Their
old-record defaults and close-approval ownership can be implemented without this
key migration. The shutdown-intent design must preserve existing shutdown state,
close ordering, and close-veto behavior.

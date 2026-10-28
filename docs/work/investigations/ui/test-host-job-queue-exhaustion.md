# Test Host Job Queue Exhaustion

Status: Fix pushed; waiting for a full Windows run.

## Problem

Broad Windows runs of the editor and UI tests hung in `NativeHierarchyMenuTests`. The hung test
changed between runs. The first hang appeared after the asset creation recovery commit, but that
commit only added tests. It did not cause the hang.

## Findings

- The hung thread waits in `AssetManager.RunOnJobThreadBlocking`. The call chain is
  `HierarchyPanel.SaveHierarchyState` during destroy, `NativeHierarchyStateStore.Save`, and
  `Engine.SaveGlobalEditorPreferences`.
- All general worker threads are idle. The bounded job queue has no free slot (0 of 8192). The
  save job waits in the deferred list for a slot.
- A heap dump shows 7872 queued app-thread jobs and 320 queued collect-visible-swap jobs. Each holds
  one queue slot. Together they use all 8192 slots.
- The app-thread jobs are `LabeledActionJob` instances labeled `UI.DrawCommand.SourceChanged`, from
  `UIDrawCommandObserver.PropertyChanged`. The swap jobs come from
  `EngineRenderingSettingsApplication.ApplyRenderMeshBoundsSetting`.
- The test host has no app or render loop, so nothing runs these jobs. Each UI test adds some.
  After about 360 tests, the queue is full. More tests in the filter made the hang appear.
- A separate problem: `EditorLinkedListVirtualizationTests.VectorEditorKeepsItsNestedBindingsAfterRecycle`
  left a deferred property refresh in the editor scene-edit queue. The next
  `NativeHierarchyMenuTests` setup then failed on `Edits.IsEmpty`.

Ruled out: the recovery store, `MaterialTextureSlotActionTests`, `HierarchySpawnTests`, and the
hierarchy test classes. Each runs clean together with the menu tests.

## Fix

- `XREngine.UnitTests/PumpThreadAffineJobsAttribute.cs` runs the queued app-thread and
  collect-visible-swap jobs on the test thread after each test.
- `EditorLinkedListVirtualizationTests` clears the scene-edit queue in its teardown.

Production behavior does not change. In the editor, the app loop runs these jobs every frame.

## Next step

Run the full editor test filter on Windows with `--blame-hang`. Record the counts here.

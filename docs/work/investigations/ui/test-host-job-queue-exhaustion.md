# Test Host Job Queue Exhaustion

Status: Fixed. The full filter passes on Windows.

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

## Validation

At `5b0f52f8` on Windows, with `--blame-hang --blame-hang-timeout 5m`:

- The native UI filter, which includes `NativeAnimationClipViewTests` and
  `NativeAnimationPanelViewTransferTests`: 651 of 651 passed. No hang.
- The whole `XREngine.UnitTests.Editor` namespace: 1058 of 1067 passed, 9 failed, no hang. The
  failures are in `AnimationLengthAdapterTests`, `EditorListMutationTests`,
  `HierarchyCloneReplayAdmissionTests`, and `RollingBallWorldAssetContractTests`. None of these files
  changed in the fix. All 9 fail with the same messages at `b1332e60`, so they are older failures.
  `7bb1c819` fixes the two `EditorListMutationTests` cases: the test now passes the optional owner
  check to the `CollectionEditorContext` constructor. The other 7 remain open outside this work:
  `AnimationLengthAdapterTests.AuthoredCadenceLengthKeepsFrameCadenceAndPlaybackBoundary`, four
  `HierarchyCloneReplayAdmissionTests` cases ("The hierarchy edit did not preserve its requested
  values."), and two `RollingBallWorldAssetContractTests` cases that check source text.

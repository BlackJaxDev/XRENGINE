# Phase 10 humanoid conformance corpus

`manifest.json` is the content-addressed declaration for Phase 10. It is
deliberately Unity-free at validation and execution time: Unity is only used to
produce checked-in pose-audit reference artifacts.

Every checked-in `.anim` and `.fbx` has an `AssetChecks` entry. The executable
classifications are intentionally separate from filename and fixture provenance:

| Classification | Meaning |
| --- | --- |
| `HumanoidMatrixAvatar` | A valid model that participates in humanoid playback matrix rows. |
| `AnimationBehaviorAndImport` | A repository walk that must import and execute through the runtime route checks. |
| `AnimationImport` | A serialized animation fixture that must preserve/import the declared semantic domains. |
| `ValidModelImport` | A model expected to import successfully, whether or not it is humanoid. |
| `ExpectedMalformedModelImport` | A deliberately malformed model expected to fail import with a diagnostic. |

`RequiredCoverage` is not satisfied by manifest declarations. The runner must
emit `HumanoidConformanceAssetCheckResult` and
`HumanoidConformanceMatrixCheckResult` observations, then call
`HumanoidConformanceCoverageEvaluator.Evaluate`. This prevents capability masks
from passing without an observed import or playback check.

Known-answer references bind source hashes, import settings, coordinate spaces,
tolerances, and a content-addressed `CaptureTools` source. The loader rejects
missing files, changed hashes, unknown reference artifacts, stale capture-tool
identities, and `PENDING` placeholders. A corpus with placeholder references is
therefore intentionally invalid until generated Unity audit files are checked
in.

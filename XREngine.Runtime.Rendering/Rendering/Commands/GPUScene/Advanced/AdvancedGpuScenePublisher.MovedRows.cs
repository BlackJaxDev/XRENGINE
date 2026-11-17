using System.Numerics;
using XREngine.Data.Rendering;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Publishes moved rows without the whole-scene plan. The unchanged-row gate accepts a supported
/// row whose only change is its world matrix, its previous world matrix, or (for a CPU-owned
/// bound) its cull bound, when the row is already a dynamic shadow caster. The publication then
/// commits the retained rows and replaces the transform and instance records of each moved row,
/// as the content update of the whole-scene plan does (<see cref="UpdateRegistration"/>). Any
/// other change, a static row that moves, or more than <see cref="MovedRowPatchLimit"/> moved
/// rows runs the whole-scene plan.
/// </summary>
/// <remarks>
/// The moved-row storage has a fixed size, so the gate and the patch do not allocate. The
/// patch writes the new matrices, bound, and content signature back to the retained plan entry
/// and to the registration, so the next gate compares against the published state.
/// </remarks>
public sealed partial class AdvancedGpuScenePublisher
{
    /// <summary>
    /// Largest number of moved rows that one publication patches. A publication with more moved
    /// rows runs the whole-scene plan, which also handles many changes at once.
    /// </summary>
    public const int MovedRowPatchLimit = 64;

    private readonly int[] _movedRowIndices = new int[MovedRowPatchLimit];
    private readonly Matrix4x4[] _movedRowWorlds = new Matrix4x4[MovedRowPatchLimit];
    private readonly Matrix4x4[] _movedRowPreviousWorlds = new Matrix4x4[MovedRowPatchLimit];
    private readonly BoundsGpu[] _movedRowCullBounds = new BoundsGpu[MovedRowPatchLimit];
    private int _movedRowCount;
    private int _lastMovedRowPatchCount;

    /// <summary>Gets the number of rows that the latest publication patched as moved rows; 0 when it patched none.</summary>
    public int LastMovedRowPatchCount => _lastMovedRowPatchCount;

    /// <summary>
    /// Returns true when the gate can patch a moved row instead of running the whole-scene
    /// plan: the row is supported, it has a registration, and its planned draw row is a dynamic
    /// shadow caster. A static caster that starts to move is promoted to dynamic, and the next
    /// swap carries the new flag, so the row takes the whole-scene plan once and qualifies after
    /// that. The registration comes from the ordered identity: the plan entry of a row that the
    /// last plan registered still holds -1, because the registration happens after the plan.
    /// </summary>
    private static bool CanPatchMovedRow(
        in AdvancedGpuSceneCommandTransition plan,
        in OrderedRegistrationIdentity identity)
        => plan.Supported &&
           identity.RegistrationIndex >= 0 &&
           (plan.Command.Flags & (uint)GPUIndirectRenderFlags.ShadowCasterDynamic) != 0u;

    /// <summary>
    /// Stores one moved row that passed every other gate check. Returns false, with the fallback
    /// recorded, when the publication already holds <see cref="MovedRowPatchLimit"/> moved rows.
    /// </summary>
    private bool TryRecordMovedRow(int commandIndex, ref UnchangedRowGateScratch scratch)
    {
        if (_movedRowCount >= MovedRowPatchLimit)
        {
            AdvancedMovedRowPatchTelemetry.RecordFallback(EAdvancedMovedRowPatchFallback.LimitExceeded);
            return false;
        }

        int slot = _movedRowCount++;
        _movedRowIndices[slot] = commandIndex;
        _movedRowWorlds[slot] = scratch.RowMovedWorld;
        _movedRowPreviousWorlds[slot] = scratch.RowMovedPreviousWorld;
        _movedRowCullBounds[slot] = scratch.RowMovedCullBounds;
        return true;
    }

    /// <summary>
    /// Checks, before the publication opens, that every moved row can be patched in this
    /// publication: its transform and instance records are current, and the tables can take
    /// two transform replacements and one instance replacement for each row. Records the
    /// fallback reason and returns false otherwise.
    /// </summary>
    private bool CanApplyMovedRowPatches()
    {
        int count = _movedRowCount;
        if (!Database.Scene.Transforms.CanApply(0, 2 * count, 0) ||
            !Database.Scene.Instances.CanApply(0, count, 0))
        {
            AdvancedMovedRowPatchTelemetry.RecordFallback(EAdvancedMovedRowPatchFallback.TableCapacity);
            return false;
        }

        for (int slot = 0; slot < count; ++slot)
        {
            int registrationIndex = _orderedRegistrationIdentities[_movedRowIndices[slot]].RegistrationIndex;
            if (registrationIndex < 0 || registrationIndex >= _registrationCount)
            {
                AdvancedMovedRowPatchTelemetry.RecordFallback(EAdvancedMovedRowPatchFallback.RegistrationNotCurrent);
                return false;
            }
            ref readonly AdvancedResidentRegistration registration = ref _registrations[registrationIndex];
            if (!registration.Active ||
                !Database.Scene.Transforms.IsCurrent(registration.CurrentTransform) ||
                !Database.Scene.Transforms.IsCurrent(registration.PreviousTransform) ||
                !Database.Scene.Instances.IsCurrent(registration.Instance))
            {
                AdvancedMovedRowPatchTelemetry.RecordFallback(EAdvancedMovedRowPatchFallback.RegistrationNotCurrent);
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Replaces the transform and instance records of each moved row inside the open
    /// publication, and writes the new matrices, bound, and content signature to the retained
    /// plan entry and to the registration. A CPU-owned bound takes the cull bound that the gate
    /// read; a GPU-owned bound keeps its planned value. Advances the content generation once per
    /// row, as the content update of the whole-scene plan does.
    /// </summary>
    private void ApplyMovedRowPatches()
    {
        for (int slot = 0; slot < _movedRowCount; ++slot)
        {
            int commandIndex = _movedRowIndices[slot];
            ref AdvancedGpuSceneCommandTransition plan = ref _plannedCommands[commandIndex];
            int registrationIndex = _orderedRegistrationIdentities[commandIndex].RegistrationIndex;
            ref AdvancedResidentRegistration registration = ref _registrations[registrationIndex];

            Matrix4x4 world = _movedRowWorlds[slot];
            Matrix4x4 previousWorld = _movedRowPreviousWorlds[slot];
            bool gpuOwnedBounds = plan.GpuBoundsSource is not null;
            BoundsGpu bounds = gpuOwnedBounds ? plan.Bounds : _movedRowCullBounds[slot];
            ulong content = ComputeContentSignature(
                in plan.Command,
                in bounds,
                in world,
                in previousWorld,
                plan.MaterialSource,
                gpuOwnedBounds,
                plan.SourceFlatDirectionOct,
                plan.SourceFlatDirectionValid);

            if (!Database.Scene.Transforms.TryReplace(
                    registration.PreviousTransform,
                    CreateTransform(previousWorld)) ||
                !Database.Scene.Transforms.TryReplace(
                    registration.CurrentTransform,
                    CreateTransform(world, plan.Source as RenderCommand)) ||
                !Database.Scene.Instances.TryReplace(
                    registration.Instance,
                    CreateInstance(world, previousWorld, in bounds, in plan.Command)))
            {
                throw new InvalidOperationException("Moved-row patch failed after successful preflight.");
            }

            plan.World = world;
            plan.PreviousWorld = previousWorld;
            plan.Bounds = bounds;
            plan.ContentSignature = content;
            registration.World = world;
            registration.ContentSignature = content;
            ++_contentDeltaCount;
            AdvanceNonZero(ref _contentGeneration);
        }
    }
}

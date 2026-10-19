using XREngine.Rendering.Materials;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Prepares the unchanged-row gate decision on the collect thread, after
/// visibility collection and before the serial swap. The next publication
/// consumes the verdict in place of the swap-time row walk.
/// </summary>
/// <remarks>
/// <para>
/// Only the gate decision moves. The boundary sizing, the tombstone reclaim,
/// the global preflight, the transaction, the row stamps, the seal, the
/// captures, the current publication, and the identity delivery stay in the
/// swap. The prepare writes only the verdict fields of this partial class.
/// </para>
/// <para>
/// The prepare evaluates every row with the shared row evaluator. It samples
/// each epoch witness before the first row read, and each writer of an
/// epoch-witnessed input advances its epoch after the write. The mesh,
/// material, and options witnesses hold the values that the rows compared.
/// The consume compares each witness at the swap. A row whose command state
/// the witnesses do not cover, or whose command version changed after the
/// prepare, gets the swap-time evaluation at the consume. Any miss runs the
/// swap-time gate, which falls back to the full plan. Thus a verdict never
/// publishes a stale row, never publishes an empty scene, and never uses a
/// CPU fallback.
/// </para>
/// <para>
/// The prepare and the consume run on one thread, and a verdict is single
/// use. Storage for the verdict grows only in the swap-time capacity methods.
/// When the storage is too small, the prepare refuses and does not allocate.
/// </para>
/// </remarks>
public sealed partial class AdvancedGpuScenePublisher
{
    // Bits of the prepared result of one row.
    private const byte PreparedRowSupported = 1;
    private const byte PreparedRowReusedStaticPlan = 2;
    private const byte PreparedRowReevaluate = 4;

    // The native raster-state flags occupy the high byte of a render-state flag
    // word. CreateRenderState keeps the structural draw flags in the low bits.
    private const uint RasterStateFlagMask = 0xFF000000u;

    // One warning text for each gate result. A verify disagreement then passes
    // no format arguments, so the warning call allocates nothing.
    private static readonly string[] s_verifyDisagreementMessages = CreateVerifyDisagreementMessages();

    private AdvancedPreparedUnchangedRowGate _preparedUnchangedRowGate;
    private long[] _preparedRowMutationVersions = [];
    private byte[] _preparedRowFlags = [];
    private AdvancedPreparedMeshWitness[] _preparedMeshWitnesses = [];
    private AdvancedPreparedMaterialWitness[] _preparedMaterialWitnesses = [];
    private AdvancedPreparedOptionsWitness[] _preparedOptionsWitnesses = [];
    // The used length of each witness array. The release clears these entries,
    // so that no mesh, material, or options object stays reachable after its verdict.
    private int _preparedMeshWitnessCount;
    private int _preparedMaterialWitnessCount;
    private int _preparedOptionsWitnessCount;
    // The global resource count of the last boundary sizing. The prepare uses
    // it to predict the boundary requirement of the next publication.
    private uint _lastBoundaryGlobalResourceCount;

    /// <summary>
    /// Gets whether the collect thread prepares the unchanged-row verdict.
    /// Set XRE_ADVANCED_PREPARED_GATE=0 before launch to disable it. Then each
    /// publication runs the swap-time gate.
    /// </summary>
    internal static bool PreparedGateEnabled { get; } = !string.Equals(
        Environment.GetEnvironmentVariable("XRE_ADVANCED_PREPARED_GATE"),
        "0",
        StringComparison.Ordinal);

    /// <summary>
    /// Gets whether each accepted verdict also runs the swap-time gate. Set
    /// XRE_ADVANCED_PREPARED_GATE_VERIFY=1 before launch to enable it. The
    /// publication then uses the swap-time result and counts each disagreement.
    /// </summary>
    internal static bool PreparedGateVerifyEnabled { get; } = string.Equals(
        Environment.GetEnvironmentVariable("XRE_ADVANCED_PREPARED_GATE_VERIFY"),
        "1",
        StringComparison.Ordinal);

    /// <summary>
    /// Prepares the unchanged-row verdict of the next publication. Call it on
    /// the thread that runs the next publication, after visibility collection
    /// and before the swap. It removes any earlier verdict first, and it
    /// records one result in <see cref="AdvancedPreparedGateTelemetry"/>.
    /// </summary>
    internal void PrepareUnchangedRowGate(GPUScene scene)
    {
        DiscardPreparedUnchangedRowGate();
        EAdvancedPreparedGateOutcome outcome = TryPrepareUnchangedRowGate(scene);
        if (outcome == EAdvancedPreparedGateOutcome.Prepared)
        {
            AdvancedPreparedGateTelemetry.RecordPrepared();
            return;
        }

        DiscardPreparedUnchangedRowGate();
        AdvancedPreparedGateTelemetry.RecordRefusal(outcome);
    }

    /// <summary>
    /// Removes the prepared verdict and releases its witness references.
    /// </summary>
    internal void DiscardPreparedUnchangedRowGate()
    {
        _preparedUnchangedRowGate = default;
        ReleasePreparedWitnesses();
    }

    /// <summary>
    /// Returns the prepared verdict and removes it, so that a verdict is single use.
    /// </summary>
    private AdvancedPreparedUnchangedRowGate TakePreparedUnchangedRowGate()
    {
        AdvancedPreparedUnchangedRowGate prepared = _preparedUnchangedRowGate;
        _preparedUnchangedRowGate = default;
        return prepared;
    }

    /// <summary>Clears the used witness entries.</summary>
    private void ReleasePreparedWitnesses()
    {
        Array.Clear(_preparedMeshWitnesses, 0, _preparedMeshWitnessCount);
        Array.Clear(_preparedMaterialWitnesses, 0, _preparedMaterialWitnessCount);
        Array.Clear(_preparedOptionsWitnesses, 0, _preparedOptionsWitnessCount);
        _preparedMeshWitnessCount = 0;
        _preparedMaterialWitnessCount = 0;
        _preparedOptionsWitnessCount = 0;
    }

    /// <summary>
    /// Grows the verdict storage to the boundary capacity. Only the swap-time
    /// capacity method calls this. Without the prepare, the storage stays empty.
    /// </summary>
    private void EnsurePreparedGateCapacity(int capacity)
    {
        if (!PreparedGateEnabled)
            return;

        if (_preparedRowFlags.Length < capacity)
        {
            Array.Resize(ref _preparedRowFlags, capacity);
            Array.Resize(ref _preparedRowMutationVersions, capacity);
        }
        if (_preparedMeshWitnesses.Length < capacity)
            Array.Resize(ref _preparedMeshWitnesses, capacity);
        if (_preparedMaterialWitnesses.Length < capacity)
            Array.Resize(ref _preparedMaterialWitnesses, capacity);
        if (_preparedOptionsWitnesses.Length < capacity)
            Array.Resize(ref _preparedOptionsWitnesses, capacity);
    }

    /// <summary>
    /// Returns the boundary-sized capacity for a command count and a global
    /// resource count. Every boundary-sized array grows only from this value.
    /// </summary>
    private uint ComputeBoundaryRequirement(uint commandCount, uint globalResourceCount)
        => Math.Max(
            InitialCapacity,
            NextPowerOfTwo(Math.Max(
                Math.Max(commandCount + 1u, globalResourceCount + 1u),
                checked((uint)_registrationCount + 1u))));

    /// <summary>
    /// Returns true when the next boundary sizing would change the boundary,
    /// with the global resource count of the last sizing. The next publication
    /// then skips the gate, so a verdict has no use.
    /// </summary>
    private bool WouldBoundaryRequirementChange(uint commandCount)
        => ComputeBoundaryRequirement(commandCount, _lastBoundaryGlobalResourceCount) != _lastBoundaryRequired;

    /// <summary>
    /// Evaluates every retained row and stores the verdict. Returns
    /// <see cref="EAdvancedPreparedGateOutcome.Prepared"/> when it stored the
    /// verdict, and otherwise the reason of the refusal.
    /// </summary>
    private EAdvancedPreparedGateOutcome TryPrepareUnchangedRowGate(GPUScene scene)
    {
        // Read the epoch before the in-progress flag. A publication that ends
        // after this read advances the epoch, so the consume then misses.
        long publisherEpoch = PublisherEpoch;
        if (System.Threading.Volatile.Read(ref _publishInProgress) != 0)
            return EAdvancedPreparedGateOutcome.PublisherChanged;
        if (!HasValidUnchangedRowBaseline(scene))
            return EAdvancedPreparedGateOutcome.BaselineInvalid;
        ulong lastStampedFrameId = _lastStampedFrameId;
        if (lastStampedFrameId == 0UL)
            return EAdvancedPreparedGateOutcome.NoStampedFrame;
        if (WouldBoundaryRequirementChange(scene.TotalCommandCount))
            return EAdvancedPreparedGateOutcome.BoundaryGrowth;

        int rowCount = _plannedCommandCount;
        if (rowCount > _preparedRowFlags.Length ||
            rowCount > _preparedRowMutationVersions.Length ||
            rowCount > _preparedMeshWitnesses.Length ||
            rowCount > _preparedMaterialWitnesses.Length ||
            rowCount > _preparedOptionsWitnesses.Length)
            return EAdvancedPreparedGateOutcome.Capacity;
        if (!scene.TryCaptureAdvancedRowBaseline(out long rowInputVersion))
            return EAdvancedPreparedGateOutcome.RowInputsChanged;

        // Sample every epoch witness before the first row read. Each writer
        // advances its epoch after its write, so a value that is equal at the
        // consume proves that no witnessed write completed after this point.
        // The mesh, material, and options witnesses hold compared values, so
        // they need no sample.
        int ownerThreadId = Environment.CurrentManagedThreadId;
        long lateRenderWorldWriteEpoch = RenderCommandMesh3D.LateRenderWorldWriteEpoch;
        long gpuDrivenBoneStateEpoch = XRMeshRenderer.GpuDrivenBoneStateEpoch;
        bool allowSkinning = RuntimeEngine.Rendering.Settings.AllowSkinning;
        bool computeSkinning = RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader;
        ulong expectedFrameId = unchecked(lastStampedFrameId + 1UL);

        UnchangedRowGateScratch scratch = default;
        // The inputs of the most recent material header check. The check runs
        // on the first row of each run of rows with one material and handle.
        XRMaterial? headerMaterial = null;
        AdvancedGpuHandle headerHandle = AdvancedGpuHandle.Invalid;
        MaterialBindingLayout? headerLayout = null;
        EAdvancedMaterialCoverageMode headerCoverage = default;
        EAdvancedMaterialRenderStateClass headerState = default;
        for (int commandIndex = 0; commandIndex < rowCount; ++commandIndex)
        {
            // Store the command version before the row read. An observed
            // mutation after this point leaves the stored version older.
            ref readonly AdvancedGpuSceneCommandTransition plan = ref _plannedCommands[commandIndex];
            long mutationVersion = plan.Source is RenderCommand command
                ? command.MutationVersion
                : 0L;
            _preparedRowMutationVersions[commandIndex] = mutationVersion;
            // The observers must be bound before the row read and after it. A
            // binding that ends and starts again advances the command version.
            bool observedBeforeRead = plan.Source is RenderCommandMesh3D observedCommand &&
                observedCommand.HasCurrentRendererMutationBinding(plan.Renderer);
            int reusedStaticPlansBefore = scratch.ReusedStaticPlans;
            if (!EvaluateUnchangedRow(scene, commandIndex, expectedFrameId, preparing: true, ref scratch))
                return scratch.RowMismatch == EAdvancedPreparedGateOutcome.None
                    ? EAdvancedPreparedGateOutcome.RegistrationChanged
                    : scratch.RowMismatch;

            if (scratch.RowHeaderChecked)
            {
                headerMaterial = scratch.RowMaterial;
                headerHandle = scratch.RowMaterialHandle;
                headerLayout = scratch.RowHeaderLayout;
                headerCoverage = scratch.RowHeaderCoverage;
                headerState = scratch.RowHeaderState;
            }

            byte rowFlags = 0;
            if (scratch.RowSource is not { } source)
            {
                // A row without a source has no registration and no command
                // state. The publisher epoch and the row inputs cover it.
            }
            else if (!scratch.RowSupported)
            {
                // An unregistered row can use a textured material and selects
                // its layout from live inputs, so the consume evaluates it again.
                rowFlags = PreparedRowReevaluate;
            }
            else
            {
                rowFlags = PreparedRowSupported;
                if (scratch.ReusedStaticPlans != reusedStaticPlansBefore)
                    rowFlags |= PreparedRowReusedStaticPlan;
                if (!observedBeforeRead || RequiresSwapEvaluation(source, in scratch))
                    rowFlags |= PreparedRowReevaluate;
                else
                {
                    EAdvancedPreparedGateOutcome witnessFailure = AddPreparedWitnesses(
                        in scratch,
                        headerMaterial,
                        headerHandle,
                        headerLayout,
                        headerCoverage,
                        headerState);
                    if (witnessFailure != EAdvancedPreparedGateOutcome.None)
                        return witnessFailure;
                }
            }

            _preparedRowFlags[commandIndex] = rowFlags;
        }

        if (!TryFinishUnchangedRowGate(ref scratch))
            return EAdvancedPreparedGateOutcome.BoundsRouteChanged;

        _preparedUnchangedRowGate = new AdvancedPreparedUnchangedRowGate
        {
            IsValid = true,
            OwnerThreadId = ownerThreadId,
            PublisherEpoch = publisherEpoch,
            RowInputVersion = rowInputVersion,
            RowCount = rowCount,
            ExpectedFrameId = expectedFrameId,
            LateRenderWorldWriteEpoch = lateRenderWorldWriteEpoch,
            GpuDrivenBoneStateEpoch = gpuDrivenBoneStateEpoch,
            AllowSkinning = allowSkinning,
            CalculateSkinningInComputeShader = computeSkinning,
            MeshWitnessCount = _preparedMeshWitnessCount,
            MaterialWitnessCount = _preparedMaterialWitnessCount,
            OptionsWitnessCount = _preparedOptionsWitnessCount,
            BoundsDispatcher = scratch.BoundsDispatcher,
        };
        return EAdvancedPreparedGateOutcome.Prepared;
    }

    /// <summary>
    /// Returns true when the command version does not witness the inputs of a
    /// passed supported row. The command observers follow the update-side
    /// renderer and material override, but the row reads the render-side
    /// snapshot. When the two differ, or when the command has an unpublished
    /// change, or when the source has no observers, the consume must evaluate
    /// the row again.
    /// </summary>
    private static bool RequiresSwapEvaluation(
        IRenderCommandMesh source,
        in UnchangedRowGateScratch scratch)
        => source is not RenderCommandMesh3D command ||
            !command.HasCurrentRendererMutationBinding(scratch.RowRenderer) ||
            !ReferenceEquals(command.MaterialOverride, scratch.RowMaterialOverride) ||
            command._dirty;

    /// <summary>
    /// Adds the mesh, the material, and the raster options of one passed
    /// supported row to the witnesses. Every value comes from the row fields of
    /// <paramref name="scratch"/>, so a witness holds the value that the row
    /// compared. Returns <see cref="EAdvancedPreparedGateOutcome.None"/> on
    /// success, and otherwise the reason of the refusal.
    /// </summary>
    private EAdvancedPreparedGateOutcome AddPreparedWitnesses(
        in UnchangedRowGateScratch scratch,
        XRMaterial? headerMaterial,
        AdvancedGpuHandle headerHandle,
        MaterialBindingLayout? headerLayout,
        EAdvancedMaterialCoverageMode headerCoverage,
        EAdvancedMaterialRenderStateClass headerState)
    {
        if (!AddPreparedMeshWitness(in scratch))
            return EAdvancedPreparedGateOutcome.GeometryChanged;

        XRMaterial? material = scratch.RowMaterial;
        AdvancedGpuHandle handle = scratch.RowMaterialHandle;
        int materialCount = _preparedMaterialWitnessCount;
        if (materialCount > 0 &&
            ReferenceEquals(_preparedMaterialWitnesses[materialCount - 1].Material, material) &&
            _preparedMaterialWitnesses[materialCount - 1].Handle == handle)
        {
            // One witness holds one value of each version. A row of the same
            // run that compared another value saw a change during the prepare.
            ref readonly AdvancedPreparedMaterialWitness run =
                ref _preparedMaterialWitnesses[materialCount - 1];
            if (run.BindingLayoutVersion != scratch.RowMaterialBindingLayoutVersion ||
                run.BindingValueVersion != scratch.RowMaterialBindingValueVersion ||
                run.BindingResourceVersion != scratch.RowMaterialBindingResourceVersion ||
                run.ShaderStateRevision != scratch.RowMaterialShaderStateRevision ||
                run.UberStateRevision != scratch.RowMaterialUberStateRevision)
                return EAdvancedPreparedGateOutcome.MaterialChanged;
        }
        else
        {
            // A passed row always follows a header check of its material and
            // handle. That check used the inputs of the first row of the run.
            if (headerLayout is null ||
                !ReferenceEquals(headerMaterial, material) ||
                headerHandle != handle)
                return EAdvancedPreparedGateOutcome.MaterialChanged;
            _preparedMaterialWitnesses[materialCount] = new AdvancedPreparedMaterialWitness
            {
                Material = material,
                Handle = handle,
                BindingLayoutVersion = scratch.RowMaterialBindingLayoutVersion,
                BindingValueVersion = scratch.RowMaterialBindingValueVersion,
                BindingResourceVersion = scratch.RowMaterialBindingResourceVersion,
                ShaderStateRevision = scratch.RowMaterialShaderStateRevision,
                UberStateRevision = scratch.RowMaterialUberStateRevision,
                HeaderLayout = headerLayout,
                HeaderCoverage = headerCoverage,
                HeaderState = headerState,
            };
            _preparedMaterialWitnessCount = materialCount + 1;
        }

        // Null options give the default raster state, which cannot change.
        if (scratch.RowRenderOptions is not { } options)
            return EAdvancedPreparedGateOutcome.None;

        uint cullMode = scratch.RowRenderState.CullMode;
        uint rasterStateFlags = scratch.RowRenderState.Flags & RasterStateFlagMask;
        int optionsCount = _preparedOptionsWitnessCount;
        if (optionsCount > 0 &&
            ReferenceEquals(_preparedOptionsWitnesses[optionsCount - 1].Options, options))
        {
            ref readonly AdvancedPreparedOptionsWitness run =
                ref _preparedOptionsWitnesses[optionsCount - 1];
            return run.CullMode == cullMode && run.RasterStateFlags == rasterStateFlags
                ? EAdvancedPreparedGateOutcome.None
                : EAdvancedPreparedGateOutcome.RenderOptionsChanged;
        }

        _preparedOptionsWitnesses[optionsCount] = new AdvancedPreparedOptionsWitness
        {
            Options = options,
            CullMode = cullMode,
            RasterStateFlags = rasterStateFlags,
        };
        _preparedOptionsWitnessCount = optionsCount + 1;
        return EAdvancedPreparedGateOutcome.None;
    }

    /// <summary>
    /// Adds the geometry values that one passed supported row compared to the
    /// mesh witnesses. Returns false when an earlier row of the same mesh run
    /// compared other values, because the mesh then changed during the prepare.
    /// </summary>
    private bool AddPreparedMeshWitness(in UnchangedRowGateScratch scratch)
    {
        XRMesh? mesh = scratch.RowMesh;
        int meshCount = _preparedMeshWitnessCount;
        if (meshCount > 0 &&
            ReferenceEquals(_preparedMeshWitnesses[meshCount - 1].Mesh, mesh))
        {
            // One witness holds one value of each geometry input.
            ref readonly AdvancedPreparedMeshWitness run =
                ref _preparedMeshWitnesses[meshCount - 1];
            return run.VertexCount == scratch.RowMeshVertexCount &&
                run.IndexCount == scratch.RowMeshIndexCount &&
                run.GeometryRevision == scratch.RowMeshGeometryRevision &&
                run.PrimitiveType == scratch.RowMeshPrimitiveType &&
                run.TriangleCount == scratch.RowMeshTriangleCount;
        }

        _preparedMeshWitnesses[meshCount] = new AdvancedPreparedMeshWitness
        {
            Mesh = mesh,
            VertexCount = scratch.RowMeshVertexCount,
            IndexCount = scratch.RowMeshIndexCount,
            GeometryRevision = scratch.RowMeshGeometryRevision,
            PrimitiveType = scratch.RowMeshPrimitiveType,
            TriangleCount = scratch.RowMeshTriangleCount,
        };
        _preparedMeshWitnessCount = meshCount + 1;
        return true;
    }

    /// <summary>
    /// Consumes the prepared verdict at the gate decision of one publication.
    /// When <paramref name="gateDecided"/> is true, the return value is the
    /// gate decision: a hit decides without the row walk, and with the verify
    /// mode a hit also runs the swap-time gate, whose result the publication
    /// uses. When it is false, the caller runs the swap-time gate. Without the
    /// prepare, the method returns at once and records nothing.
    /// </summary>
    /// <param name="recordTelemetry">
    /// True to record the result in <see cref="AdvancedPreparedGateTelemetry"/>.
    /// Pass true only for the publications that the swap stage timers count,
    /// so that the consume counters and the row-gate counters have one scope.
    /// </param>
    private bool TryConsumePreparedUnchangedRowGate(
        GPUScene scene,
        ulong frameId,
        in AdvancedPreparedUnchangedRowGate prepared,
        bool recordTelemetry,
        out bool gateDecided,
        out int reusedStaticPlans,
        out int materialResolveAttempts)
    {
        gateDecided = false;
        reusedStaticPlans = 0;
        materialResolveAttempts = 0;
        if (!PreparedGateEnabled)
            return false;

        EAdvancedPreparedGateOutcome outcome = EvaluatePreparedUnchangedRowGate(
            scene,
            frameId,
            in prepared,
            out int reevaluatedRows,
            out reusedStaticPlans,
            out materialResolveAttempts);
        if (recordTelemetry)
        {
            AdvancedPreparedGateTelemetry.RecordReevaluatedRows(reevaluatedRows);
            if (outcome == EAdvancedPreparedGateOutcome.Hit)
                AdvancedPreparedGateTelemetry.RecordHit();
            else
                AdvancedPreparedGateTelemetry.RecordMiss(outcome);
        }
        if (outcome != EAdvancedPreparedGateOutcome.Hit)
            return false;

        gateDecided = true;
        if (!PreparedGateVerifyEnabled)
            return true;

        if (TryPassUnchangedRowGate(
                scene,
                frameId,
                out reusedStaticPlans,
                out materialResolveAttempts,
                out EAdvancedPreparedGateOutcome mismatch))
            return true;

        // The swap-time reason names the witness that missed the change.
        if (recordTelemetry)
            AdvancedPreparedGateTelemetry.RecordVerifyDisagreement(mismatch);
        Debug.RenderingWarningEvery(
            "AdvancedGpuScenePublisher.PreparedGateVerify",
            TimeSpan.FromSeconds(5),
            GetVerifyDisagreementMessage(mismatch));
        return false;
    }

    /// <summary>
    /// Compares the prepared verdict with the state at the swap, cheapest
    /// check first. Returns <see cref="EAdvancedPreparedGateOutcome.Hit"/>
    /// when the swap-time gate would pass, and otherwise the reason of the miss.
    /// <paramref name="reevaluatedRows"/> receives the rows that got the
    /// swap-time evaluation: the flagged rows and the rows whose command
    /// changed after the prepare.
    /// </summary>
    private EAdvancedPreparedGateOutcome EvaluatePreparedUnchangedRowGate(
        GPUScene scene,
        ulong frameId,
        in AdvancedPreparedUnchangedRowGate prepared,
        out int reevaluatedRows,
        out int reusedStaticPlans,
        out int materialResolveAttempts)
    {
        reevaluatedRows = 0;
        reusedStaticPlans = 0;
        materialResolveAttempts = 0;
        if (!prepared.IsValid)
            return EAdvancedPreparedGateOutcome.NotPrepared;
        if (prepared.OwnerThreadId != Environment.CurrentManagedThreadId)
            return EAdvancedPreparedGateOutcome.OtherThread;
        // The epoch advances at the end of each publication, so it is still
        // the prepared value inside the publication that consumes the verdict.
        if (prepared.PublisherEpoch != PublisherEpoch)
            return EAdvancedPreparedGateOutcome.PublisherChanged;
        if (!HasValidUnchangedRowBaseline(scene))
            return EAdvancedPreparedGateOutcome.BaselineInvalid;
        if (!scene.IsAdvancedRowInputVersionCurrent(prepared.RowInputVersion))
            return EAdvancedPreparedGateOutcome.RowInputsChanged;

        int rowCount = prepared.RowCount;
        if (rowCount != _plannedCommandCount || scene.TotalCommandCount != (uint)rowCount)
            return EAdvancedPreparedGateOutcome.RowCountChanged;
        if (frameId != prepared.ExpectedFrameId)
            return EAdvancedPreparedGateOutcome.FrameGap;
        if (RenderCommandMesh3D.LateRenderWorldWriteEpoch != prepared.LateRenderWorldWriteEpoch)
            return EAdvancedPreparedGateOutcome.LateWorldWrite;
        if (XRMeshRenderer.GpuDrivenBoneStateEpoch != prepared.GpuDrivenBoneStateEpoch)
            return EAdvancedPreparedGateOutcome.BoneStateChanged;
        if (RuntimeEngine.Rendering.Settings.AllowSkinning != prepared.AllowSkinning ||
            RuntimeEngine.Rendering.Settings.CalculateSkinningInComputeShader != prepared.CalculateSkinningInComputeShader)
            return EAdvancedPreparedGateOutcome.SettingsChanged;
        if (!AreMeshWitnessesCurrent(prepared.MeshWitnessCount))
            return EAdvancedPreparedGateOutcome.GeometryChanged;
        if (!AreMaterialWitnessesCurrent(prepared.MaterialWitnessCount))
            return EAdvancedPreparedGateOutcome.MaterialChanged;
        if (!AreOptionsWitnessesCurrent(prepared.OptionsWitnessCount))
            return EAdvancedPreparedGateOutcome.RenderOptionsChanged;

        UnchangedRowGateScratch scratch = default;
        scratch.BoundsDispatcher = prepared.BoundsDispatcher;
        for (int commandIndex = 0; commandIndex < rowCount; ++commandIndex)
        {
            byte rowFlags = _preparedRowFlags[commandIndex];
            if ((rowFlags & PreparedRowReevaluate) == 0 &&
                (_plannedCommands[commandIndex].Source is not RenderCommand command ||
                    command.MutationVersion == _preparedRowMutationVersions[commandIndex]))
            {
                // The witnesses prove the prepared result of this row.
                if ((rowFlags & PreparedRowSupported) != 0)
                    ++scratch.SupportedRows;
                if ((rowFlags & PreparedRowReusedStaticPlan) != 0)
                    ++scratch.ReusedStaticPlans;
                continue;
            }

            // A flagged row, or a row whose command changed after the prepare,
            // gets the swap-time evaluation. Its result equals the result of
            // the swap-time gate for this row.
            ++reevaluatedRows;
            if (!EvaluateUnchangedRow(scene, commandIndex, frameId, preparing: false, ref scratch))
                return scratch.RowMismatch == EAdvancedPreparedGateOutcome.None
                    ? EAdvancedPreparedGateOutcome.RegistrationChanged
                    : scratch.RowMismatch;
        }

        if (!TryFinishUnchangedRowGate(ref scratch))
            return EAdvancedPreparedGateOutcome.BoundsRouteChanged;

        reusedStaticPlans = scratch.ReusedStaticPlans;
        materialResolveAttempts = scratch.MaterialResolveAttempts;
        return EAdvancedPreparedGateOutcome.Hit;
    }

    /// <summary>
    /// Returns true when each witnessed mesh still has the compared geometry
    /// values. The primitive type is read before the index count, because the
    /// index count of some primitive types throws.
    /// </summary>
    private bool AreMeshWitnessesCurrent(int count)
    {
        if (count > _preparedMeshWitnessCount)
            return false;

        for (int index = 0; index < count; ++index)
        {
            ref readonly AdvancedPreparedMeshWitness witness =
                ref _preparedMeshWitnesses[index];
            if (witness.Mesh is not { } mesh ||
                Math.Max(0, mesh.VertexCount) != witness.VertexCount ||
                mesh.Type != witness.PrimitiveType ||
                Math.Max(0, mesh.IndexCount) != witness.IndexCount ||
                mesh.GeometryRevision != witness.GeometryRevision ||
                mesh.Triangles is not { } triangles ||
                triangles.Count != witness.TriangleCount)
                return false;
        }
        return true;
    }

    /// <summary>
    /// Returns true when each witnessed material still has the compared
    /// versions and source bindings, and still matches its committed header.
    /// </summary>
    private bool AreMaterialWitnessesCurrent(int count)
    {
        if (count > _preparedMaterialWitnessCount)
            return false;

        for (int index = 0; index < count; ++index)
        {
            ref readonly AdvancedPreparedMaterialWitness witness =
                ref _preparedMaterialWitnesses[index];
            XRMaterial? material = witness.Material;
            if ((material?.BindingLayoutVersion ?? 0u) != witness.BindingLayoutVersion ||
                (material?.BindingValueVersion ?? 0u) != witness.BindingValueVersion ||
                (material?.BindingResourceVersion ?? 0u) != witness.BindingResourceVersion ||
                (material?.ShaderStateRevision ?? 0L) != witness.ShaderStateRevision ||
                (material?.UberStateRevision ?? 0L) != witness.UberStateRevision ||
                !CanReuseMaterialSources(material) ||
                witness.HeaderLayout is not { } layout ||
                (NativeUberMaterialSchema.IsLayout(layout) &&
                 !_materialPublisher.HasCurrentStrictUberValidation(witness.Handle)) ||
                !Database.Materials.Materials.TryGet(witness.Handle,
                    out AdvancedMaterialRecord current) ||
                !Database.Materials.TryGetTextureBindings(current,
                    out ReadOnlySpan<AdvancedMaterialTextureBinding> bindings) ||
                bindings.Length != layout.Textures.Count ||
                !Database.Materials.TryGetEngineSurface(witness.Handle,
                    out AdvancedEngineSurfaceRecord engineSurface) ||
                !Database.Materials.TryGetUberBaseSurface(witness.Handle,
                    out AdvancedUberBaseSurfaceRecord uberSurface) ||
                !CommittedMaterialBindingsMatch(material, layout, bindings,
                    in engineSurface, in uberSurface) ||
                !_materialPublisher.HeaderMatches(
                    witness.Handle,
                    material,
                    layout,
                    witness.HeaderCoverage,
                    witness.HeaderState,
                    bindings))
                return false;
        }
        return true;
    }

    /// <summary>Returns true when each witnessed options object still gives the compared raster state.</summary>
    private bool AreOptionsWitnessesCurrent(int count)
    {
        if (count > _preparedOptionsWitnessCount)
            return false;

        for (int index = 0; index < count; ++index)
        {
            ref readonly AdvancedPreparedOptionsWitness witness =
                ref _preparedOptionsWitnesses[index];
            if (witness.Options is not { } options ||
                (uint)options.CullMode != witness.CullMode ||
                (uint)WebGpuAdvancedRasterStateContract.Capture(options) != witness.RasterStateFlags)
                return false;
        }
        return true;
    }

    /// <summary>Returns the cached verify warning text of <paramref name="reason"/>.</summary>
    private static string GetVerifyDisagreementMessage(EAdvancedPreparedGateOutcome reason)
        => s_verifyDisagreementMessages[
            (uint)reason < (uint)s_verifyDisagreementMessages.Length ? (int)reason : 0];

    /// <summary>Creates one verify warning text for each gate result.</summary>
    private static string[] CreateVerifyDisagreementMessages()
    {
        string[] messages = new string[(int)EAdvancedPreparedGateOutcome.Count];
        for (int index = 0; index < messages.Length; ++index)
            messages[index] =
                "[CanonicalPublication] The prepared unchanged-row verdict passed, but the swap-time gate failed. Witness: " +
                ((EAdvancedPreparedGateOutcome)index).ToString() + ".";
        return messages;
    }
}

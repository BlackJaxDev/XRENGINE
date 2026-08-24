using System.Numerics;
using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Dual-publishes legacy <see cref="GPUScene"/> residents into the canonical
/// shared database at the scene swap boundary. Registrations retain handles
/// allocated by the canonical tables; this type never manufactures logical IDs.
/// </summary>
public sealed class AdvancedGpuScenePublisher
{
    private const uint InitialCapacity = 64u;

    private AdvancedResidentRegistration[] _registrations =
        new AdvancedResidentRegistration[InitialCapacity];
    private AdvancedGpuHandle[] _commandDrawHandles =
        new AdvancedGpuHandle[InitialCapacity];
    private LegacyCanonicalDrawMapping[] _legacyMappings =
        new LegacyCanonicalDrawMapping[InitialCapacity];
    private int[] _registrationLookupIndices = new int[InitialCapacity * 2u];
    private uint[] _registrationLookupStamps = new uint[InitialCapacity * 2u];
    private uint[] _preflightSeenStamps = new uint[InitialCapacity];
    private readonly AdvancedGpuDirtyOwnerRange[] _dirtyOwnerRanges =
        new AdvancedGpuDirtyOwnerRange[8];
    private int _registrationCount;
    private int _legacyMappingCount;
    private ulong _sequence;
    private ulong _topologyGeneration;
    private ulong _contentGeneration;
    private ulong _lookupGeneration;
    private int _topologyDeltaCount;
    private int _contentDeltaCount;
    private bool _publicationRejected;
    private AdvancedGpuScenePublicationReference _currentPublication;
    private int _dirtyOwnerRangeCount;
    private uint _registrationLookupGeneration;
    private uint _preflightSeenGeneration;

    public AdvancedGpuScenePublisher()
    {
        Database = new AdvancedSharedGpuSceneDatabase(
            CreateCapacityProfile(InitialCapacity));
    }

    public AdvancedSharedGpuSceneDatabase Database { get; }

    public ulong Sequence => _sequence;

    public ulong TopologyGeneration => _topologyGeneration;

    public ulong ContentGeneration => _contentGeneration;

    public ulong LookupGeneration => _lookupGeneration;

    public int TopologyDeltaCount => _topologyDeltaCount;

    public int ContentDeltaCount => _contentDeltaCount;

    public bool PublicationRejected => _publicationRejected;

    public AdvancedGpuScenePublicationReference CurrentPublication
        => _currentPublication;

    public ReadOnlySpan<LegacyCanonicalDrawMapping> LegacyMappings
        => _legacyMappings.AsSpan(0, _legacyMappingCount);

    public ReadOnlySpan<AdvancedGpuDirtyOwnerRange> DirtyOwnerRanges
        => _dirtyOwnerRanges.AsSpan(0, _dirtyOwnerRangeCount);

    /// <summary>
    /// Publishes one immutable render-side scene snapshot. Callers must invoke
    /// this only from <see cref="GPUScene.SwapCommandBuffers"/> while holding the
    /// scene mutation lock.
    /// </summary>
    public void Publish(GPUScene scene, ulong frameId)
    {
        AdvanceSequence();
        _topologyDeltaCount = 0;
        _contentDeltaCount = 0;
        _legacyMappingCount = 0;
        _publicationRejected = false;

        if (!EnsureBoundaryCapacity(scene.TotalCommandCount))
        {
            _publicationRejected = true;
            return;
        }
        RebuildRegistrationLookup();
        if (!CanPublishWholeScene(scene))
        {
            _publicationRejected = true;
            return;
        }
        if (!Database.BeginPublication())
        {
            _publicationRejected = true;
            return;
        }
        _sequence = Database.ActivePublicationSequence;

        for (uint commandIndex = 0u; commandIndex < scene.TotalCommandCount; ++commandIndex)
        {
            if (!scene.TryGetAdvancedPreparationCommand(commandIndex, out DrawMetadata command) ||
                !scene.TryGetSourceCommand(commandIndex, out IRenderCommandMesh? source, out int primitiveIndex) ||
                source is null)
            {
                _commandDrawHandles[commandIndex] = AdvancedGpuHandle.Invalid;
                continue;
            }

            int registrationIndex = FindRegistration(source, primitiveIndex);
            if (registrationIndex < 0)
            {
                registrationIndex = TryAddRegistration(
                    scene,
                    source,
                    primitiveIndex,
                    in command);
                if (registrationIndex < 0)
                {
                    throw new InvalidOperationException(
                        "Canonical resident tables exhausted their preflighted frame-boundary capacity.");
                }
            }
            else
            {
                UpdateRegistration(
                    scene,
                    registrationIndex,
                    source,
                    primitiveIndex,
                    in command);
            }

            ref AdvancedResidentRegistration registration =
                ref _registrations[registrationIndex];
            registration.LastSeenSequence = _sequence;
            registration.LegacyCommandIndex = commandIndex;
            _commandDrawHandles[commandIndex] = registration.Draw;
            AppendLegacyMapping(commandIndex, primitiveIndex, in command, in registration);
        }

        TombstoneMissingRegistrations();
        bool lookupDirty = HasDirtyLogicalLookups();
        if (Database.PublishHandleLookups())
        {
            if (lookupDirty)
                AdvanceNonZero(ref _lookupGeneration);
        }
        else
            _publicationRejected = true;

        if (!Database.SealPublication(
            frameId,
            _topologyGeneration,
            _contentGeneration,
            _lookupGeneration,
            out _currentPublication))
        {
            _publicationRejected = true;
            throw new InvalidOperationException("Canonical scene publication failed after a successful whole-frame preflight.");
        }

        PublishSourceDrawIdentities();
        CaptureAndClearDirtyOwnerRanges();
    }

    private AdvancedGpuHandle[] _identityHandleScratch = new AdvancedGpuHandle[8];

    /// <summary>
    /// Assigns renderer-facing identities only after the database sealed the
    /// publication. Primitive zero is the primary identity while the immutable
    /// snapshot retains every exact primitive handle for Vulkan submission.
    /// </summary>
    private void PublishSourceDrawIdentities()
    {
        for (int registrationIndex = 0; registrationIndex < _registrationCount; ++registrationIndex)
        {
            ref readonly AdvancedResidentRegistration primary = ref _registrations[registrationIndex];
            if (!primary.Active || primary.PrimitiveIndex != 0 ||
                primary.Source is not RenderCommandMesh3D command)
            {
                continue;
            }

            AdvancedMeshRenderSnapshot renderSnapshot =
                command.CaptureAdvancedPreparationSnapshot();
            int primitiveCount = Math.Max(
                1,
                renderSnapshot.Renderer?.Submeshes.Count ?? 0);

            if (_identityHandleScratch.Length < primitiveCount)
                Array.Resize(
                    ref _identityHandleScratch,
                    checked((int)NextPowerOfTwo((uint)primitiveCount)));
            Array.Clear(_identityHandleScratch, 0, primitiveCount);
            for (int primitiveIndex = 0; primitiveIndex < primitiveCount; ++primitiveIndex)
            {
                int candidateIndex = FindRegistration(primary.Source, primitiveIndex);
                if (candidateIndex >= 0)
                    _identityHandleScratch[primitiveIndex] =
                        _registrations[candidateIndex].Draw;
            }

            command.PublishCanonicalDrawIdentities(
                Database,
                _currentPublication,
                _identityHandleScratch.AsSpan(0, primitiveCount));
        }
    }

    private void CaptureAndClearDirtyOwnerRanges()
    {
        AdvancedGpuSceneDatabase scene = Database.Scene;
        _dirtyOwnerRangeCount = 0;
        Capture(EAdvancedGpuRecordOwner.Draw, scene.Draws);
        Capture(EAdvancedGpuRecordOwner.Instance, scene.Instances);
        Capture(EAdvancedGpuRecordOwner.Transform, scene.Transforms);
        Capture(EAdvancedGpuRecordOwner.Deformation, scene.Deformations);
        Capture(EAdvancedGpuRecordOwner.RenderState, scene.RenderStates);
        Capture(EAdvancedGpuRecordOwner.Material, Database.Materials.Materials);
        Capture(EAdvancedGpuRecordOwner.Geometry, scene.Geometry.Records);
        Capture(EAdvancedGpuRecordOwner.EditorIdentity, scene.EditorIdentities);
    }

    private bool HasDirtyLogicalLookups()
    {
        AdvancedGpuSceneDatabase scene = Database.Scene;
        return !scene.Draws.LogicalLookupDirtyRange.IsEmpty ||
            !scene.Instances.LogicalLookupDirtyRange.IsEmpty ||
            !scene.Transforms.LogicalLookupDirtyRange.IsEmpty ||
            !scene.Deformations.LogicalLookupDirtyRange.IsEmpty ||
            !scene.RenderStates.LogicalLookupDirtyRange.IsEmpty ||
            !scene.EditorIdentities.LogicalLookupDirtyRange.IsEmpty ||
            !scene.Geometry.Records.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Materials.Materials.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Materials.Kernels.LogicalLookupDirtyRange.IsEmpty ||
            !Database.Materials.Layouts.LogicalLookupDirtyRange.IsEmpty;
    }

    private void Capture<T>(
        EAdvancedGpuRecordOwner owner,
        AdvancedGpuRecordTable<T> table)
        where T : unmanaged
    {
        AdvancedGpuDirtyRange range = table.DirtyRange;
        if (!range.IsEmpty)
        {
            _dirtyOwnerRanges[_dirtyOwnerRangeCount++] =
                new AdvancedGpuDirtyOwnerRange(owner, range, _contentGeneration);
        }
        table.ClearDirtyRange();
        table.ClearLogicalLookupDirtyRange();
    }

    public bool TryGetCanonicalHandles(
        uint commandIndex,
        out AdvancedGpuHandle draw,
        out AdvancedGpuHandle geometry,
        out AdvancedGpuHandle material)
        => TryGetCanonicalHandles(
            commandIndex,
            out draw,
            out geometry,
            out material,
            out _);

    public bool TryGetCanonicalHandles(
        uint commandIndex,
        out AdvancedGpuHandle draw,
        out AdvancedGpuHandle geometry,
        out AdvancedGpuHandle material,
        out AdvancedGpuHandle deformation)
    {
        draw = AdvancedGpuHandle.Invalid;
        geometry = AdvancedGpuHandle.Invalid;
        material = AdvancedGpuHandle.Invalid;
        deformation = AdvancedGpuHandle.Invalid;
        if (commandIndex >= (uint)_commandDrawHandles.Length)
            return false;

        draw = _commandDrawHandles[commandIndex];
        if (!draw.IsValid ||
            !Database.Scene.Draws.TryGet(draw, out AdvancedDrawRecord record))
            return false;

        geometry = record.Geometry;
        material = record.Material;
        deformation = record.Deformation;
        return geometry.IsValid && material.IsValid && deformation.IsValid;
    }

    public bool TryGetCanonicalDraw(
        IRenderCommandMesh source,
        out AdvancedGpuHandle draw)
        => TryGetCanonicalDraw(source, 0, out draw);

    public bool TryGetCanonicalDraw(
        IRenderCommandMesh source,
        int primitiveIndex,
        out AdvancedGpuHandle draw)
    {
        int index = FindRegistration(source, primitiveIndex);
        if (index >= 0)
        {
            draw = _registrations[index].Draw;
            return draw.IsValid;
        }

        draw = AdvancedGpuHandle.Invalid;
        return false;
    }

    public bool WasDrawAddedThisPublication(AdvancedGpuHandle draw)
    {
        ReadOnlySpan<AdvancedGpuRecordPublicationDelta> deltas =
            Database.Scene.Draws.PublishedDeltas;
        for (int index = deltas.Length - 1; index >= 0; --index)
        {
            AdvancedGpuRecordPublicationDelta delta = deltas[index];
            if (delta.PublicationGeneration != _sequence || delta.Handle != draw)
                continue;
            return delta.Change == EAdvancedGpuRecordPublicationChange.Added;
        }
        return false;
    }

    private int TryAddRegistration(
        GPUScene scene,
        IRenderCommandMesh source,
        int primitiveIndex,
        in DrawMetadata command)
    {
        CaptureSourceState(scene, source, primitiveIndex, in command,
            out Matrix4x4 world,
            out Matrix4x4 previousWorld,
            out BoundsGpu bounds,
            out XRMesh? mesh,
            out XRMaterial? sourceMaterial);

        AdvancedGpuSceneDatabase tables = Database.Scene;
        AdvancedGpuHandle currentTransform = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle previousTransform = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle instance = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle geometry = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle deformation = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle material = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle renderState = AdvancedGpuHandle.Invalid;
        AdvancedGpuHandle editorIdentity = AdvancedGpuHandle.Invalid;
        if (!CanAddRegistration(tables))
            return -1;

        if (!tables.Transforms.TryAdd(CreateTransform(world), out currentTransform) ||
            !tables.Transforms.TryAdd(CreateTransform(previousWorld), out previousTransform) ||
            !tables.Instances.TryAdd(CreateInstance(world, previousWorld, in bounds, in command), out instance) ||
            !tables.Geometry.Records.TryAdd(CreateGeometry(scene, mesh, in bounds, in command), out geometry) ||
            !tables.Deformations.TryAdd(CreateDeformation(geometry, mesh), out deformation) ||
            !Database.Materials.Materials.TryAdd(CreateMaterial(in command, sourceMaterial), out material) ||
            !tables.RenderStates.TryAdd(CreateRenderState(mesh, in command), out renderState) ||
            !tables.EditorIdentities.TryAdd(CreateEditorIdentity(in command), out editorIdentity))
        {
            RollBackPartialRegistration(
                currentTransform,
                previousTransform,
                instance,
                geometry,
                deformation,
                material,
                renderState,
                editorIdentity);
            return -1;
        }

        // The table owns canonical row identity. The source material ID is only
        // input metadata and must never become a second logical-handle allocator.
        if (!Database.Materials.Materials.TryReplace(
            material,
            CreateMaterial(in command, sourceMaterial, material)))
        {
            RollBackPartialRegistration(
                currentTransform,
                previousTransform,
                instance,
                geometry,
                deformation,
                material,
                renderState,
                editorIdentity);
            return -1;
        }

        AdvancedDrawRecord drawRecord = new()
        {
            Instance = instance,
            Geometry = geometry,
            Material = material,
            Deformation = deformation,
            RenderState = renderState,
            EditorIdentity = editorIdentity,
            CurrentTransform = currentTransform,
            PreviousTransform = previousTransform,
            PrimitiveSection = checked((uint)Math.Max(0, primitiveIndex)),
            Flags = command.Flags,
        };
        if (!tables.Draws.TryAdd(drawRecord, out AdvancedGpuHandle draw))
        {
            RollBackPartialRegistration(
                currentTransform,
                previousTransform,
                instance,
                geometry,
                deformation,
                material,
                renderState,
                editorIdentity);
            return -1;
        }

        int index = FindReusableRegistrationIndex();
        if (index < 0)
            index = _registrationCount++;
        _registrations[index] = new AdvancedResidentRegistration
        {
            Source = source,
            PrimitiveIndex = primitiveIndex,
            Draw = draw,
            Instance = instance,
            Geometry = geometry,
            Deformation = deformation,
            Material = material,
            RenderState = renderState,
            EditorIdentity = editorIdentity,
            CurrentTransform = currentTransform,
            PreviousTransform = previousTransform,
            World = world,
            StructuralSignature = ComputeStructuralSignature(in command, mesh, sourceMaterial, primitiveIndex),
            ContentSignature = ComputeContentSignature(in command, in bounds, in world, in previousWorld, sourceMaterial),
            Active = true,
        };
        if (!TryInsertRegistrationLookup(index))
        {
            Database.Scene.Draws.TryRemoveImmediatelyBeforePublication(draw);
            RollBackPartialRegistration(
                currentTransform,
                previousTransform,
                instance,
                geometry,
                deformation,
                material,
                renderState,
                editorIdentity);
            _registrations[index] = default;
            if (index == _registrationCount - 1)
                --_registrationCount;
            return -1;
        }
        ++_topologyDeltaCount;
        AdvanceNonZero(ref _topologyGeneration);
        AdvanceNonZero(ref _contentGeneration);
        return index;
    }

    private void UpdateRegistration(
        GPUScene scene,
        int registrationIndex,
        IRenderCommandMesh source,
        int primitiveIndex,
        in DrawMetadata command)
    {
        ref AdvancedResidentRegistration registration =
            ref _registrations[registrationIndex];
        if (!registration.Active)
            return;

        CaptureSourceState(scene, source, primitiveIndex, in command,
            out Matrix4x4 world,
            out Matrix4x4 previousWorld,
            out BoundsGpu bounds,
            out XRMesh? mesh,
            out XRMaterial? sourceMaterial);
        ulong structural = ComputeStructuralSignature(in command, mesh, sourceMaterial, primitiveIndex);
        ulong content = ComputeContentSignature(in command, in bounds, in world, in previousWorld, sourceMaterial);
        if (structural != registration.StructuralSignature)
        {
            if (!CanUpdateRegistrationStructure(registration) ||
                !Database.Scene.Draws.TryGet(registration.Draw, out AdvancedDrawRecord draw))
            {
                throw new InvalidOperationException("Canonical structural update preflight failed.");
            }

            if (!Database.Scene.Geometry.Records.TryReplace(
                registration.Geometry,
                CreateGeometry(scene, mesh, in bounds, in command)) ||
                !Database.Scene.Deformations.TryReplace(
                    registration.Deformation,
                    CreateDeformation(registration.Geometry, mesh)) ||
                !Database.Materials.Materials.TryReplace(
                registration.Material,
                CreateMaterial(in command, sourceMaterial, registration.Material)) ||
                !Database.Scene.RenderStates.TryReplace(
                registration.RenderState,
                CreateRenderState(mesh, in command)))
            {
                throw new InvalidOperationException("Canonical structural update failed after successful preflight.");
            }

            draw.PrimitiveSection = checked((uint)Math.Max(0, primitiveIndex));
            draw.Flags = command.Flags;
            if (!Database.Scene.Draws.TryReplace(registration.Draw, draw))
                throw new InvalidOperationException("Canonical draw update failed after successful preflight.");
            registration.StructuralSignature = structural;
            ++_topologyDeltaCount;
            AdvanceNonZero(ref _topologyGeneration);
        }

        if (content == registration.ContentSignature)
            return;

        if (!CanUpdateRegistrationContent(registration) ||
            !Database.Scene.Transforms.TryReplace(
            registration.PreviousTransform,
            CreateTransform(previousWorld)) ||
            !Database.Scene.Transforms.TryReplace(
            registration.CurrentTransform,
            CreateTransform(world)) ||
            !Database.Scene.Instances.TryReplace(
            registration.Instance,
            CreateInstance(world, previousWorld, in bounds, in command)) ||
            !Database.Materials.Materials.TryReplace(
            registration.Material,
            CreateMaterial(in command, sourceMaterial, registration.Material)) ||
            !Database.Scene.EditorIdentities.TryReplace(
            registration.EditorIdentity,
            CreateEditorIdentity(in command)))
        {
            throw new InvalidOperationException("Canonical content update failed after successful preflight.");
        }
        registration.World = world;
        registration.ContentSignature = content;
        ++_contentDeltaCount;
        AdvanceNonZero(ref _contentGeneration);
    }

    private void TombstoneMissingRegistrations()
    {
        for (int index = 0; index < _registrationCount; ++index)
        {
            ref AdvancedResidentRegistration registration = ref _registrations[index];
            if (!registration.Active || registration.LastSeenSequence == _sequence)
                continue;

            if (!CanTombstoneRegistration(registration))
                throw new InvalidOperationException("Canonical tombstone transaction exceeded its bounded journal capacity.");

            bool tombstoned =
                Database.Scene.Draws.TryTombstone(registration.Draw, _sequence) &&
                Database.Scene.Instances.TryTombstone(registration.Instance, _sequence) &&
                Database.Scene.Geometry.Records.TryTombstone(registration.Geometry, _sequence) &&
                Database.Scene.Deformations.TryTombstone(registration.Deformation, _sequence) &&
                Database.Materials.Materials.TryTombstone(registration.Material, _sequence) &&
                Database.Scene.RenderStates.TryTombstone(registration.RenderState, _sequence) &&
                Database.Scene.EditorIdentities.TryTombstone(registration.EditorIdentity, _sequence) &&
                Database.Scene.Transforms.TryTombstone(registration.CurrentTransform, _sequence) &&
                Database.Scene.Transforms.TryTombstone(registration.PreviousTransform, _sequence);
            if (!tombstoned)
                throw new InvalidOperationException("Canonical tombstone transaction failed after successful preflight.");

            registration.Active = false;
            registration.TombstoneSequence = _sequence;
            ++_topologyDeltaCount;
            AdvanceNonZero(ref _topologyGeneration);
            AdvanceNonZero(ref _contentGeneration);
        }
    }

    private void AppendLegacyMapping(
        uint commandIndex,
        int primitiveIndex,
        in DrawMetadata command,
        in AdvancedResidentRegistration registration)
    {
        _legacyMappings[_legacyMappingCount++] = new LegacyCanonicalDrawMapping(
            commandIndex,
            command.MeshID,
            command.MaterialID,
            command.RenderPass,
            primitiveIndex,
            registration.Draw,
            registration.Geometry,
            registration.Material,
            Mix(
                registration.StructuralSignature,
                registration.ContentSignature));
    }

    private bool EnsureBoundaryCapacity(uint commandCount)
    {
        uint required = Math.Max(
            InitialCapacity,
            NextPowerOfTwo(Math.Max(
                commandCount + 1u,
                checked((uint)_registrationCount + 1u))));
        if (required > (uint)_commandDrawHandles.Length)
        {
            Array.Resize(ref _commandDrawHandles, checked((int)required));
            Array.Resize(ref _legacyMappings, checked((int)required));
        }
        if (required > (uint)_registrations.Length)
        {
            Array.Resize(ref _registrations, checked((int)required));
            Array.Resize(ref _preflightSeenStamps, checked((int)required));
        }

        uint lookupCapacity = NextPowerOfTwo(checked(required * 2u));
        if (lookupCapacity > (uint)_registrationLookupIndices.Length)
        {
            Array.Resize(ref _registrationLookupIndices, checked((int)lookupCapacity));
            Array.Resize(ref _registrationLookupStamps, checked((int)lookupCapacity));
            _registrationLookupGeneration = 0u;
        }

        return required <= Database.Scene.Draws.Capacity ||
            Database.TryGrowAtFrameBoundary(CreateCapacityProfile(required));
    }

    private int FindRegistration(IRenderCommandMesh source, int primitiveIndex)
    {
        uint mask = checked((uint)_registrationLookupIndices.Length - 1u);
        uint start = HashRegistration(source, primitiveIndex) & mask;
        for (uint probe = 0u; probe < (uint)_registrationLookupIndices.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_registrationLookupStamps[slot] != _registrationLookupGeneration)
                return -1;

            int index = _registrationLookupIndices[slot];
            ref readonly AdvancedResidentRegistration registration = ref _registrations[index];
            if (registration.Active &&
                ReferenceEquals(registration.Source, source) &&
                registration.PrimitiveIndex == primitiveIndex)
                return index;
        }
        return -1;
    }

    private void RebuildRegistrationLookup()
    {
        ++_registrationLookupGeneration;
        if (_registrationLookupGeneration == 0u)
        {
            Array.Clear(_registrationLookupStamps);
            _registrationLookupGeneration = 1u;
        }

        for (int index = 0; index < _registrationCount; ++index)
            if (_registrations[index].Active && !TryInsertRegistrationLookup(index))
                throw new InvalidOperationException("The fixed resident-registration lookup table is undersized.");
    }

    private bool TryInsertRegistrationLookup(int registrationIndex)
    {
        ref readonly AdvancedResidentRegistration registration =
            ref _registrations[registrationIndex];
        if (!registration.Active || registration.Source is null)
            return false;

        uint mask = checked((uint)_registrationLookupIndices.Length - 1u);
        uint start = HashRegistration(registration.Source, registration.PrimitiveIndex) & mask;
        for (uint probe = 0u; probe < (uint)_registrationLookupIndices.Length; ++probe)
        {
            int slot = checked((int)((start + probe) & mask));
            if (_registrationLookupStamps[slot] == _registrationLookupGeneration)
                continue;

            _registrationLookupIndices[slot] = registrationIndex;
            _registrationLookupStamps[slot] = _registrationLookupGeneration;
            return true;
        }

        return false;
    }

    private static uint HashRegistration(IRenderCommandMesh source, int primitiveIndex)
    {
        uint hash = unchecked((uint)RuntimeHelpers.GetHashCode(source));
        hash ^= unchecked((uint)primitiveIndex) * 0x9E3779B9u;
        hash ^= hash >> 16;
        hash *= 0x7FEB352Du;
        hash ^= hash >> 15;
        return hash;
    }

    private int FindReusableRegistrationIndex()
    {
        for (int index = 0; index < _registrationCount; ++index)
            if (!_registrations[index].Active)
                return index;
        return -1;
    }

    private static void CaptureSourceState(
        GPUScene scene,
        IRenderCommandMesh source,
        int primitiveIndex,
        in DrawMetadata command,
        out Matrix4x4 world,
        out Matrix4x4 previousWorld,
        out BoundsGpu bounds,
        out XRMesh? mesh,
        out XRMaterial? material)
    {
        AdvancedMeshRenderSnapshot snapshot = source is RenderCommandMesh3D mesh3D
            ? mesh3D.CaptureAdvancedPreparationSnapshot()
            : new AdvancedMeshRenderSnapshot(
                source.Mesh,
                source.WorldMatrix,
                source.WorldMatrix,
                source.Instances,
                source.WorldMatrixIsModelMatrix,
                source.ForceCpuRendering,
                source.MaterialOverride,
                source.RenderOptionsOverride);
        world = snapshot.WorldMatrixIsModelMatrix
            ? snapshot.CurrentWorld
            : Matrix4x4.Identity;
        previousWorld = snapshot.WorldMatrixIsModelMatrix
            ? snapshot.PreviousWorld
            : Matrix4x4.Identity;
        bounds = command.BoundsID < scene.CullBoundsBuffer.ElementCount
            ? scene.CullBoundsBuffer.GetDataRawAtIndex<BoundsGpu>(command.BoundsID)
            : default;
        if (snapshot.Renderer is not { } renderer ||
            !renderer.TryGetMesh(primitiveIndex, out mesh, out material))
        {
            mesh = null;
            material = null;
        }
        material = snapshot.MaterialOverride ?? material;
    }

    private bool CanAddRegistration(AdvancedGpuSceneDatabase tables)
        => tables.Transforms.CanApply(2, 0, 0) &&
           tables.Instances.CanApply(1, 0, 0) &&
           tables.Geometry.Records.CanApply(1, 0, 0) &&
           tables.Deformations.CanApply(1, 0, 0) &&
           Database.Materials.Materials.CanApply(1, 1, 0) &&
           tables.RenderStates.CanApply(1, 0, 0) &&
           tables.EditorIdentities.CanApply(1, 0, 0) &&
           tables.Draws.CanApply(1, 0, 0);

    private bool CanPublishWholeScene(GPUScene scene)
    {
        ++_preflightSeenGeneration;
        if (_preflightSeenGeneration == 0u)
        {
            Array.Clear(_preflightSeenStamps);
            _preflightSeenGeneration = 1u;
        }

        int additions = 0;
        int structuralUpdates = 0;
        int contentUpdates = 0;
        for (uint commandIndex = 0u; commandIndex < scene.TotalCommandCount; ++commandIndex)
        {
            if (!scene.TryGetAdvancedPreparationCommand(commandIndex, out DrawMetadata command) ||
                !scene.TryGetSourceCommand(commandIndex, out IRenderCommandMesh? source, out int primitiveIndex) ||
                source is null)
            {
                continue;
            }

            int registrationIndex = FindRegistration(source, primitiveIndex);
            if (registrationIndex < 0)
            {
                ++additions;
                continue;
            }

            _preflightSeenStamps[registrationIndex] = _preflightSeenGeneration;
            ref readonly AdvancedResidentRegistration registration = ref _registrations[registrationIndex];
            CaptureSourceState(
                scene,
                source,
                primitiveIndex,
                in command,
                out Matrix4x4 world,
                out Matrix4x4 previousWorld,
                out BoundsGpu bounds,
                out XRMesh? mesh,
                out XRMaterial? material);
            if (ComputeStructuralSignature(in command, mesh, material, primitiveIndex) !=
                registration.StructuralSignature)
            {
                ++structuralUpdates;
            }
            if (ComputeContentSignature(in command, in bounds, in world, in previousWorld, material) !=
                registration.ContentSignature)
            {
                ++contentUpdates;
            }
        }

        int tombstones = 0;
        for (int index = 0; index < _registrationCount; ++index)
            if (_registrations[index].Active &&
                _preflightSeenStamps[index] != _preflightSeenGeneration)
            {
                ++tombstones;
            }

        AdvancedGpuSceneDatabase tables = Database.Scene;
        return tables.Draws.CanApply(additions, structuralUpdates, tombstones) &&
            tables.Instances.CanApply(additions, contentUpdates, tombstones) &&
            tables.Transforms.CanApply(additions * 2, contentUpdates * 2, tombstones * 2) &&
            tables.Geometry.Records.CanApply(additions, structuralUpdates, tombstones) &&
            tables.Deformations.CanApply(additions, structuralUpdates, tombstones) &&
            Database.Materials.Materials.CanApply(
                additions,
                checked(additions + structuralUpdates + contentUpdates),
                tombstones) &&
            tables.RenderStates.CanApply(additions, structuralUpdates, tombstones) &&
            tables.EditorIdentities.CanApply(additions, contentUpdates, tombstones);
    }

    private bool CanUpdateRegistrationStructure(
        in AdvancedResidentRegistration registration)
        => Database.Scene.Geometry.Records.IsCurrent(registration.Geometry) &&
           Database.Scene.Geometry.Records.CanApply(0, 1, 0) &&
           Database.Scene.Deformations.IsCurrent(registration.Deformation) &&
           Database.Scene.Deformations.CanApply(0, 1, 0) &&
           Database.Materials.Materials.IsCurrent(registration.Material) &&
           Database.Materials.Materials.CanApply(0, 1, 0) &&
           Database.Scene.RenderStates.IsCurrent(registration.RenderState) &&
           Database.Scene.RenderStates.CanApply(0, 1, 0) &&
           Database.Scene.Draws.IsCurrent(registration.Draw) &&
           Database.Scene.Draws.CanApply(0, 1, 0);

    private bool CanUpdateRegistrationContent(
        in AdvancedResidentRegistration registration)
        => Database.Scene.Transforms.IsCurrent(registration.CurrentTransform) &&
           Database.Scene.Transforms.IsCurrent(registration.PreviousTransform) &&
           Database.Scene.Transforms.CanApply(0, 2, 0) &&
           Database.Scene.Instances.IsCurrent(registration.Instance) &&
           Database.Scene.Instances.CanApply(0, 1, 0) &&
           Database.Materials.Materials.IsCurrent(registration.Material) &&
           Database.Materials.Materials.CanApply(0, 1, 0) &&
           Database.Scene.EditorIdentities.IsCurrent(registration.EditorIdentity) &&
           Database.Scene.EditorIdentities.CanApply(0, 1, 0);

    private bool CanTombstoneRegistration(
        in AdvancedResidentRegistration registration)
        => Database.Scene.Draws.IsCurrent(registration.Draw) &&
           Database.Scene.Draws.CanApply(0, 0, 1) &&
           Database.Scene.Instances.IsCurrent(registration.Instance) &&
           Database.Scene.Instances.CanApply(0, 0, 1) &&
           Database.Scene.Geometry.Records.IsCurrent(registration.Geometry) &&
           Database.Scene.Geometry.Records.CanApply(0, 0, 1) &&
           Database.Scene.Deformations.IsCurrent(registration.Deformation) &&
           Database.Scene.Deformations.CanApply(0, 0, 1) &&
           Database.Materials.Materials.IsCurrent(registration.Material) &&
           Database.Materials.Materials.CanApply(0, 0, 1) &&
           Database.Scene.RenderStates.IsCurrent(registration.RenderState) &&
           Database.Scene.RenderStates.CanApply(0, 0, 1) &&
           Database.Scene.EditorIdentities.IsCurrent(registration.EditorIdentity) &&
           Database.Scene.EditorIdentities.CanApply(0, 0, 1) &&
           Database.Scene.Transforms.IsCurrent(registration.CurrentTransform) &&
           Database.Scene.Transforms.IsCurrent(registration.PreviousTransform) &&
           Database.Scene.Transforms.CanApply(0, 0, 2);

    private void RollBackPartialRegistration(
        AdvancedGpuHandle currentTransform,
        AdvancedGpuHandle previousTransform,
        AdvancedGpuHandle instance,
        AdvancedGpuHandle geometry,
        AdvancedGpuHandle deformation,
        AdvancedGpuHandle material,
        AdvancedGpuHandle renderState,
        AdvancedGpuHandle editorIdentity)
    {
        if (currentTransform.IsValid)
            Database.Scene.Transforms.TryRemoveImmediatelyBeforePublication(currentTransform);
        if (previousTransform.IsValid)
            Database.Scene.Transforms.TryRemoveImmediatelyBeforePublication(previousTransform);
        if (instance.IsValid)
            Database.Scene.Instances.TryRemoveImmediatelyBeforePublication(instance);
        if (geometry.IsValid)
            Database.Scene.Geometry.Records.TryRemoveImmediatelyBeforePublication(geometry);
        if (deformation.IsValid)
            Database.Scene.Deformations.TryRemoveImmediatelyBeforePublication(deformation);
        if (material.IsValid)
            Database.Materials.Materials.TryRemoveImmediatelyBeforePublication(material);
        if (renderState.IsValid)
            Database.Scene.RenderStates.TryRemoveImmediatelyBeforePublication(renderState);
        if (editorIdentity.IsValid)
            Database.Scene.EditorIdentities.TryRemoveImmediatelyBeforePublication(editorIdentity);
    }

    private static AdvancedTransformRecord CreateTransform(in Matrix4x4 world)
        => new() { World = world };

    private static AdvancedInstanceRecord CreateInstance(
        in Matrix4x4 world,
        in Matrix4x4 previousWorld,
        in BoundsGpu bounds,
        in DrawMetadata command)
        => new()
        {
            CurrentWorld = world,
            PreviousWorld = previousWorld,
            BoundsSphere = bounds.BoundingSphere,
            BoundsMin = bounds.AabbMin,
            BoundsMax = bounds.AabbMax,
            VisibilityFlags = EAdvancedInstanceVisibilityFlags.Enabled,
            ViewMaskLow = command.LayerMask,
            ViewMaskHigh = command.RenderPassMask,
        };

    private static AdvancedDeformationRecord CreateDeformation(
        AdvancedGpuHandle geometry,
        XRMesh? mesh)
        => new()
        {
            SourceGeometry = geometry,
            CurrentGeometry = geometry,
            PreviousGeometry = geometry,
            Animation = AdvancedGpuHandle.Invalid,
            VertexCount = checked((uint)Math.Max(0, mesh?.VertexCount ?? 0)),
        };

    private static AdvancedGeometryRecord CreateGeometry(
        GPUScene scene,
        XRMesh? mesh,
        in BoundsGpu bounds,
        in DrawMetadata command)
    {
        scene.TryGetMeshDataEntry(command.MeshID, out GPUScene.MeshDataEntry meshData);
        scene.TryGetMeshletRange(command.MeshID, out GPUScene.GpuMeshletRange meshlets);
        return new AdvancedGeometryRecord
        {
            VertexBase = meshData.FirstVertex,
            VertexCount = checked((uint)Math.Max(0, mesh?.VertexCount ?? 0)),
            IndexBase = meshData.FirstIndex,
            IndexCount = meshData.IndexCount,
            MeshletFirst = meshlets.MeshletOffset,
            MeshletCount = meshlets.MeshletCount,
            BoundsSphere = bounds.BoundingSphere,
            BoundsMin = bounds.AabbMin,
            BoundsMax = bounds.AabbMax,
            MaterialSectionFirst = command.SubmeshID,
            MaterialSectionCount = 1u,
            PrimitiveTopology = mesh?.Type ?? EPrimitiveType.Triangles,
            Source = EAdvancedGeometrySource.Static,
            // Phase 2 mirrors exact legacy atlas offsets but does not claim that
            // Phase 3's advanced immutable arenas have acquired native storage.
            Residency = EAdvancedGeometryResidency.Pending,
            MissingBehavior = EAdvancedMissingGeometryBehavior.SkipDraw,
            CookedLayoutVersion = meshData.Flags,
            Flags = command.Flags,
        };
    }

    private static AdvancedMaterialRecord CreateMaterial(
        in DrawMetadata command,
        XRMaterial? material)
        => new()
        {
            ShadingKernelGeneration = FoldToUInt(material?.ShaderStateRevision ?? 0L),
            MaterialLayoutHash = ComputeMaterialLayoutHash(in command, material),
            RenderStateClass = (EAdvancedMaterialRenderStateClass)Math.Min(
                command.StateClassID,
                (uint)EAdvancedMaterialRenderStateClass.Refractive),
            CoverageMode = (command.Flags & (uint)GPUIndirectRenderFlags.Transparent) != 0u
                ? EAdvancedMaterialCoverageMode.Refractive
                : EAdvancedMaterialCoverageMode.Opaque,
            Reserved = FoldToUInt(unchecked(
                (long)(material?.BindingValueVersion ?? 0UL) ^
                (long)(material?.BindingResourceVersion ?? 0UL))),
        };

    private static AdvancedMaterialRecord CreateMaterial(
        in DrawMetadata command,
        XRMaterial? material,
        AdvancedGpuHandle handle)
    {
        AdvancedMaterialRecord record = CreateMaterial(in command, material);
        record.StableRowId = handle.Index;
        record.Generation = handle.Generation;
        return record;
    }

    private static AdvancedRenderStateRecord CreateRenderState(
        XRMesh? mesh,
        in DrawMetadata command)
        => new()
        {
            StateClass = command.StateClassID,
            PrimitiveTopology = checked((uint)(mesh?.Type ?? EPrimitiveType.Triangles)),
            CoverageMode = (command.Flags & (uint)GPUIndirectRenderFlags.Transparent) != 0u ? 1u : 0u,
            CullMode = (command.Flags & (uint)GPUIndirectRenderFlags.DoubleSided) != 0u ? 0u : 1u,
            Flags = command.Flags,
        };

    private static AdvancedEditorIdentityRecord CreateEditorIdentity(in DrawMetadata command)
        => new()
        {
            StableInstanceId = command.RenderIdentityID,
            IdentityLow = command.DrawID,
            IdentityHigh = command.LogicalMeshID,
            SelectionId = command.RenderIdentityID,
        };

    private static ulong ComputeStructuralSignature(
        in DrawMetadata command,
        XRMesh? mesh,
        XRMaterial? material,
        int primitiveIndex)
    {
        ulong hash = 14695981039346656037ul;
        hash = Mix(hash, command.MeshID);
        hash = Mix(hash, command.MaterialID);
        hash = Mix(hash, command.SubmeshID);
        hash = Mix(hash, command.RenderPass);
        hash = Mix(hash, command.RenderPassMask);
        hash = Mix(hash, command.StateClassID);
        hash = Mix(hash, command.Flags);
        hash = Mix(hash, checked((uint)Math.Max(0, primitiveIndex)));
        hash = Mix(hash, checked((uint)Math.Max(0, mesh?.VertexCount ?? 0)));
        hash = Mix(hash, checked((uint)Math.Max(0, mesh?.IndexCount ?? 0)));
        hash = Mix(hash, unchecked((ulong)(mesh?.GeometryRevision ?? 0L)));
        hash = Mix(hash, material?.BindingLayoutVersion ?? 0u);
        hash = Mix(hash, unchecked((ulong)(material?.ShaderStateRevision ?? 0L)));
        hash = Mix(hash, unchecked((ulong)(material?.UberStateRevision ?? 0L)));
        return hash;
    }

    private static ulong ComputeContentSignature(
        in DrawMetadata command,
        in BoundsGpu bounds,
        in Matrix4x4 world,
        in Matrix4x4 previousWorld,
        XRMaterial? material)
    {
        ulong hash = Mix(command.TransformID, command.InstanceCount);
        hash = Mix(hash, command.LayerMask);
        hash = Mix(hash, bounds.BoundsVersion);
        hash = Mix(hash, material?.BindingValueVersion ?? 0u);
        hash = Mix(hash, material?.BindingResourceVersion ?? 0u);
        hash = MixMatrix(hash, in world);
        hash = MixMatrix(hash, in previousWorld);
        return hash;
    }

    private static ulong ComputeMaterialLayoutHash(
        in DrawMetadata command,
        XRMaterial? material)
    {
        ulong hash = Mix(command.MaterialID, command.StateClassID);
        hash = Mix(hash, material?.BindingLayoutVersion ?? 0u);
        hash = Mix(hash, unchecked((ulong)(material?.ShaderStateRevision ?? 0L)));
        return Mix(hash, unchecked((ulong)(material?.UberStateRevision ?? 0L)));
    }

    private static uint FoldToUInt(long value)
        => unchecked((uint)value ^ (uint)((ulong)value >> 32));

    private static ulong MixMatrix(ulong hash, in Matrix4x4 value)
    {
        hash = MixFloat(hash, value.M11); hash = MixFloat(hash, value.M12);
        hash = MixFloat(hash, value.M13); hash = MixFloat(hash, value.M14);
        hash = MixFloat(hash, value.M21); hash = MixFloat(hash, value.M22);
        hash = MixFloat(hash, value.M23); hash = MixFloat(hash, value.M24);
        hash = MixFloat(hash, value.M31); hash = MixFloat(hash, value.M32);
        hash = MixFloat(hash, value.M33); hash = MixFloat(hash, value.M34);
        hash = MixFloat(hash, value.M41); hash = MixFloat(hash, value.M42);
        hash = MixFloat(hash, value.M43); hash = MixFloat(hash, value.M44);
        return hash;
    }

    private static ulong MixFloat(ulong hash, float value)
        => Mix(hash, unchecked((uint)BitConverter.SingleToInt32Bits(value)));

    private static AdvancedSharedGpuSceneCapacityProfile CreateCapacityProfile(uint capacity)
        => new(
            new AdvancedGpuSceneCapacityProfile(
                capacity,
                capacity,
                checked(capacity * 2u),
                capacity,
                capacity,
                capacity,
                capacity,
                0u,
                0u,
                0u,
                0u,
                0u),
            capacity,
            1u,
            1u,
            0u,
            0u,
            0u);

    private static uint NextPowerOfTwo(uint value)
    {
        if (value <= 1u)
            return 1u;
        return BitOperations.RoundUpToPowerOf2(value);
    }

    private void AdvanceSequence()
        => AdvanceNonZero(ref _sequence);

    private static void AdvanceNonZero(ref ulong value)
    {
        unchecked { ++value; }
        if (value == 0ul)
            value = 1ul;
    }

    private static ulong Mix(ulong hash, ulong value)
    {
        hash ^= value;
        hash *= 1099511628211ul;
        return hash;
    }

    private struct AdvancedResidentRegistration
    {
        public IRenderCommandMesh? Source;
        public int PrimitiveIndex;
        public uint LegacyCommandIndex;
        public AdvancedGpuHandle Draw;
        public AdvancedGpuHandle Instance;
        public AdvancedGpuHandle Geometry;
        public AdvancedGpuHandle Deformation;
        public AdvancedGpuHandle Material;
        public AdvancedGpuHandle RenderState;
        public AdvancedGpuHandle EditorIdentity;
        public AdvancedGpuHandle CurrentTransform;
        public AdvancedGpuHandle PreviousTransform;
        public Matrix4x4 World;
        public ulong StructuralSignature;
        public ulong ContentSignature;
        public ulong LastSeenSequence;
        public ulong TombstoneSequence;
        public bool Active;
    }
}

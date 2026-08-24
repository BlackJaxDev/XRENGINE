using System.Runtime.CompilerServices;
using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.RenderGraph;

namespace XREngine.Rendering.Commands;

/// <summary>
/// Double-buffered, allocation-stable package prepared by collect-visible and
/// published only after its command membership and dependency inputs are
/// complete. The package never owns mutable backend handles.
/// </summary>
public sealed partial class BackendReadyFramePackage
{
    private BackendReadyRenderPass[] _passes = [];
    private BackendReadyMeshSelection[] _meshSelections = [];
    private readonly Dictionary<uint, BackendReadyMeshSelection> _meshSelectionCache = [];
    private readonly Dictionary<uint, int> _meshSelectionIndices = [];
    private IReadOnlyCollection<RenderPassMetadata>? _passMetadata;
    private int _passCount;
    private int _meshSelectionCount;

    public EBackendReadyFramePackageState State { get; private set; }
    public BackendReadyFramePackageIdentity Identity { get; private set; }
    public long PackageGeneration { get; private set; }
    public long SourceRevision { get; private set; } = -1L;
    public int CommandCount { get; private set; }
    public int MeshCommandCount { get; private set; }
    public ulong DependencySignature { get; private set; }
    public ulong ShadowCasterCommandSetSignature { get; private set; }
    public IReadOnlyCollection<RenderPassMetadata>? PassMetadata => _passMetadata;
    public ReadOnlySpan<BackendReadyRenderPass> Passes => _passes.AsSpan(0, _passCount);
    public ReadOnlySpan<BackendReadyMeshSelection> MeshSelections
        => _meshSelections.AsSpan(0, _meshSelectionCount);

    internal void Prepare(
        in BackendReadyFramePackageIdentity identity,
        long packageGeneration,
        long sourceRevision,
        Dictionary<int, ICollection<RenderCommand>> updatingPasses,
        IReadOnlyCollection<RenderPassMetadata>? passMetadata)
    {
        int previousPassCount = _passCount;
        int previousMeshSelectionCount = _meshSelectionCount;
        EnsurePassCapacity(updatingPasses.Count);

        int passCount = 0;
        foreach ((int passIndex, ICollection<RenderCommand> commands) in updatingPasses)
        {
            if (commands is not IReadOnlyCollection<RenderCommand> readOnlyCommands)
            {
                throw new InvalidOperationException(
                    $"Render pass {passIndex} uses unsupported collection type '{commands.GetType().FullName}'.");
            }

            ulong commandSetSignature = RenderCommandCollection.ComputeOcclusionCommandSetSignature(
                commands,
                out int meshCount);
            ulong dependencySignature = ComputePassDependencySignature(passIndex, commands);
            _passes[passCount++] = new BackendReadyRenderPass(
                passIndex,
                commands.Count,
                meshCount,
                commandSetSignature,
                dependencySignature,
                readOnlyCommands);
        }

        SortPasses(passCount);

        int commandCount = 0;
        int meshCommandCount = 0;
        for (int passIndex = 0; passIndex < passCount; passIndex++)
        {
            BackendReadyRenderPass pass = _passes[passIndex];
            commandCount += pass.CommandCount;
            meshCommandCount += pass.MeshCommandCount;
        }

        EnsureMeshSelectionCapacity(meshCommandCount);
        int meshSelectionCacheLimit = Math.Max(64, meshCommandCount * 2);
        if (_meshSelectionCache.Count > meshSelectionCacheLimit)
            _meshSelectionCache.Clear();
        _meshSelectionIndices.Clear();
        _meshSelectionIndices.EnsureCapacity(meshCommandCount);
        int meshSelectionCount = 0;
        ulong packageDependencySignature = 14695981039346656037UL;
        for (int passIndex = 0; passIndex < passCount; passIndex++)
        {
            BackendReadyRenderPass pass = _passes[passIndex];
            packageDependencySignature = AddHash(packageDependencySignature, pass.PassIndex);
            packageDependencySignature = AddHash(packageDependencySignature, pass.DependencySignature);

            ICollection<RenderCommand> commands =
                (ICollection<RenderCommand>)pass.Commands;
            for (int commandIndex = 0; commandIndex < commands.Count; commandIndex++)
            {
                RenderCommand command = RenderCommandCollection.GetCommandAt(
                    commands,
                    commandIndex);
                if (command is not IRenderCommandMesh meshCommand)
                    continue;

                XRMaterial? material = meshCommand.MaterialOverride ?? meshCommand.Mesh?.Material;
                BackendReadyMeshSelection selection =
                    CreateOrReuseMeshSelection(pass.PassIndex, meshCommand, material);
                _meshSelections[meshSelectionCount] = selection;
                _meshSelectionIndices[meshCommand.StableQueryKey] = meshSelectionCount;
                meshSelectionCount++;
                _meshSelectionCache[meshCommand.StableQueryKey] = selection;
            }
        }

        Identity = identity;
        PackageGeneration = packageGeneration;
        SourceRevision = sourceRevision;
        CommandCount = commandCount;
        MeshCommandCount = meshCommandCount;
        DependencySignature = MixHash(packageDependencySignature);
        _passCount = passCount;
        _meshSelectionCount = meshSelectionCount;
        ShadowCasterCommandSetSignature = ComputeShadowCasterCommandSetSignature();
        _passMetadata = passMetadata;
        State = EBackendReadyFramePackageState.Prepared;

        if (previousPassCount > passCount)
            Array.Clear(_passes, passCount, previousPassCount - passCount);
        if (previousMeshSelectionCount > meshSelectionCount)
            Array.Clear(
                _meshSelections,
                meshSelectionCount,
                previousMeshSelectionCount - meshSelectionCount);
    }

    internal void Publish()
    {
        if (State != EBackendReadyFramePackageState.Prepared)
            throw new InvalidOperationException($"Cannot publish a frame package in state {State}.");

        State = EBackendReadyFramePackageState.Published;
    }

    internal void Reset()
    {
        for (int i = 0; i < _passCount; i++)
            _passes[i] = default;
        for (int i = 0; i < _meshSelectionCount; i++)
            _meshSelections[i] = default;

        Identity = default;
        SourceRevision = -1L;
        CommandCount = 0;
        MeshCommandCount = 0;
        DependencySignature = 0UL;
        ShadowCasterCommandSetSignature = 0UL;
        _passMetadata = null;
        _passCount = 0;
        _meshSelectionCount = 0;
        ResetCanonical();
        State = EBackendReadyFramePackageState.Empty;
    }

    internal void Cancel()
    {
        Reset();
        _meshSelectionCache.Clear();
        _meshSelectionIndices.Clear();
        State = EBackendReadyFramePackageState.Cancelled;
    }

    public bool TryGetPass(int passIndex, out BackendReadyRenderPass pass)
    {
        int low = 0;
        int high = _passCount - 1;
        while (low <= high)
        {
            int middle = low + ((high - low) >> 1);
            BackendReadyRenderPass candidate = _passes[middle];
            if (candidate.PassIndex == passIndex)
            {
                pass = candidate;
                return true;
            }

            if (candidate.PassIndex < passIndex)
                low = middle + 1;
            else
                high = middle - 1;
        }

        pass = default;
        return false;
    }

    public bool TryGetMeshSelection(
        uint stableQueryKey,
        out BackendReadyMeshSelection selection)
    {
        if (_meshSelectionIndices.TryGetValue(stableQueryKey, out int index) &&
            (uint)index < (uint)_meshSelectionCount)
        {
            selection = _meshSelections[index];
            return true;
        }

        selection = default;
        return false;
    }

    private static ulong ComputePassDependencySignature(
        int passIndex,
        ICollection<RenderCommand> commands)
    {
        ulong hash = AddHash(14695981039346656037UL, passIndex);
        for (int i = 0; i < commands.Count; i++)
        {
            RenderCommand command = RenderCommandCollection.GetCommandAt(commands, i);
            hash = AddHash(hash, command.StableQueryKey);
            hash = AddHash(hash, command.Enabled ? 1u : 0u);
            hash = AddHash(hash, RuntimeHelpers.GetHashCode(command));
            if (command is IRenderCommandMesh meshCommand)
            {
                XRMaterial? material = meshCommand.MaterialOverride ?? meshCommand.Mesh?.Material;
                hash = AddHash(hash, ComputeMeshDependencySignature(meshCommand, material));
            }
        }

        return MixHash(hash);
    }

    private ulong ComputeShadowCasterCommandSetSignature()
    {
        ulong hash = 14695981039346656037UL;
        AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.PreRender);
        AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.OpaqueDeferred);
        AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.OpaqueForward);
        AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.MaskedForward);
        AddShadowCasterPassSignature(ref hash, EDefaultRenderPass.PostRender);
        return MixHash(hash);
    }

    private void AddShadowCasterPassSignature(ref ulong hash, EDefaultRenderPass renderPass)
    {
        int passIndex = (int)renderPass;
        if (!TryGetPass(passIndex, out BackendReadyRenderPass pass))
        {
            hash = AddHash(hash, passIndex);
            return;
        }

        ulong contentSignature = RenderCommandCollection.ComputeShadowCasterPassContentSignature(
            (ICollection<RenderCommand>)pass.Commands);
        hash = AddHash(
            hash,
            pass.CommandSetSignature ^
            BitOperations.RotateLeft(contentSignature, 17) ^
            unchecked((uint)passIndex));
    }

    private static ulong ComputeMeshDependencySignature(
        IRenderCommandMesh command,
        XRMaterial? material)
    {
        ulong hash = AddHash(14695981039346656037UL, command.StableQueryKey);
        hash = AddHash(hash, command.Mesh is null ? 0 : RuntimeHelpers.GetHashCode(command.Mesh));
        hash = AddHash(hash, material is null ? 0 : RuntimeHelpers.GetHashCode(material));
        hash = AddHash(
            hash,
            command.RenderOptionsOverride is null
                ? 0
                : RuntimeHelpers.GetHashCode(command.RenderOptionsOverride));
        hash = AddHash(hash, command.Instances);
        hash = AddHash(hash, command.ForceCpuRendering ? 1u : 0u);
        if (material is not null)
        {
            hash = AddHash(hash, material.BindingLayoutVersion);
            hash = AddHash(hash, unchecked((ulong)material.ShaderStateRevision));
            hash = AddHash(hash, unchecked((ulong)material.UberStateRevision));
        }
        return MixHash(hash);
    }

    private BackendReadyMeshSelection CreateOrReuseMeshSelection(
        int renderPass,
        IRenderCommandMesh command,
        XRMaterial? material)
    {
        RenderingParameters? renderOptions =
            command.RenderOptionsOverride ?? material?.RenderOptions;
        uint instances = command.Instances;
        bool forceCpuRendering = command.ForceCpuRendering;
        bool excludeFromGpuIndirect =
            renderOptions?.ExcludeFromGpuIndirect == true;
        ulong materialBindingLayoutVersion =
            material?.BindingLayoutVersion ?? 0UL;
        long materialShaderStateRevision =
            material?.ShaderStateRevision ?? 0L;
        long materialUberStateRevision =
            material?.UberStateRevision ?? 0L;

        if (_meshSelectionCache.TryGetValue(
                command.StableQueryKey,
                out BackendReadyMeshSelection cached) &&
            cached.RenderPass == renderPass &&
            ReferenceEquals(cached.Command, command) &&
            ReferenceEquals(cached.Mesh, command.Mesh) &&
            ReferenceEquals(cached.Material, material) &&
            ReferenceEquals(cached.RenderOptions, renderOptions) &&
            cached.Instances == instances &&
            cached.ForceCpuRendering == forceCpuRendering &&
            cached.ExcludeFromGpuIndirect == excludeFromGpuIndirect &&
            cached.MaterialBindingLayoutVersion == materialBindingLayoutVersion &&
            cached.MaterialShaderStateRevision == materialShaderStateRevision &&
            cached.MaterialUberStateRevision == materialUberStateRevision)
        {
            return cached;
        }

        return new BackendReadyMeshSelection(
            renderPass,
            command.StableQueryKey,
            command,
            command.Mesh,
            material,
            renderOptions,
            instances,
            forceCpuRendering,
            excludeFromGpuIndirect,
            materialBindingLayoutVersion,
            materialShaderStateRevision,
            materialUberStateRevision,
            ComputeMeshDependencySignature(command, material));
    }

    private void EnsurePassCapacity(int required)
    {
        if (_passes.Length >= required)
            return;

        Array.Resize(ref _passes, GrowCapacity(_passes.Length, required));
    }

    private void SortPasses(int count)
    {
        // Pass counts are small and stable. Insertion sort avoids the helper
        // allocation made by Array.Sort on every collect-visible frame.
        for (int i = 1; i < count; i++)
        {
            BackendReadyRenderPass value = _passes[i];
            int destination = i;
            while (destination > 0 &&
                   _passes[destination - 1].PassIndex > value.PassIndex)
            {
                _passes[destination] = _passes[destination - 1];
                destination--;
            }

            _passes[destination] = value;
        }
    }

    private void EnsureMeshSelectionCapacity(int required)
    {
        if (_meshSelections.Length >= required)
            return;

        Array.Resize(ref _meshSelections, GrowCapacity(_meshSelections.Length, required));
    }

    private static int GrowCapacity(int current, int required)
    {
        int capacity = Math.Max(4, current);
        while (capacity < required)
            capacity = checked(capacity * 2);
        return capacity;
    }

    private static ulong AddHash(ulong hash, int value)
        => AddHash(hash, unchecked((uint)value));

    private static ulong AddHash(ulong hash, uint value)
    {
        hash ^= value;
        return hash * 1099511628211UL;
    }

    private static ulong AddHash(ulong hash, ulong value)
    {
        hash = AddHash(hash, unchecked((uint)value));
        return AddHash(hash, unchecked((uint)(value >> 32)));
    }

    private static ulong MixHash(ulong value)
    {
        value ^= value >> 30;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 27;
        value *= 0x94D049BB133111EBUL;
        return value ^ (value >> 31);
    }
}

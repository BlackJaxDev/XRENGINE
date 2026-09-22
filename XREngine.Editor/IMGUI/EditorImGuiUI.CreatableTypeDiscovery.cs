using ImGuiNET;
using System.Numerics;
using XREngine.Editor.Services;

namespace XREngine.Editor;

public static partial class EditorImGuiUI
{
    private enum CreatableTypeDiscoveryStatus
    {
        Pending,
        Ready,
        Failed,
    }

    private readonly record struct CreatableTypeDiscoverySnapshot(
        long Generation,
        CreatableTypeDiscoveryStatus Status,
        IReadOnlyList<CollectionTypeDescriptor> Descriptors,
        string? Error,
        double ElapsedMilliseconds);

    private sealed record CreatableTypeDiscoveryResult(
        CollectionTypeDescriptor[] Descriptors,
        string? Error,
        double ElapsedMilliseconds);

    private sealed class CreatableTypeDiscoveryEntry(long generation, Task<CreatableTypeDiscoveryResult> work)
    {
        public long Generation { get; } = generation;
        public Task<CreatableTypeDiscoveryResult> Work { get; } = work;
        public CreatableTypeDiscoveryResult? Published { get; set; }
    }

    private sealed class CreatableTypeDiscoveryState(long generation)
    {
        public long Generation { get; } = generation;
        public CancellationTokenSource Cancellation { get; } = new();
        public Dictionary<Type, CreatableTypeDiscoveryEntry> Entries { get; } = [];
    }

    /// <summary>
    /// Serializes cold reflection work and publishes completed immutable results
    /// only while the owner ImGui thread is drawing the requested popup.
    /// </summary>
    private static class CreatableTypeDiscovery
    {
        private static readonly object Sync = new();
        private static readonly SemaphoreSlim WorkerGate = new(1, 1);
        private static CreatableTypeDiscoveryState _state =
            new(EditorTypeCatalogGeneration.Current);

        static CreatableTypeDiscovery()
            => EditorTypeCatalogGeneration.Changed += Invalidate;

        public static CreatableTypeDiscoverySnapshot GetOrRequest(Type baseType)
        {
            baseType = Nullable.GetUnderlyingType(baseType) ?? baseType;
            if (!EditorTypeCatalogGeneration.IsTypeDiscoverable(baseType))
            {
                return new(
                    EditorTypeCatalogGeneration.Current,
                    CreatableTypeDiscoveryStatus.Failed,
                    Array.Empty<CollectionTypeDescriptor>(),
                    "The declared asset type belongs to an assembly that is unloading.",
                    0.0);
            }

            const int maxGenerationAttempts = 2;
            for (int attempt = 0; attempt < maxGenerationAttempts; attempt++)
            {
                CreatableTypeDiscoveryState? superseded;
                CreatableTypeDiscoveryResult? publishedNow = null;
                CreatableTypeDiscoverySnapshot? snapshot = null;

                lock (Sync)
                {
                    CreatableTypeDiscoveryState state = EnsureCurrentStateLocked(out superseded);

                    if (!state.Entries.TryGetValue(baseType, out CreatableTypeDiscoveryEntry? entry))
                    {
                        entry = new CreatableTypeDiscoveryEntry(
                            state.Generation,
                            StartWorker(baseType, state.Cancellation.Token));
                        state.Entries[baseType] = entry;
                    }

                    if (entry.Published is not null)
                    {
                        snapshot = CreateSnapshot(state.Generation, entry.Published);
                    }
                    else if (!entry.Work.IsCompleted)
                    {
                        snapshot = new(
                            state.Generation,
                            CreatableTypeDiscoveryStatus.Pending,
                            Array.Empty<CollectionTypeDescriptor>(),
                            null,
                            0.0);
                    }
                    else if (ReferenceEquals(state, _state)
                        && state.Generation == EditorTypeCatalogGeneration.Current)
                    {
                        publishedNow = entry.Work.GetAwaiter().GetResult();
                        entry.Published = publishedNow;
                        snapshot = CreateSnapshot(state.Generation, publishedNow);
                    }
                }

                CancelSuperseded(superseded);
                if (snapshot is null)
                    continue;

                if (publishedNow is not null)
                    LogPublication(baseType, snapshot.Value.Generation, publishedNow);

                return snapshot.Value;
            }

            return new(
                EditorTypeCatalogGeneration.Current,
                CreatableTypeDiscoveryStatus.Pending,
                Array.Empty<CollectionTypeDescriptor>(),
                null,
                0.0);
        }

        public static void Retry(Type baseType)
        {
            baseType = Nullable.GetUnderlyingType(baseType) ?? baseType;
            if (!EditorTypeCatalogGeneration.IsTypeDiscoverable(baseType))
                return;

            CreatableTypeDiscoveryState? superseded;
            lock (Sync)
            {
                CreatableTypeDiscoveryState state = EnsureCurrentStateLocked(out superseded);
                state.Entries[baseType] = new CreatableTypeDiscoveryEntry(
                    state.Generation,
                    StartWorker(baseType, state.Cancellation.Token));
            }

            CancelSuperseded(superseded);
        }

        private static Task<CreatableTypeDiscoveryResult> StartWorker(
            Type baseType,
            CancellationToken cancellationToken)
            => Task.Run(async () =>
            {
                bool ownsGate = false;
                long started = 0;
                try
                {
                    await WorkerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                    ownsGate = true;
                    started = System.Diagnostics.Stopwatch.GetTimestamp();
                    CollectionTypeDescriptor[] descriptors = DiscoverPropertyTypeDescriptors(baseType);
                    return new CreatableTypeDiscoveryResult(
                        descriptors,
                        null,
                        System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                catch (OperationCanceledException)
                {
                    return new CreatableTypeDiscoveryResult(
                        Array.Empty<CollectionTypeDescriptor>(),
                        "Type discovery was superseded by an assembly generation change.",
                        started == 0
                            ? 0.0
                            : System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                catch (Exception ex)
                {
                    return new CreatableTypeDiscoveryResult(
                        Array.Empty<CollectionTypeDescriptor>(),
                        $"{ex.GetType().Name}: {ex.Message}",
                        System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                finally
                {
                    if (ownsGate)
                        WorkerGate.Release();
                }
            });

        private static CreatableTypeDiscoverySnapshot CreateSnapshot(
            long generation,
            CreatableTypeDiscoveryResult result)
            => new(
                generation,
                result.Error is null ? CreatableTypeDiscoveryStatus.Ready : CreatableTypeDiscoveryStatus.Failed,
                result.Descriptors,
                result.Error,
                result.ElapsedMilliseconds);

        private static void LogPublication(
            Type baseType,
            long generation,
            CreatableTypeDiscoveryResult result)
        {
            string typeName = baseType.FullName ?? baseType.Name;
            if (result.Error is null)
            {
                Debug.UI(
                    "[InspectorTypeDiscovery] published base={0} count={1} workerMs={2:0.000} generation={3}",
                    typeName,
                    result.Descriptors.Length,
                    result.ElapsedMilliseconds,
                    generation);
            }
            else
            {
                Debug.UIWarning(
                    "[InspectorTypeDiscovery] failed base={0} workerMs={1:0.000} generation={2}: {3}",
                    typeName,
                    result.ElapsedMilliseconds,
                    generation,
                    result.Error);
            }
        }

        private static void Invalidate(long generation)
        {
            CreatableTypeDiscoveryState? superseded = null;
            lock (Sync)
            {
                if (generation <= _state.Generation)
                    return;

                superseded = _state;
                _state = new CreatableTypeDiscoveryState(generation);
            }

            CancelSuperseded(superseded);
        }

        private static CreatableTypeDiscoveryState EnsureCurrentStateLocked(
            out CreatableTypeDiscoveryState? superseded)
        {
            long generation = EditorTypeCatalogGeneration.Current;
            if (_state.Generation == generation)
            {
                superseded = null;
                return _state;
            }

            superseded = _state;
            _state = new CreatableTypeDiscoveryState(generation);
            return _state;
        }

        private static void CancelSuperseded(CreatableTypeDiscoveryState? superseded)
            => superseded?.Cancellation.Cancel();
    }

    /// <summary>Draws an open picker and returns its selection without allocating a per-frame callback.</summary>
    internal static Type? DrawCreatablePropertyTypePickerPopup(string popupId, Type baseType)
    {
        if (!ImGui.BeginPopup(popupId))
            return null;

        Type? selectedType = null;
        string searchKey = $"{popupId}:{baseType.AssemblyQualifiedName}";
        string search = _propertyTypePickerSearch.TryGetValue(searchKey, out string? existing)
            ? existing
            : string.Empty;
        if (ImGui.InputTextWithHint("##PropertyTypeSearch", "Search types...", ref search, 256u))
            _propertyTypePickerSearch[searchKey] = search.Trim();

        ImGui.Separator();
        CreatableTypeDiscoverySnapshot snapshot = CreatableTypeDiscovery.GetOrRequest(baseType);
        if (snapshot.Generation != EditorTypeCatalogGeneration.Current)
        {
            ImGui.TextDisabled("Refreshing creatable asset types...");
        }
        else switch (snapshot.Status)
        {
            case CreatableTypeDiscoveryStatus.Pending:
                ImGui.TextDisabled("Discovering creatable asset types...");
                break;

            case CreatableTypeDiscoveryStatus.Failed:
                ImGui.TextColored(new Vector4(1.0f, 0.35f, 0.25f, 1.0f), "Type discovery failed.");
                if (!string.IsNullOrWhiteSpace(snapshot.Error))
                    ImGui.TextWrapped(snapshot.Error);
                if (ImGui.Button("Retry"))
                    CreatableTypeDiscovery.Retry(baseType);
                break;

            case CreatableTypeDiscoveryStatus.Ready:
                selectedType = DrawCreatableTypeResults(snapshot.Generation, snapshot.Descriptors, search);
                break;
        }

        if (ImGui.Button("Close"))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
        return selectedType;
    }

    private static Type? DrawCreatableTypeResults(
        long generation,
        IReadOnlyList<CollectionTypeDescriptor> descriptors,
        string search)
    {
        bool found = false;
        Type? selectedType = null;
        if (ImGui.BeginChild("##PropertyTypeList", new Vector2(0f, 280f), ImGuiChildFlags.Borders))
        {
            for (int i = 0; i < descriptors.Count; i++)
            {
                CollectionTypeDescriptor descriptor = descriptors[i];
                if (!MatchesCreatableTypeSearch(descriptor, search))
                    continue;

                found = true;
                string label = $"{descriptor.DisplayName}##{descriptor.FullName}";
                if (ImGui.Selectable(label, false))
                {
                    selectedType = descriptor.Type;
                    break;
                }

                if (ImGui.IsItemHovered())
                {
                    string tooltip = descriptor.FullName;
                    if (!string.IsNullOrEmpty(descriptor.AssemblyName))
                        tooltip += $" ({descriptor.AssemblyName})";
                    ImGui.SetTooltip(tooltip);
                }
            }

            if (!found)
                ImGui.TextDisabled("No matching types found.");

        }

        ImGui.EndChild();
        if (selectedType is null)
            return null;

        if (generation != EditorTypeCatalogGeneration.Current)
            return null;

        ImGui.CloseCurrentPopup();
        return selectedType;
    }

    private static bool MatchesCreatableTypeSearch(CollectionTypeDescriptor descriptor, string search)
        => string.IsNullOrWhiteSpace(search)
            || descriptor.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(descriptor.Namespace)
                && descriptor.Namespace.Contains(search, StringComparison.OrdinalIgnoreCase))
            || descriptor.FullName.Contains(search, StringComparison.OrdinalIgnoreCase);
}

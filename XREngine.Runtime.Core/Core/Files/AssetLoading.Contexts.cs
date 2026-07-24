using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using XREngine.Core.Files;
using XREngine.Data;
using XREngine.Data.Core;
using XRAsset = XREngine.Core.Files.XRAsset;

namespace XREngine
{
    internal static class DeferredAssetReferenceContext
    {
        [ThreadStatic]
        private static State? _state;

        private sealed class State(bool deferLoads)
        {
            private readonly Dictionary<string, DeferredAssetLoadReference> _referencesByPath = new(StringComparer.OrdinalIgnoreCase);

            public bool DeferLoads { get; } = deferLoads;

            public IReadOnlyList<DeferredAssetLoadReference> Snapshot()
                => _referencesByPath.Values
                    .OrderBy(static x => x.AssetPath, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

            public void Record(string assetPath, Type assetType)
            {
                if (_referencesByPath.TryGetValue(assetPath, out DeferredAssetLoadReference existing))
                {
                    _referencesByPath[assetPath] = existing with
                    {
                        AssetType = SelectMoreSpecificType(existing.AssetType, assetType)
                    };
                    return;
                }

                _referencesByPath.Add(assetPath, new DeferredAssetLoadReference(assetPath, assetType));
            }

            private static Type SelectMoreSpecificType(Type existingType, Type candidateType)
            {
                if (existingType == candidateType)
                    return existingType;

                if (existingType.IsAssignableFrom(candidateType))
                    return candidateType;

                if (candidateType.IsAssignableFrom(existingType))
                    return existingType;

                return existingType;
            }
        }

        internal sealed class Collector : IDisposable
        {
            private readonly State? _previousState;
            private readonly State _currentState;

            public Collector(bool deferLoads = true)
            {
                _previousState = _state;
                _currentState = new State(deferLoads);
                _state = _currentState;
            }

            public IReadOnlyList<DeferredAssetLoadReference> References => _currentState.Snapshot();

            public void Dispose()
            {
                _state = _previousState;
            }
        }

        internal static bool TryDeferAssetLoad(string assetPath, Type assetType, out XRAsset? asset)
        {
            State? state = _state;
            if (state is null || string.IsNullOrWhiteSpace(assetPath))
            {
                asset = null;
                return false;
            }

            string normalizedPath = Path.GetFullPath(assetPath);
            state.Record(normalizedPath, assetType);

            if (!state.DeferLoads)
            {
                asset = null;
                return false;
            }

            asset = CreatePlaceholderAsset(normalizedPath, assetType);
            return true;
        }

        private static XRAsset? CreatePlaceholderAsset(string assetPath, Type assetType)
        {
            if (assetType.IsAbstract || assetType.IsInterface || !typeof(XRAsset).IsAssignableFrom(assetType))
                return null;

            try
            {
                if (Activator.CreateInstance(assetType) is not XRAsset placeholder)
                    return null;

                placeholder.FilePath = assetPath;
                if (string.IsNullOrWhiteSpace(placeholder.Name))
                    placeholder.Name = Path.GetFileNameWithoutExtension(assetPath);
                return placeholder;
            }
            catch
            {
                return null;
            }
        }
    }

    internal static class AssetLoadProgressContext
    {
        [ThreadStatic]
        private static State? _state;

        [ThreadStatic]
        private static Stack<string>? _assetPathStack;

        internal sealed class State(string rootAssetPath, Action<AssetLoadProgress> callback)
        {
            public string RootAssetPath { get; } = rootAssetPath;
            public Action<AssetLoadProgress> Callback { get; } = callback;
            public int DiscoveredDependencyLoads { get; set; }
            public int CompletedDependencyLoads { get; set; }
            public AssetLoadProgress? LastProgress { get; set; }
        }

        internal static Scope Begin(string rootAssetPath, Action<AssetLoadProgress>? callback)
        {
            if (callback is null || string.IsNullOrWhiteSpace(rootAssetPath))
                return default;

            string normalizedRoot = Path.GetFullPath(rootAssetPath);
            var previousState = _state;
            var previousPathStack = _assetPathStack;
            _state = new State(normalizedRoot, callback);
            _assetPathStack = null;
            return new Scope(previousState, previousPathStack);
        }

        internal static AssetScope EnterAsset(string? assetPath)
        {
            if (_state is null || string.IsNullOrWhiteSpace(assetPath))
                return default;

            (_assetPathStack ??= []).Push(Path.GetFullPath(assetPath));
            return new AssetScope(active: true);
        }

        internal static void ReportStage(AssetLoadProgressStage stage, string status, float progress)
        {
            State? state = _state;
            if (state is null || string.IsNullOrWhiteSpace(status))
                return;

            string assetPath = CurrentAssetPath ?? state.RootAssetPath;
            if (!string.Equals(assetPath, state.RootAssetPath, StringComparison.Ordinal))
                return;

            Report(new AssetLoadProgress(
                state.RootAssetPath,
                assetPath,
                stage,
                status,
                Math.Clamp(progress, 0.0f, 1.0f),
                state.CompletedDependencyLoads,
                state.DiscoveredDependencyLoads));
        }

        internal static void BeginReferencedAssetLoad(string assetPath, Type expectedType)
        {
            State? state = _state;
            if (state is null || string.IsNullOrWhiteSpace(assetPath))
                return;

            int referenceCount = ++state.DiscoveredDependencyLoads;
            string fileLabel = Path.GetFileName(assetPath);
            string typeLabel = expectedType.Name;
            string status = string.IsNullOrWhiteSpace(fileLabel)
                ? $"Resolving asset reference {referenceCount} ({typeLabel})"
                : $"Resolving asset reference {referenceCount}: {fileLabel} ({typeLabel})";
            Report(new AssetLoadProgress(
                state.RootAssetPath,
                Path.GetFullPath(assetPath),
                AssetLoadProgressStage.ResolvingDependencies,
                status,
                ComputeDependencyProgress(state),
                state.CompletedDependencyLoads,
                state.DiscoveredDependencyLoads));
        }

        internal static void CompleteReferencedAssetLoad(string assetPath, Type expectedType)
        {
            State? state = _state;
            if (state is null || string.IsNullOrWhiteSpace(assetPath))
                return;

            state.CompletedDependencyLoads = Math.Min(state.CompletedDependencyLoads + 1, Math.Max(state.DiscoveredDependencyLoads, 1));
            string fileLabel = Path.GetFileName(assetPath);
            string typeLabel = expectedType.Name;
            string status = string.IsNullOrWhiteSpace(fileLabel)
                ? $"Resolved {state.CompletedDependencyLoads}/{Math.Max(state.DiscoveredDependencyLoads, state.CompletedDependencyLoads)} asset references ({typeLabel})"
                : $"Resolved {state.CompletedDependencyLoads}/{Math.Max(state.DiscoveredDependencyLoads, state.CompletedDependencyLoads)} asset references. Last: {fileLabel} ({typeLabel})";
            Report(new AssetLoadProgress(
                state.RootAssetPath,
                Path.GetFullPath(assetPath),
                AssetLoadProgressStage.ResolvingDependencies,
                status,
                ComputeDependencyProgress(state),
                state.CompletedDependencyLoads,
                Math.Max(state.DiscoveredDependencyLoads, state.CompletedDependencyLoads)));
        }

        private static string? CurrentAssetPath
            => _assetPathStack is { Count: > 0 } stack ? stack.Peek() : null;

        private static void Report(AssetLoadProgress progress)
        {
            State? state = _state;
            if (state is null)
                return;

            if (state.LastProgress is AssetLoadProgress lastProgress && lastProgress.Equals(progress))
                return;

            state.LastProgress = progress;
            state.Callback(progress);
        }

        private static float ComputeDependencyProgress(State state)
        {
            int total = Math.Max(state.DiscoveredDependencyLoads, state.CompletedDependencyLoads);
            if (total <= 0)
                return 0.60f;

            return Math.Clamp(0.60f + (state.CompletedDependencyLoads / (float)total) * 0.30f, 0.60f, 0.90f);
        }

        internal readonly struct Scope(State? previousState, Stack<string>? previousAssetPathStack) : IDisposable
        {
            private readonly State? _previousState = previousState;
            private readonly Stack<string>? _previousAssetPathStack = previousAssetPathStack;

            public void Dispose()
            {
                _state = _previousState;
                _assetPathStack = _previousAssetPathStack;
            }
        }

        internal readonly struct AssetScope(bool active) : IDisposable
        {
            private readonly bool _active = active;

            public void Dispose()
            {
                if (!_active)
                    return;

                Stack<string>? stack = _assetPathStack;
                if (stack is null || stack.Count == 0)
                    return;

                stack.Pop();
                if (stack.Count == 0)
                    _assetPathStack = null;
            }
        }
    }

    internal static class AssetDeserializationContext
    {
        [ThreadStatic]
        private static Stack<string>? _filePathStack;

        [ThreadStatic]
        private static Stack<bool>? _rootAssetPendingStack;

        public static string? CurrentFilePath
            => _filePathStack is { Count: > 0 } stack ? stack.Peek() : null;

        public static Scope Push(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return default;

            (_filePathStack ??= []).Push(Path.GetFullPath(filePath));
            (_rootAssetPendingStack ??= []).Push(true);
            return new Scope(active: true);
        }

        public static bool ConsumeRootAsset()
        {
            Stack<bool>? stack = _rootAssetPendingStack;
            if (stack is null || stack.Count == 0 || !stack.Peek())
                return false;

            _ = stack.Pop();
            stack.Push(false);
            return true;
        }

        internal readonly struct Scope(bool active) : IDisposable
        {
            private readonly bool _active = active;

            public void Dispose()
            {
                if (!_active)
                    return;

                Stack<string>? stack = _filePathStack;
                if (stack is null || stack.Count == 0)
                    return;

                stack.Pop();
                if (stack.Count == 0)
                    _filePathStack = null;

                Stack<bool>? rootStack = _rootAssetPendingStack;
                if (rootStack is null || rootStack.Count == 0)
                    return;

                rootStack.Pop();
                if (rootStack.Count == 0)
                    _rootAssetPendingStack = null;
            }
        }
    }
}
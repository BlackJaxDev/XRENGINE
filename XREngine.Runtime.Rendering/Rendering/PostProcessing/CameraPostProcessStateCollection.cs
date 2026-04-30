using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.Serialization;
using XREngine.Data.Colors;
using XREngine.Data.Core;
using YamlDotNet.Serialization;

namespace XREngine.Rendering.PostProcessing;

/// <summary>
/// Stores the per-camera post-processing values for every render pipeline the camera has used.
/// Values are organized per-stage so they can be resolved directly from a pipeline's schema.
/// </summary>
[Serializable]
public sealed class CameraPostProcessStateCollection
{
    private readonly object _pipelinesSync = new();
    private Dictionary<Guid, PipelinePostProcessState> _pipelines = new();

    public IReadOnlyDictionary<Guid, PipelinePostProcessState> Pipelines => _pipelines;

    public PipelinePostProcessState GetOrCreateState(RenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        lock (_pipelinesSync)
        {
            if (!_pipelines.TryGetValue(pipeline.ID, out var state))
            {
                state = new PipelinePostProcessState();
                _pipelines[pipeline.ID] = state;
                state.BindToPipeline(pipeline);
                return state;
            }

            // Only rebind if this state is not already for this pipeline ID (or schema changed).
            if (state.PipelineId != pipeline.ID)
                state.BindToPipeline(pipeline);

            return state;
        }
    }

    public bool TryGetState(Guid pipelineId, out PipelinePostProcessState? state)
    {
        lock (_pipelinesSync)
            return _pipelines.TryGetValue(pipelineId, out state);
    }

    public void SetState(Guid pipelineId, PipelinePostProcessState replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        lock (_pipelinesSync)
            _pipelines[pipelineId] = replacement;
    }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        lock (_pipelinesSync)
        {
            if (_pipelines is null)
            {
                _pipelines = new();
                return;
            }

            _pipelines = new Dictionary<Guid, PipelinePostProcessState>(_pipelines);
        }
    }
}

/// <summary>
/// Stores all post-processing stage values for a specific render pipeline instance.
/// </summary>
[Serializable]
public sealed class PipelinePostProcessState
{
    private readonly object _stagesSync = new();
    private Dictionary<string, PostProcessStageState> _stages = new(StringComparer.OrdinalIgnoreCase);

    public Guid PipelineId { get; private set; }
    public string PipelineName { get; private set; } = string.Empty;

    [YamlIgnore]
    public RenderPipelinePostProcessSchema Schema { get; private set; } = RenderPipelinePostProcessSchema.Empty;

    public IReadOnlyDictionary<string, PostProcessStageState> Stages => _stages;

    public void BindToPipeline(RenderPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);

        lock (_stagesSync)
        {
            PipelineId = pipeline.ID;
            PipelineName = pipeline.DebugName;
            Schema = pipeline.PostProcessSchema ?? RenderPipelinePostProcessSchema.Empty;
            SynchronizeStages();
        }
    }

    public bool TryGetStage(string stageKey, out PostProcessStageState? state)
    {
        lock (_stagesSync)
            return _stages.TryGetValue(stageKey, out state);
    }

    public PostProcessStageState? GetStage(string stageKey)
    {
        lock (_stagesSync)
            return _stages.TryGetValue(stageKey, out var stage) ? stage : null;
    }

    public PostProcessStageState? GetStage(Type backingType)
    {
        ArgumentNullException.ThrowIfNull(backingType);
        lock (_stagesSync)
            return _stages.Values.FirstOrDefault(stage => stage.Descriptor?.BackingType == backingType);
    }

    public PostProcessStageState? GetStage<TBacking>() where TBacking : class
        => GetStage(typeof(TBacking));

    public PostProcessStageState? FindStageByParameter(string parameterName)
    {
        ArgumentException.ThrowIfNullOrEmpty(parameterName);
        lock (_stagesSync)
            return _stages.Values.FirstOrDefault(stage => stage.Values.ContainsKey(parameterName));
    }

    private void SynchronizeStages()
    {
        if (Schema.IsEmpty)
        {
            _stages.Clear();
            return;
        }

        foreach (var (key, descriptor) in Schema.StagesByKey)
        {
            if (!_stages.TryGetValue(key, out var stage))
            {
                stage = new PostProcessStageState();
                _stages[key] = stage;
            }

            stage.AttachDescriptor(descriptor);
        }

        var obsolete = _stages.Keys
            .Where(key => !Schema.StagesByKey.ContainsKey(key))
            .ToList();
        foreach (var key in obsolete)
            _stages.Remove(key);
    }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        if (_stages is null)
            _stages = new(StringComparer.OrdinalIgnoreCase);
        else if (_stages.Comparer != StringComparer.OrdinalIgnoreCase)
            _stages = new Dictionary<string, PostProcessStageState>(_stages, StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Represents the editable values for a single post-processing stage.
/// Values are stored in a dictionary but can optionally drive a backing XRBase settings object.
/// </summary>
[Serializable]
public sealed class PostProcessStageState : IDisposable
{
    private Dictionary<string, object?> _values = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, PropertyPathAccessor> _backingProperties = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _backingSync = new();
    private IXRNotifyPropertyChanged? _backingNotifier;
    private bool _suppressBackingCallbacks;

    public string StageKey { get; private set; } = string.Empty;

    [YamlIgnore]
    public PostProcessStageDescriptor? Descriptor { get; private set; }

    [YamlIgnore]
    public object? BackingInstance { get; private set; }

    public IReadOnlyDictionary<string, object?> Values => _values;

    internal void AttachDescriptor(PostProcessStageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);

        Descriptor = descriptor;
        StageKey = descriptor.Key;

        EnsureParameters(descriptor);
        InitializeBacking(descriptor);
    }

    public bool TryGetBacking<T>(out T? instance) where T : class
    {
        if (BackingInstance is T typed)
        {
            instance = typed;
            return true;
        }

        instance = null;
        return false;
    }

    public T? GetValue<T>(string parameterName, T? fallback = default)
    {
        if (!_values.TryGetValue(parameterName, out var raw) || raw is null)
            return fallback;

        if (raw is T typed)
            return typed;

        return TryCoerce(raw, typeof(T), out var result) && result is T converted
            ? converted
            : fallback;
    }

    public object? GetValue(string parameterName)
        => _values.TryGetValue(parameterName, out var value) ? value : null;

    public void SetValue<T>(string parameterName, T value)
    {
        _values[parameterName] = value;
        PushValueToBacking(parameterName, value);
    }

    private void EnsureParameters(PostProcessStageDescriptor descriptor)
    {
        var knownParameters = new HashSet<string>(descriptor.Parameters.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var parameter in descriptor.Parameters)
        {
            if (!_values.ContainsKey(parameter.Name))
                _values[parameter.Name] = CloneDefault(parameter.DefaultValue);
        }

        var toRemove = _values.Keys.Where(key => !knownParameters.Contains(key)).ToList();
        foreach (var key in toRemove)
            _values.Remove(key);
    }

    private static object? CloneDefault(object? value)
    {
        if (value is null)
            return null;

        if (value is ICloneable cloneable)
            return cloneable.Clone();

        return value;
    }

    private void InitializeBacking(PostProcessStageDescriptor descriptor)
    {
        TeardownBacking();

        if (descriptor.BackingType is null)
            return;

        if (!descriptor.TryCreateBacking(out object? backing) || backing is null)
        {
            if (XRRuntimeEnvironment.IsAotRuntimeBuild)
                throw new InvalidOperationException($"Post-process stage '{descriptor.Key}' uses backing type {descriptor.BackingType.FullName} without a registered factory.");

            backing = Activator.CreateInstance(descriptor.BackingType);
        }

        BackingInstance = backing;

        // Build backing property map off-thread then publish under lock to avoid concurrent mutations.
        var backingMap = new Dictionary<string, PropertyPathAccessor>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in descriptor.Parameters)
        {
            if (PropertyPathAccessor.TryCreate(descriptor.BackingType, parameter.Name, out var accessor) && accessor is not null)
                backingMap[parameter.Name] = accessor;
        }
        lock (_backingSync)
        {
            _backingProperties = backingMap;
        }

        if (BackingInstance is IXRNotifyPropertyChanged notifier)
        {
            _backingNotifier = notifier;
            notifier.PropertyChanged += OnBackingPropertyChanged;
        }

        KeyValuePair<string, PropertyPathAccessor>[]? propsSnapshot;
        lock (_backingSync)
        {
            backing = BackingInstance;
            propsSnapshot = [.. _backingProperties];
        }

        if (backing is null)
            return;

        foreach (var (parameter, property) in propsSnapshot)
        {
            if (parameter is null || !_values.TryGetValue(parameter, out var raw) || !TryCoerce(raw, property.ValueType, out var coerced))
                continue;

            _suppressBackingCallbacks = true;
            property.TrySetValue(backing, coerced);
            _suppressBackingCallbacks = false;
        }
    }

    private void PushValueToBacking<T>(string parameterName, T value)
    {
        object? backing;
        PropertyPathAccessor? property;
        lock (_backingSync)
        {
            backing = BackingInstance;
            _backingProperties.TryGetValue(parameterName, out property);
        }
        if (backing is null || property is null)
            return;

        if (!TryCoerce(value, property.ValueType, out var coerced))
            return;

        _suppressBackingCallbacks = true;
        property.TrySetValue(backing, coerced);
        _suppressBackingCallbacks = false;
    }

    private void OnBackingPropertyChanged(object? sender, IXRPropertyChangedEventArgs args)
    {
        if (_suppressBackingCallbacks)
            return;
        if (string.IsNullOrWhiteSpace(args.PropertyName))
            return;
        if (!_values.ContainsKey(args.PropertyName))
            return;

        object? value = args.NewValue;
        if (value is ColorF3 color)
            value = new Vector3(color.R, color.G, color.B);

        lock (_backingSync)
        {
            _values[args.PropertyName] = value;
        }
    }

    private static bool TryCoerce(object? value, Type targetType, out object? result)
    {
        result = null;
        if (value is null)
            return false;

        if (targetType.IsInstanceOfType(value))
        {
            result = value;
            return true;
        }

        try
        {
            if (targetType.IsEnum)
            {
                var underlying = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                result = Enum.ToObject(targetType, underlying);
                return true;
            }

            if (targetType == typeof(float))
            {
                result = Convert.ToSingle(value, CultureInfo.InvariantCulture);
                return true;
            }

            if (targetType == typeof(int))
            {
                result = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                return true;
            }

            if (targetType == typeof(bool))
            {
                result = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                return true;
            }

            if (targetType == typeof(Vector2))
            {
                if (value is Vector2 v2)
                {
                    result = v2;
                    return true;
                }

                if (value is Vector3 v3)
                {
                    result = new Vector2(v3.X, v3.Y);
                    return true;
                }

                return false;
            }

            if (targetType == typeof(Vector3))
            {
                result = value switch
                {
                    Vector3 v3 => v3,
                    ColorF3 color => new Vector3(color.R, color.G, color.B),
                    _ => null
                };
                return result is not null;
            }

            if (targetType == typeof(Vector4))
            {
                if (value is Vector4 v4)
                {
                    result = v4;
                    return true;
                }

                if (value is Vector3 v3)
                {
                    result = new Vector4(v3, 0.0f);
                    return true;
                }

                return false;
            }

            if (targetType == typeof(ColorF3))
            {
                result = value switch
                {
                    ColorF3 c => c,
                    Vector3 v => new ColorF3(v.X, v.Y, v.Z),
                    _ => null
                };
                return result is not null;
            }

            result = Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
            return true;
        }
        catch
        {
            result = null;
            return false;
        }
    }

    private void TeardownBacking()
    {
        var notifier = _backingNotifier;
        if (notifier is not null)
            notifier.PropertyChanged -= OnBackingPropertyChanged;

        _backingNotifier = null;
        lock (_backingSync)
        {
            _backingProperties.Clear();
        }
        BackingInstance = null;
    }

    public void Dispose()
    {
        TeardownBacking();
        GC.SuppressFinalize(this);
    }

    [OnDeserialized]
    private void OnDeserialized(StreamingContext context)
    {
        if (_values is null)
            _values = new(StringComparer.OrdinalIgnoreCase);
        else if (_values.Comparer != StringComparer.OrdinalIgnoreCase)
            _values = new Dictionary<string, object?>(_values, StringComparer.OrdinalIgnoreCase);

        lock (_backingSync)
        {
            _backingProperties = new(StringComparer.OrdinalIgnoreCase);
        }
    }

    private sealed class PropertyPathAccessor
    {
        private readonly PropertyInfo[] _chain;

        private PropertyPathAccessor(string path, PropertyInfo[] chain)
        {
            Path = path;
            _chain = chain;
        }

        public string Path { get; }
        public Type ValueType => _chain[^1].PropertyType;

        public static bool TryCreate(Type rootType, string path, out PropertyPathAccessor? accessor)
        {
            ArgumentNullException.ThrowIfNull(rootType);
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            string[] segments = path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (segments.Length == 0)
            {
                accessor = null;
                return false;
            }

            Type currentType = rootType;
            PropertyInfo[] chain = new PropertyInfo[segments.Length];

            for (int i = 0; i < segments.Length; i++)
            {
                PropertyInfo? property = currentType.GetProperty(segments[i], BindingFlags.Instance | BindingFlags.Public | BindingFlags.IgnoreCase);
                if (property is null)
                {
                    accessor = null;
                    return false;
                }

                if (i == segments.Length - 1)
                {
                    if (!property.CanWrite)
                    {
                        accessor = null;
                        return false;
                    }
                }
                else if (!property.CanRead)
                {
                    accessor = null;
                    return false;
                }

                chain[i] = property;
                currentType = property.PropertyType;
            }

            accessor = new PropertyPathAccessor(path, chain);
            return true;
        }

        public bool TrySetValue(object root, object? value)
        {
            if (!TryResolveOwner(root, out var owner))
                return false;

            _chain[^1].SetValue(owner, value);
            return true;
        }

        public bool TryGetValue(object root, out object? value)
        {
            value = null;
            if (!TryResolveOwner(root, out var owner))
                return false;

            value = _chain[^1].GetValue(owner);
            return true;
        }

        private bool TryResolveOwner(object current, out object? owner)
        {
            owner = current;
            for (int i = 0; i < _chain.Length - 1; i++)
            {
                owner = _chain[i].GetValue(owner);
                if (owner is null)
                    return false;
            }

            return owner is not null;
        }
    }
}

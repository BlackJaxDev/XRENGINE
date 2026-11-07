using System.Globalization;
using System.Reflection;
using XREngine.Rendering;
using XREngine.Rendering.Pipelines.Commands;
using XREngine.Rendering.PostProcessing;
using XREngine.Rendering.RenderGraph.Diagnostics;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>
/// Caches the schema tree and the detail lines of the selected stage for the Settings view. The tree rebuilds when the
/// schema or the structure changes. The detail rebuilds when the stage, the camera, or the camera state changes.
/// </summary>
public sealed class RenderPipelineWorkbenchSettingsContent
{
    private readonly List<RenderPipelineSchemaTreeRow> _tree = new(64);
    private readonly List<RenderPipelineWorkbenchInspectorItem> _stageItems = new(64);
    private readonly List<RenderPipelineWorkbenchInspectorItem> _cameraSettingItems = new(16);
    private readonly Dictionary<string, List<ViewportRenderCommand>> _consumersByStage = new(StringComparer.Ordinal);
    private readonly List<string> _stageScratch = [];
    private RenderPipelinePostProcessSchema? _schema;
    private Type? _pipelineType;
    private int _structureBuild = -1;
    private string? _stageKey;
    private XRCamera? _camera;
    private ulong _cameraStateVersion = ulong.MaxValue;

    /// <summary>Gets the rows of the schema tree.</summary>
    public IReadOnlyList<RenderPipelineSchemaTreeRow> Tree => _tree;

    /// <summary>Gets the detail lines of the selected stage.</summary>
    public List<RenderPipelineWorkbenchInspectorItem> StageItems => _stageItems;

    /// <summary>Gets the lines that list the pipeline properties that camera editors show.</summary>
    public List<RenderPipelineWorkbenchInspectorItem> CameraSettingItems => _cameraSettingItems;

    /// <summary>Gets the summary line of the schema.</summary>
    public string SchemaSummary { get; private set; } = string.Empty;

    /// <summary>Gets how many times the detail lines were rebuilt.</summary>
    public int DetailBuildCount { get; private set; }

    /// <summary>Refreshes the tree and the detail lines.</summary>
    /// <param name="model">The Workbench model.</param>
    /// <param name="stageKey">The selected stage key, or null.</param>
    /// <param name="camera">The camera that renders the selected instance, or null.</param>
    public void Update(RenderPipelineWorkbenchModel model, string? stageKey, XRCamera? camera)
    {
        RenderPipeline? pipeline = model.Pipeline;
        RenderPipelinePostProcessSchema schema = pipeline?.PostProcessSchema ?? RenderPipelinePostProcessSchema.Empty;
        bool treeChanged = !ReferenceEquals(schema, _schema) || _structureBuild != model.StructureBuildCount;
        if (treeChanged)
        {
            _schema = schema;
            _structureBuild = model.StructureBuildCount;
            RebuildConsumers(model.Structure);
            RebuildTree(schema);
        }

        Type? pipelineType = pipeline?.GetType();
        if (pipelineType != _pipelineType)
        {
            _pipelineType = pipelineType;
            RebuildCameraSettings(pipelineType);
        }

        ulong cameraVersion = camera is not null && pipeline is not null
            ? camera.GetPostProcessState(pipeline)?.ChangeVersion ?? 0UL
            : 0UL;
        if (!treeChanged &&
            string.Equals(stageKey, _stageKey, StringComparison.Ordinal) &&
            ReferenceEquals(camera, _camera) &&
            cameraVersion == _cameraStateVersion)
        {
            return;
        }

        _stageKey = stageKey;
        _camera = camera;
        _cameraStateVersion = cameraVersion;
        RebuildStage(pipeline, schema, stageKey, camera);
    }

    private void RebuildConsumers(RenderPipelineStructureSnapshot structure)
    {
        _consumersByStage.Clear();
        for (int i = 0; i < structure.Commands.Count; i++)
        {
            ViewportRenderCommand command = structure.Commands[i].Command;
            _stageScratch.Clear();
            RenderCommandInfo.CollectConsumedStages(command, _stageScratch);
            for (int s = 0; s < _stageScratch.Count; s++)
            {
                if (!_consumersByStage.TryGetValue(_stageScratch[s], out List<ViewportRenderCommand>? list))
                {
                    list = [];
                    _consumersByStage.Add(_stageScratch[s], list);
                }

                if (!list.Contains(command))
                    list.Add(command);
            }
        }
    }

    private void RebuildTree(RenderPipelinePostProcessSchema schema)
    {
        _tree.Clear();
        int parameterCount = 0;
        foreach (PostProcessStageDescriptor stage in schema.StagesByKey.Values)
            parameterCount += stage.Parameters.Count;
        SchemaSummary = schema.IsEmpty
            ? "This pipeline has no post-process schema."
            : string.Concat(
                schema.Categories.Count.ToString(CultureInfo.InvariantCulture), " categories, ",
                schema.StagesByKey.Count.ToString(CultureInfo.InvariantCulture), " stages, ",
                parameterCount.ToString(CultureInfo.InvariantCulture), " parameters.");

        HashSet<string> listed = new(StringComparer.Ordinal);
        for (int c = 0; c < schema.Categories.Count; c++)
        {
            PostProcessCategoryDescriptor category = schema.Categories[c];
            _tree.Add(new RenderPipelineSchemaTreeRow(true, category.DisplayName, string.Empty, category.Description ?? string.Empty, 0));
            for (int s = 0; s < category.StageKeys.Count; s++)
            {
                string key = category.StageKeys[s];
                if (schema.TryGetStage(key, out PostProcessStageDescriptor? stage) && stage is not null && listed.Add(key))
                    _tree.Add(BuildStageRow(stage));
            }
        }

        bool otherHeader = false;
        foreach (PostProcessStageDescriptor stage in schema.StagesByKey.Values)
        {
            if (listed.Contains(stage.Key))
                continue;

            if (!otherHeader)
            {
                _tree.Add(new RenderPipelineSchemaTreeRow(true, "Other stages", string.Empty, "Stages that no category lists.", 0));
                otherHeader = true;
            }

            _tree.Add(BuildStageRow(stage));
        }
    }

    private RenderPipelineSchemaTreeRow BuildStageRow(PostProcessStageDescriptor stage)
    {
        int consumers = _consumersByStage.TryGetValue(stage.Key, out List<ViewportRenderCommand>? list) ? list.Count : 0;
        string label = consumers == 0
            ? string.Concat(stage.DisplayName, "  (no consumer)")
            : stage.DisplayName;
        return new RenderPipelineSchemaTreeRow(false, label, stage.Key, stage.Key, consumers);
    }

    private void RebuildCameraSettings(Type? pipelineType)
    {
        _cameraSettingItems.Clear();
        if (pipelineType is null)
            return;

        PropertyInfo[] properties = pipelineType.GetProperties(BindingFlags.Instance | BindingFlags.Public);
        Array.Sort(properties, static (a, b) =>
        {
            int order = GetCameraOrder(a).CompareTo(GetCameraOrder(b));
            return order != 0 ? order : string.CompareOrdinal(a.Name, b.Name);
        });

        for (int i = 0; i < properties.Length; i++)
        {
            if (properties[i].GetCustomAttribute<RenderPipelineCameraSettingAttribute>() is null)
                continue;

            _cameraSettingItems.Add(new RenderPipelineWorkbenchInspectorItem(
                string.Concat(properties[i].Name, "  (", properties[i].PropertyType.Name, ")"),
                RenderPipelineWorkbenchSelectionEntry.None,
                1));
        }

        if (_cameraSettingItems.Count == 0)
            _cameraSettingItems.Add(new RenderPipelineWorkbenchInspectorItem("This pipeline has no camera-visible settings.", RenderPipelineWorkbenchSelectionEntry.None, 1));
    }

    private static int GetCameraOrder(PropertyInfo property)
        => property.GetCustomAttribute<RenderPipelineCameraSettingAttribute>()?.Order ?? int.MaxValue;

    private void RebuildStage(RenderPipeline? pipeline, RenderPipelinePostProcessSchema schema, string? stageKey, XRCamera? camera)
    {
        DetailBuildCount++;
        _stageItems.Clear();
        if (stageKey is null)
            return;

        if (!schema.TryGetStage(stageKey, out PostProcessStageDescriptor? stage) || stage is null)
        {
            Add(string.Concat("The schema has no stage with the key ", stageKey, "."), 0);
            AddConsumers(stageKey);
            return;
        }

        Header("Stage");
        Add(string.Concat("Key: ", stage.Key), 1);
        Add(string.Concat("Display name: ", stage.DisplayName), 1);
        Add(string.Concat("Editor section: ", stage.EditorSection.ToString()), 1);
        Add(string.Concat("Backing type: ", stage.BackingType?.FullName ?? "none, the stage keeps a value bag"), 1);
        Add(string.Concat("Backing factory: ", stage.BackingFactory is null ? "no" : "yes"), 1);
        Add(string.Concat("Custom drawer: ", stage.CustomDrawer?.GetType().Name ?? "none"), 1);
        Add(string.Concat("State evaluator: ", stage.StateEvaluator is null ? "no" : "yes"), 1);
        if (pipeline is not CustomRenderPipeline)
            Add("Defaults come from code and are read-only here.", 1);

        PostProcessStageState? cameraStage = null;
        Header("Camera");
        if (camera is null || pipeline is null)
        {
            Add("No camera renders the selected instance.", 1);
        }
        else
        {
            (bool disabled, string? reason) = stage.EvaluateState(camera);
            Add(disabled
                ? string.Concat("Disabled for this camera: ", reason ?? "no reason given")
                : "Enabled for this camera.", 1);
            camera.GetPostProcessState(pipeline)?.TryGetStage(stage.Key, out cameraStage);
        }

        Header("Parameters");
        if (stage.Parameters.Count == 0)
            Add("None.", 1);
        for (int i = 0; i < stage.Parameters.Count; i++)
            AddParameter(stage.Parameters[i], cameraStage);

        AddConsumers(stage.Key);
    }

    private void AddParameter(PostProcessParameterDescriptor parameter, PostProcessStageState? cameraStage)
    {
        string title = string.Equals(parameter.DisplayName, parameter.Name, StringComparison.Ordinal)
            ? parameter.Name
            : string.Concat(parameter.DisplayName, "  (", parameter.Name, ")");
        Add(string.Concat(title, "  ", parameter.Kind.ToString(), parameter.IsColor ? "  color" : string.Empty), 1);
        Add(string.Concat("Default: ", FormatValue(parameter.DefaultValue)), 2);
        if (parameter.IsUniform)
            Add(string.Concat("Uniform: ", parameter.UniformName ?? parameter.Name), 2);
        if (parameter.Min.HasValue || parameter.Max.HasValue || parameter.Step.HasValue)
        {
            Add(string.Concat(
                "Range: ", FormatNumber(parameter.Min), " to ", FormatNumber(parameter.Max),
                parameter.Step.HasValue ? string.Concat("  step ", FormatNumber(parameter.Step)) : string.Empty), 2);
        }

        if (parameter.EnumOptions.Count > 0)
        {
            string[] options = new string[parameter.EnumOptions.Count];
            for (int i = 0; i < options.Length; i++)
                options[i] = string.Concat(parameter.EnumOptions[i].Label, " = ", parameter.EnumOptions[i].Value.ToString(CultureInfo.InvariantCulture));
            Add(string.Concat("Options: ", string.Join(", ", options)), 2);
        }

        if (parameter.EditorSection is { } section)
            Add(string.Concat("Editor section: ", section.ToString()), 2);
        if (parameter.VisibilityCondition is not null)
            Add("Has a visibility condition.", 2);

        if (cameraStage is null)
            return;

        object? value = cameraStage.GetValue(parameter.Name);
        bool changed = !Equals(value, parameter.DefaultValue);
        Add(string.Concat("Camera value: ", FormatValue(value), changed ? "  (differs from default)" : string.Empty), 2);
    }

    private void AddConsumers(string stageKey)
    {
        Header("Consumers");
        if (!_consumersByStage.TryGetValue(stageKey, out List<ViewportRenderCommand>? consumers) || consumers.Count == 0)
        {
            Add("No command reads this stage.", 1);
            return;
        }

        for (int i = 0; i < consumers.Count; i++)
        {
            _stageItems.Add(new RenderPipelineWorkbenchInspectorItem(
                RenderPipelineWorkbenchText.GetCommandLabel(consumers[i]),
                RenderPipelineWorkbenchSelectionEntry.ForCommand(RenderPipelineWorkbenchNodeIds.Get(consumers[i])),
                1));
        }
    }

    private static string FormatNumber(float? value)
        => value.HasValue ? value.Value.ToString("0.###", CultureInfo.InvariantCulture) : "-";

    private static string FormatValue(object? value)
        => value switch
        {
            null => "none",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };

    private void Add(string text, int indent)
        => _stageItems.Add(new RenderPipelineWorkbenchInspectorItem(text, RenderPipelineWorkbenchSelectionEntry.None, indent));

    private void Header(string text)
        => _stageItems.Add(new RenderPipelineWorkbenchInspectorItem(text, RenderPipelineWorkbenchSelectionEntry.None, 0, IsHeader: true));
}

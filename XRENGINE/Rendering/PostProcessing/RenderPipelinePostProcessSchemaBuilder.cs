using System;
using System.Collections.Generic;
using System.Linq;
using XREngine.Core;
using XREngine.Data.Rendering;
using XREngine.Rendering;
using XREngine.Rendering.Models.Materials;

namespace XREngine.Rendering.PostProcessing;

public sealed class RenderPipelinePostProcessSchemaBuilder(RenderPipeline pipeline)
{
    private readonly RenderPipeline _pipeline = pipeline;
    private readonly Dictionary<string, StageDefinition> _stages = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CategoryDefinition> _categories = new(StringComparer.Ordinal);

    public PostProcessStageBuilder Stage(string key, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Stage key cannot be empty.", nameof(key));

        if (!_stages.TryGetValue(key, out var definition))
        {
            definition = new StageDefinition(key, displayName ?? key);
            _stages[key] = definition;
        }
        else if (!string.IsNullOrWhiteSpace(displayName))
        {
            definition.DisplayName = displayName!;
        }

        return new PostProcessStageBuilder(definition);
    }

    public PostProcessCategoryBuilder Category(string key, string? displayName = null)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("Category key cannot be empty.", nameof(key));

        if (!_categories.TryGetValue(key, out var definition))
        {
            definition = new CategoryDefinition(key, displayName ?? key);
            _categories[key] = definition;
        }
        else if (!string.IsNullOrWhiteSpace(displayName))
        {
            definition.DisplayName = displayName!;
        }

        return new PostProcessCategoryBuilder(definition);
    }

    internal RenderPipelinePostProcessSchema Build()
    {
        if (_stages.Count == 0)
            return RenderPipelinePostProcessSchema.Empty;

        Dictionary<string, PostProcessStageDescriptor> stageDescriptors = new(StringComparer.Ordinal);
        foreach (var stage in _stages.Values)
        {
            try
            {
                var descriptor = BuildStageDescriptor(stage);
                if (descriptor is not null)
                    stageDescriptors[stage.Key] = descriptor;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[{_pipeline.DebugName}] Failed to build post-process stage '{stage.Key}': {ex.Message}");
            }
        }

        if (stageDescriptors.Count == 0)
            return RenderPipelinePostProcessSchema.Empty;

        var categoryDescriptors = new List<PostProcessCategoryDescriptor>(_categories.Count);
        foreach (var category in _categories.Values)
        {
            var stageKeys = category.StageKeys.Where(stageDescriptors.ContainsKey).ToArray();
            if (stageKeys.Length == 0)
                continue;
            categoryDescriptors.Add(new PostProcessCategoryDescriptor(category.Key, category.DisplayName, category.Description, stageKeys));
        }

        if (categoryDescriptors.Count == 0)
        {
            // Provide a default category that lists all stages in declaration order.
            var orderedKeys = stageDescriptors.Keys.ToArray();
            categoryDescriptors.Add(new PostProcessCategoryDescriptor("default", "Post Processing", null, orderedKeys));
        }

        return new RenderPipelinePostProcessSchema(stageDescriptors, categoryDescriptors);
    }

    private static PostProcessStageDescriptor? BuildStageDescriptor(StageDefinition definition)
    {
        List<PostProcessParameterDescriptor> parameters = new();
        List<XRShader> shaders = definition.BuildShaders();

        if (shaders.Count > 0)
        {
            XRRenderProgram program = new(false, true, shaders);
            program.RefreshShaderInterfaceMetadata();

            foreach (var binding in program.UniformBindings.Values)
            {
                if (binding.IsArray)
                    continue;
                if (definition.HiddenUniforms.Contains(binding.Name))
                    continue;

                if (!TryConvert(binding.EngineType, out var kind))
                    continue;

                var customization = definition.GetCustomization(binding.Name);
                string displayName = customization?.DisplayName ?? binding.Name;
                object? defaultValue = customization?.DefaultValue ?? GetDefault(kind);

                parameters.Add(new PostProcessParameterDescriptor(
                    binding.Name,
                    displayName,
                    kind,
                    isUniform: true,
                    uniformName: binding.Name,
                    defaultValue,
                    customization?.IsColor ?? false,
                    customization?.Min,
                    customization?.Max,
                    customization?.Step,
                    customization?.EnumOptions,
                    null));
            }
        }

        foreach (var custom in definition.CustomParameters)
        {
            parameters.Add(new PostProcessParameterDescriptor(
                custom.Name,
                custom.DisplayName,
                custom.Kind,
                isUniform: false,
                uniformName: null,
                custom.DefaultValue,
                custom.IsColor,
                custom.Min,
                custom.Max,
                custom.Step,
                custom.EnumOptions,
                custom.VisibilityCondition));
        }

        if (parameters.Count == 0)
            return null;

        return new PostProcessStageDescriptor(definition.Key, definition.DisplayName, parameters, definition.BackingType);
    }

    private static bool TryConvert(EShaderVarType? type, out PostProcessParameterKind kind)
    {
        kind = PostProcessParameterKind.Float;
        return type switch
        {
            EShaderVarType._float => Return(PostProcessParameterKind.Float, out kind),
            EShaderVarType._vec2 => Return(PostProcessParameterKind.Vector2, out kind),
            EShaderVarType._vec3 => Return(PostProcessParameterKind.Vector3, out kind),
            EShaderVarType._vec4 => Return(PostProcessParameterKind.Vector4, out kind),
            EShaderVarType._int => Return(PostProcessParameterKind.Int, out kind),
            EShaderVarType._uint => Return(PostProcessParameterKind.Int, out kind),
            EShaderVarType._bool => Return(PostProcessParameterKind.Bool, out kind),
            _ => false
        };

        static bool Return(PostProcessParameterKind value, out PostProcessParameterKind result)
        {
            result = value;
            return true;
        }
    }

    private static object GetDefault(PostProcessParameterKind kind)
        => kind switch
        {
            PostProcessParameterKind.Bool => false,
            PostProcessParameterKind.Float => 0.0f,
            PostProcessParameterKind.Int => 0,
            PostProcessParameterKind.Vector2 => System.Numerics.Vector2.Zero,
            PostProcessParameterKind.Vector3 => System.Numerics.Vector3.Zero,
            PostProcessParameterKind.Vector4 => System.Numerics.Vector4.Zero,
            _ => 0.0f
        };

    public sealed class StageDefinition(string key, string displayName)
    {
        private Func<IEnumerable<XRShader>>? _shaderFactory;

        public string Key { get; } = key;
        public string DisplayName { get; set; } = displayName;
        public HashSet<string> HiddenUniforms { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, UniformCustomization> Customizations { get; } = new(StringComparer.Ordinal);
        public List<CustomParameterDefinition> CustomParameters { get; } = new();
        public Type? BackingType { get; set; }

        public void SetShaderFactory(Func<IEnumerable<XRShader>> factory)
            => _shaderFactory = factory ?? throw new ArgumentNullException(nameof(factory));

        public List<XRShader> BuildShaders()
            => _shaderFactory is null ? [] : _shaderFactory().Where(shader => shader is not null).ToList();

        public UniformCustomization? GetCustomization(string uniformName)
            => Customizations.TryGetValue(uniformName, out var customization) ? customization : null;
    }

    public sealed class CategoryDefinition(string key, string displayName)
    {
        public string Key { get; } = key;
        public string DisplayName { get; set; } = displayName;
        public string? Description { get; set; }
        public List<string> StageKeys { get; } = new();
    }

    public sealed class UniformCustomization
    {
        public string? DisplayName { get; set; }
        public object? DefaultValue { get; set; }
        public float? Min { get; set; }
        public float? Max { get; set; }
        public float? Step { get; set; }
        public bool IsColor { get; set; }
        public IReadOnlyList<PostProcessEnumOption>? EnumOptions { get; set; }
    }

    public sealed class CustomParameterDefinition
    {
        public string Name { get; init; } = string.Empty;
        public string DisplayName { get; init; } = string.Empty;
        public PostProcessParameterKind Kind { get; init; }
        public object DefaultValue { get; init; } = 0;
        public bool IsColor { get; init; }
        public float? Min { get; init; }
        public float? Max { get; init; }
        public float? Step { get; init; }
        public IReadOnlyList<PostProcessEnumOption>? EnumOptions { get; init; }
        public Func<object, bool>? VisibilityCondition { get; init; }
    }

    public sealed class PostProcessStageBuilder
    {
        private readonly StageDefinition _definition;

        internal PostProcessStageBuilder(StageDefinition definition)
            => _definition = definition;

        public PostProcessStageBuilder BackedBy<TSettings>() where TSettings : class, new()
        {
            _definition.BackingType = typeof(TSettings);
            return this;
        }

        public PostProcessStageBuilder BackedBy(Type settingsType)
        {
            ArgumentNullException.ThrowIfNull(settingsType);
            _definition.BackingType = settingsType;
            return this;
        }

        public PostProcessStageBuilder WithShaderFactory(Func<IEnumerable<XRShader>> factory)
        {
            _definition.SetShaderFactory(factory);
            return this;
        }

        public PostProcessStageBuilder UsesEngineShader(string relativePath, EShaderType type)
            => WithShaderFactory(() => new[] { XRShader.EngineShader(relativePath, type) });

        public PostProcessStageBuilder HideUniform(string uniformName)
        {
            if (!string.IsNullOrWhiteSpace(uniformName))
                _definition.HiddenUniforms.Add(uniformName);
            return this;
        }

        public PostProcessStageBuilder RenameUniform(string uniformName, string displayName)
        {
            if (string.IsNullOrWhiteSpace(uniformName))
                return this;
            if (!_definition.Customizations.TryGetValue(uniformName, out var customization) || customization is null)
                customization = new UniformCustomization();
            customization.DisplayName = displayName;
            _definition.Customizations[uniformName] = customization;
            return this;
        }

        public PostProcessStageBuilder SetUniformDefault(string uniformName, object value)
        {
            if (string.IsNullOrWhiteSpace(uniformName))
                return this;
            if (!_definition.Customizations.TryGetValue(uniformName, out var customization) || customization is null)
                customization = new UniformCustomization();
            customization.DefaultValue = value;
            _definition.Customizations[uniformName] = customization;
            return this;
        }

        public PostProcessStageBuilder SetUniformRange(string uniformName, float? min, float? max, float? step = null)
        {
            if (string.IsNullOrWhiteSpace(uniformName))
                return this;
            if (!_definition.Customizations.TryGetValue(uniformName, out var customization) || customization is null)
                customization = new UniformCustomization();
            customization.Min = min;
            customization.Max = max;
            customization.Step = step;
            _definition.Customizations[uniformName] = customization;
            return this;
        }

        public PostProcessStageBuilder TreatUniformAsColor(string uniformName, bool isColor = true)
        {
            if (string.IsNullOrWhiteSpace(uniformName))
                return this;
            if (!_definition.Customizations.TryGetValue(uniformName, out var customization) || customization is null)
                customization = new UniformCustomization();
            customization.IsColor = isColor;
            _definition.Customizations[uniformName] = customization;
            return this;
        }

        public PostProcessStageBuilder DefineUniformEnum(string uniformName, params PostProcessEnumOption[] options)
        {
            if (string.IsNullOrWhiteSpace(uniformName))
                return this;
            if (!_definition.Customizations.TryGetValue(uniformName, out var customization) || customization is null)
                customization = new UniformCustomization();
            customization.EnumOptions = options;
            _definition.Customizations[uniformName] = customization;
            return this;
        }

        public PostProcessStageBuilder AddParameter(
            string name,
            PostProcessParameterKind kind,
            object defaultValue,
            string? displayName = null,
            float? min = null,
            float? max = null,
            float? step = null,
            bool isColor = false,
            IReadOnlyList<PostProcessEnumOption>? enumOptions = null,
            Func<object, bool>? visibilityCondition = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Parameter name cannot be empty.", nameof(name));

            _definition.CustomParameters.Add(new CustomParameterDefinition
            {
                Name = name,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? name : displayName!,
                Kind = kind,
                DefaultValue = defaultValue,
                Min = min,
                Max = max,
                Step = step,
                IsColor = isColor,
                EnumOptions = enumOptions,
                VisibilityCondition = visibilityCondition
            });

            return this;
        }
    }

    public sealed class PostProcessCategoryBuilder
    {
        private readonly CategoryDefinition _definition;

        internal PostProcessCategoryBuilder(CategoryDefinition definition)
            => _definition = definition;

        public PostProcessCategoryBuilder Describe(string? description)
        {
            _definition.Description = description;
            return this;
        }

        public PostProcessCategoryBuilder IncludeStage(string stageKey)
        {
            if (!string.IsNullOrWhiteSpace(stageKey) && !_definition.StageKeys.Contains(stageKey))
                _definition.StageKeys.Add(stageKey);
            return this;
        }

        public PostProcessCategoryBuilder IncludeStages(params string[] stageKeys)
        {
            if (stageKeys is null)
                return this;
            foreach (var key in stageKeys)
                IncludeStage(key);
            return this;
        }
    }
}

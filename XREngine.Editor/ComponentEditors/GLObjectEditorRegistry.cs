using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using ImGuiNET;
using XREngine.Diagnostics;
using XREngine.Rendering.OpenGL;

namespace XREngine.Editor.ComponentEditors;

/// <summary>
/// Registry that discovers and manages GL object editors marked with <see cref="GLObjectEditorAttribute"/>.
/// Provides automatic dispatch to the correct editor based on the GL object type.
/// </summary>
public static class GLObjectEditorRegistry
{
    /// <summary>
    /// Delegate signature for GL object editor methods.
    /// </summary>
    public delegate void GLObjectEditorDelegate(OpenGLRenderer.GLObjectBase glObject);

    private static readonly Dictionary<Type, GLObjectEditorDelegate> _editors = new();
    private static bool _initialized;
    private static readonly object _lock = new();

    /// <summary>
    /// Gets whether the registry has been initialized.
    /// </summary>
    public static bool IsInitialized => _initialized;

    /// <summary>
    /// Gets the number of registered editors.
    /// </summary>
    public static int RegisteredEditorCount => _editors.Count;

    /// <summary>
    /// Ensures the registry is initialized, scanning for editors if necessary.
    /// Thread-safe.
    /// </summary>
    public static void EnsureInitialized()
    {
        if (_initialized)
            return;

        lock (_lock)
        {
            if (_initialized)
                return;

            ScanForEditors();
            _initialized = true;
        }
    }

    /// <summary>
    /// Scans all loaded assemblies for methods marked with <see cref="GLObjectEditorAttribute"/>.
    /// </summary>
    private static void ScanForEditors()
    {
        _editors.Clear();

        var editorAssembly = typeof(GLObjectEditorRegistry).Assembly;

        try
        {
            ScanAssembly(editorAssembly);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex, "Failed to scan editor assembly for GL object editors.");
        }
    }

    private static void ScanAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (!type.IsClass || !type.IsAbstract || !type.IsSealed) // static class check
                continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var attribute = method.GetCustomAttribute<GLObjectEditorAttribute>();
                if (attribute is null)
                    continue;

                if (!ValidateEditorMethod(method, out string? error))
                {
                    Debug.LogWarning($"Invalid GL object editor method '{type.Name}.{method.Name}': {error}");
                    continue;
                }

                RegisterEditor(attribute.TargetType, method);
            }
        }

        // Informational: Registered {_editors.Count} editor(s)
    }

    private static bool ValidateEditorMethod(MethodInfo method, out string? error)
    {
        error = null;

        if (method.ReturnType != typeof(void))
        {
            error = "Method must return void.";
            return false;
        }

        var parameters = method.GetParameters();
        if (parameters.Length != 1)
        {
            error = "Method must have exactly one parameter.";
            return false;
        }

        var paramType = parameters[0].ParameterType;
        if (!typeof(OpenGLRenderer.GLObjectBase).IsAssignableFrom(paramType) && paramType != typeof(OpenGLRenderer.GLObjectBase))
        {
            error = $"Parameter must be of type OpenGLRenderer.GLObjectBase or a derived type, got {paramType.Name}.";
            return false;
        }

        return true;
    }

    private static void RegisterEditor(Type targetType, MethodInfo method)
    {
        var editorDelegate = (GLObjectEditorDelegate)Delegate.CreateDelegate(typeof(GLObjectEditorDelegate), method);

        if (_editors.TryGetValue(targetType, out var existing))
        {
            Debug.LogWarning($"GL object editor for type '{targetType.Name}' already registered. Overwriting with '{method.DeclaringType?.Name}.{method.Name}'.");
        }

        _editors[targetType] = editorDelegate;
        // Registered GL object editor: {targetType.Name} -> {method.DeclaringType?.Name}.{method.Name}
    }

    /// <summary>
    /// Manually registers an editor for a specific GL object type.
    /// </summary>
    /// <param name="targetType">The GL object type to edit.</param>
    /// <param name="editor">The editor delegate.</param>
    public static void Register(Type targetType, GLObjectEditorDelegate editor)
    {
        EnsureInitialized();
        lock (_lock)
        {
            _editors[targetType] = editor;
        }
    }

    /// <summary>
    /// Manually registers an editor for a specific GL object type.
    /// </summary>
    /// <typeparam name="T">The GL object type to edit.</typeparam>
    /// <param name="editor">The editor action.</param>
    public static void Register<T>(Action<T> editor) where T : OpenGLRenderer.GLObjectBase
    {
        Register(typeof(T), glObj => editor((T)glObj));
    }

    /// <summary>
    /// Unregisters an editor for the specified GL object type.
    /// </summary>
    /// <param name="targetType">The type to unregister.</param>
    /// <returns>True if an editor was removed.</returns>
    public static bool Unregister(Type targetType)
    {
        lock (_lock)
        {
            return _editors.Remove(targetType);
        }
    }

    /// <summary>
    /// Attempts to get the editor delegate for a specific GL object type.
    /// </summary>
    /// <param name="glObjectType">The GL object type.</param>
    /// <param name="editor">The found editor delegate, or null.</param>
    /// <returns>True if an editor was found.</returns>
    public static bool TryGetEditor(Type glObjectType, out GLObjectEditorDelegate? editor)
    {
        EnsureInitialized();

        // First try exact match
        if (_editors.TryGetValue(glObjectType, out editor))
            return true;

        // Then try to find an editor for a base type or interface
        foreach (var kvp in _editors)
        {
            if (kvp.Key.IsAssignableFrom(glObjectType))
            {
                editor = kvp.Value;
                return true;
            }
        }

        editor = null;
        return false;
    }

    /// <summary>
    /// Draws the appropriate ImGui inspector for the given GL object.
    /// Uses the attribute-based registry to find the correct editor.
    /// </summary>
    /// <param name="glObject">The GL object to inspect.</param>
    /// <returns>True if an editor was found and invoked.</returns>
    public static bool DrawInspector(OpenGLRenderer.GLObjectBase glObject)
    {
        if (glObject is null)
        {
            ImGui.TextDisabled("No GL object selected.");
            return false;
        }

        EnsureInitialized();

        var glObjectType = glObject.GetType();

        if (TryGetEditor(glObjectType, out var editor) && editor is not null)
        {
            try
            {
                editor(glObject);
                return true;
            }
            catch (Exception ex)
            {
                ImGui.TextColored(new System.Numerics.Vector4(1.0f, 0.3f, 0.3f, 1.0f), 
                    $"Editor error: {ex.Message}");
                Debug.LogException(ex, $"GL object editor for '{glObjectType.Name}' threw an exception.");
                return false;
            }
        }

        // Fallback: draw basic header info
        ImGui.TextDisabled($"No custom editor for {glObjectType.Name}.");
        DrawFallbackInspector(glObject);
        return false;
    }

    private static void DrawFallbackInspector(OpenGLRenderer.GLObjectBase glObject)
    {
        ImGui.TextUnformatted($"Type: {glObject.Type}");

        string bindingIdText = glObject.TryGetBindingId(out uint bindingId) 
            ? bindingId.ToString() 
            : "<Ungenerated>";
        ImGui.TextUnformatted($"Binding ID: {bindingIdText}");

        ImGui.TextUnformatted($"Generated: {(glObject.IsGenerated ? "Yes" : "No")}");
    }

    /// <summary>
    /// Gets all registered editor types.
    /// </summary>
    /// <returns>Enumerable of registered GL object types with editors.</returns>
    public static IEnumerable<Type> GetRegisteredTypes()
    {
        EnsureInitialized();
        return _editors.Keys.ToList();
    }

    /// <summary>
    /// Forces re-scanning for editors. Useful if new assemblies are loaded.
    /// </summary>
    public static void Refresh()
    {
        lock (_lock)
        {
            _initialized = false;
            EnsureInitialized();
        }
    }
}

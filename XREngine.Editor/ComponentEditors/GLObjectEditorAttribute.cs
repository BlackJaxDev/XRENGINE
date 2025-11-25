using System;
using System.Reflection;
using XREngine.Rendering.OpenGL;
using XREngine.Scene;

namespace XREngine.Editor.ComponentEditors;

/// <summary>
/// Attribute to mark a method as a custom ImGui editor for a specific OpenGL object type.
/// The method must have the signature: static void MethodName(OpenGLRenderer.GLObjectBase glObject)
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public class GLObjectEditorAttribute : Attribute
{
    /// <summary>
    /// The type of GL object this editor handles.
    /// Must be assignable to OpenGLRenderer.GLObjectBase.
    /// </summary>
    public Type TargetType { get; }

    /// <summary>
    /// Priority for editor selection. Higher priority editors are chosen over lower ones
    /// when multiple editors match a type. Default is 0.
    /// </summary>
    public int Priority { get; set; } = 0;

    public GLObjectEditorAttribute(Type targetType)
    {
        if (!typeof(OpenGLRenderer.GLObjectBase).IsAssignableFrom(targetType))
            throw new ArgumentException($"Target type must be assignable to {nameof(OpenGLRenderer.GLObjectBase)}", nameof(targetType));

        TargetType = targetType;
    }
}

/// <summary>
/// Attribute to specify the default ImGui editor for XRTexture-derived types.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GLTextureEditorAttribute : GLObjectEditorAttribute
{
    public GLTextureEditorAttribute() : base(typeof(IGLTexture))
    {
    }
}

/// <summary>
/// Attribute to specify the ImGui editor for GLFrameBuffer.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GLFrameBufferEditorAttribute : GLObjectEditorAttribute
{
    public GLFrameBufferEditorAttribute() : base(typeof(GLFrameBuffer))
    {
    }
}

/// <summary>
/// Attribute to specify the ImGui editor for GLRenderBuffer.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class GLRenderBufferEditorAttribute : GLObjectEditorAttribute
{
    public GLRenderBufferEditorAttribute() : base(typeof(GLRenderBuffer))
    {
    }
}

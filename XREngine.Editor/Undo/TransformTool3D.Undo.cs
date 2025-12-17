using System;
using XREngine.Data.Core;
using XREngine.Scene.Components.Editing;
using XREngine.Scene.Transforms;

namespace XREngine.Editor;

internal static class TransformToolUndoAdapter
{
	private static readonly object _sync = new();
	private static TransformTool3D? _tool;
	private static Undo.ChangeScope? _scope;
	private static IDisposable? _userInteractionScope;

	public static void Attach(TransformTool3D? tool)
	{
		lock (_sync)
		{
			if (ReferenceEquals(_tool, tool))
				return;

			DetachInternal();

			if (tool is null)
				return;

			_tool = tool;
			_tool.MouseDown += ToolMouseDown;
			_tool.MouseUp += ToolMouseUp;
			_tool.PropertyChanged += ToolPropertyChanged;
			_tool.Destroyed += ToolDestroyed;

			TrackTarget(_tool.TargetSocket);
		}
	}

	public static void Detach()
	{
		lock (_sync)
			DetachInternal();
	}

	private static void ToolMouseDown()
	{
		lock (_sync)
		{
			var tool = _tool;
			if (tool is null)
				return;

			var target = tool.TargetSocket;
			if (target is null)
				return;

			CloseScope();
			TrackTarget(target);

			string description = BuildDescription(target);
			_userInteractionScope = Undo.BeginUserInteraction();
			_scope = Undo.BeginChange(description);
		}
	}

	private static void ToolMouseUp()
	{
		lock (_sync)
			CloseScope();
	}

	private static void ToolPropertyChanged(object? sender, IXRPropertyChangedEventArgs e)
	{
		if (!string.Equals(e.PropertyName, nameof(TransformTool3D.TargetSocket), StringComparison.Ordinal))
			return;

		lock (_sync)
		{
			CloseScope();
			TrackTarget(_tool?.TargetSocket);
		}
	}

	private static void ToolDestroyed(XRObjectBase obj)
	{
		lock (_sync)
			DetachInternal();
	}

	private static void DetachInternal()
	{
		if (_tool is null)
		{
			CloseScope();
			return;
		}

		_tool.MouseDown -= ToolMouseDown;
		_tool.MouseUp -= ToolMouseUp;
		_tool.PropertyChanged -= ToolPropertyChanged;
		_tool.Destroyed -= ToolDestroyed;
		_tool = null;
		CloseScope();
	}

	private static void TrackTarget(TransformBase? target)
	{
		if (target is null)
			return;

		Undo.Track(target);
		Undo.Track(target.SceneNode);
	}

	private static void CloseScope()
	{
		_scope?.Dispose();
		_scope = null;
		_userInteractionScope?.Dispose();
		_userInteractionScope = null;
	}

	private static string BuildDescription(TransformBase target)
	{
		string? name = target.SceneNode?.Name;
		if (string.IsNullOrWhiteSpace(name))
			name = target.Name;
		if (string.IsNullOrWhiteSpace(name))
			name = target.GetType().Name;

		string verb = TransformTool3D.TransformMode switch
		{
			ETransformMode.Translate => "Move",
			ETransformMode.Rotate => "Rotate",
			ETransformMode.Scale => "Scale",
			_ => "Transform"
		};

		return $"{verb} {name}";
	}
}

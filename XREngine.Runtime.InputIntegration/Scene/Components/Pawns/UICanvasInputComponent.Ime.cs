using System.Numerics;
using XREngine.Rendering;
using XREngine.Rendering.UI;
using XREngine.Data.Core;
using XREngine.Rendering.UI.State;

namespace XREngine.Components;

public partial class UICanvasInputComponent
{
    private UITextInputComponent? _imeTarget;
    private long _imeInteractionRevision;
    private long _imeFocusEpoch;
    private UIElementIdentity _imeIdentity;
    private bool _imeCandidateTickRegistered;

    private void UpdateImeFocusTarget()
    {
        UITextInputComponent? focused = FocusedComponent as UITextInputComponent;
        UIElementIdentity identity = focused?.AccessibilityIdentity ?? default;
        long revision = focused?.FocusEpoch ?? 0;
        if (OwningPawn?.Viewport is XRViewport viewport)
            viewport.Window?.TrySetImeFocusTarget(identity, revision);
    }

    /// <summary>Applies ordered native IME events to the field focused at composition start.</summary>
    internal void DispatchImeEvents(ReadOnlySpan<WindowImeEvent> events)
    {
        for (int i = 0; i < events.Length; i++)
        {
            WindowImeEvent item = events[i];
            if (item.Kind == WindowImeEventKind.Start)
            {
                _imeTarget?.CancelComposition();
                _imeTarget = FocusedComponent as UITextInputComponent;
                if (_imeTarget is not null &&
                    (!IsPointerTargetEligible(_imeTarget) || item.FocusIdentity.Id == 0 ||
                     _imeTarget.AccessibilityIdentity != item.FocusIdentity ||
                     _imeTarget.FocusEpoch != item.FocusRevision))
                    _imeTarget = null;
                _imeIdentity = item.FocusIdentity;
                _imeInteractionRevision = _imeTarget?.InteractionRevision ?? 0;
                _imeFocusEpoch = _imeTarget?.FocusEpoch ?? 0;
                if (_imeTarget?.BeginComposition() != true)
                    _imeTarget = null;
                else
                    EnsureImeCandidateTick();
                continue;
            }

            UITextInputComponent? target = _imeTarget;
            if (target is null)
                continue;
            if (!ReferenceEquals(FocusedComponent, target) || !target.IsFocused ||
                !IsPointerTargetEligible(target) ||
                item.FocusIdentity != _imeIdentity || item.FocusRevision != _imeFocusEpoch ||
                target.InteractionRevision != _imeInteractionRevision ||
                target.FocusEpoch != _imeFocusEpoch)
            {
                target.CancelComposition();
                _imeTarget = null;
                StopImeCandidateTick();
                continue;
            }

            switch (item.Kind)
            {
                case WindowImeEventKind.Update:
                    target.UpdateComposition(item.Text, item.CaretOffset);
                    UpdateImeCandidatePlacement();
                    break;
                case WindowImeEventKind.Commit:
                    try
                    {
                        if (!target.CommitComposition(item.Text))
                            target.CancelComposition();
                    }
                    finally
                    {
                        try
                        {
                            if (target.IsComposing)
                                target.CancelComposition();
                        }
                        finally
                        {
                            _imeTarget = null;
                            StopImeCandidateTick();
                        }
                    }
                    break;
                case WindowImeEventKind.Cancel:
                    target.CancelComposition();
                    _imeTarget = null;
                    StopImeCandidateTick();
                    break;
            }
        }
    }

    private void EnsureImeCandidateTick()
    {
        if (_imeCandidateTickRegistered)
            return;
        _imeCandidateTickRegistered = true;
        RegisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateImeCandidatePlacement);
    }

    private void StopImeCandidateTick()
    {
        if (!_imeCandidateTickRegistered)
            return;
        _imeCandidateTickRegistered = false;
        UnregisterTick(ETickGroup.Late, ETickOrder.Scene, UpdateImeCandidatePlacement);
    }

    private void UpdateImeCandidatePlacement()
    {
        UITextInputComponent? target = _imeTarget;
        if (target is null || !target.IsComposing || !ReferenceEquals(FocusedComponent, target) ||
            !IsPointerTargetEligible(target) ||
            target.InteractionRevision != _imeInteractionRevision ||
            target.FocusEpoch != _imeFocusEpoch)
        {
            target?.CancelComposition();
            _imeTarget = null;
            StopImeCandidateTick();
            return;
        }

        UICanvasComponent? canvas = GetCameraCanvas();
        object? output = OwningPawn?.Viewport ?? (object?)canvas;
        UIPublication? publication = canvas is not null && output is not null
            ? canvas.GetPresentedPublication(output) : null;
        UITextComponent text = target.TextComponent;
        if (publication is null || canvas is null ||
            publication.LayoutGeneration != canvas.CanvasTransform.CurrentLayoutGeneration ||
            !text.TryGetCurrentLayoutResult(out UITextLayoutResult layout) ||
            !target.TryGetCaretLocalBounds(layout, out Vector4 bounds) ||
            !publication.TryGetRenderRecord(text.RenderInfo2D.MembershipId, out UIRenderRecord record) ||
            record.Identity != text.UITransform.ElementIdentity ||
            !ReferenceEquals(record.TextLayout, layout))
            return;

        if (OwningPawn?.Viewport is XRViewport viewport &&
            TryMapImeCaretClientPoint(publication, record, new Vector2(bounds.X, bounds.Y),
                out Vector2 clientPoint))
            viewport.Window?.TrySetImeCandidatePosition(clientPoint);
    }

    /// <summary>Projects a caret through only the geometry frozen in the displayed packet.</summary>
    internal static bool TryMapImeCaretClientPoint(UIPublication publication, UIRenderRecord record,
        Vector2 localCaret, out Vector2 clientPoint)
    {
        clientPoint = default;
        Vector2 canvasPoint = Vector2.Transform(localCaret, record.WorldMatrix);
        if (!float.IsFinite(canvasPoint.X) || !float.IsFinite(canvasPoint.Y))
            return false;
        if (publication.DrawSpace == ECanvasDrawSpace.Screen)
        {
            clientPoint = Vector2.Transform(canvasPoint, publication.CanvasToOutput);
        }
        else
        {
            if (!Matrix4x4.Invert(publication.WorldToCanvas, out Matrix4x4 canvasToWorld) ||
                !Matrix4x4.Invert(publication.ClipToWorld, out Matrix4x4 worldToClip))
                return false;
            Vector4 world = Vector4.Transform(new Vector4(canvasPoint, 0, 1), canvasToWorld);
            Vector4 clip = Vector4.Transform(world, worldToClip);
            if (!float.IsFinite(clip.W) || clip.W <= 0)
                return false;
            float normalizedX = clip.X / clip.W;
            float normalizedY = clip.Y / clip.W;
            clientPoint = new(
                publication.OutputRegion.MinX + (normalizedX + 1) * publication.OutputRegion.Width * 0.5f,
                publication.OutputRegion.MinY + (1 - normalizedY) * publication.OutputRegion.Height * 0.5f);
        }
        return float.IsFinite(clientPoint.X) && float.IsFinite(clientPoint.Y) &&
            clientPoint.X >= publication.OutputRegion.MinX &&
            clientPoint.Y >= publication.OutputRegion.MinY &&
            clientPoint.X <= publication.OutputRegion.MinX + publication.OutputRegion.Width &&
            clientPoint.Y <= publication.OutputRegion.MinY + publication.OutputRegion.Height;
    }
}

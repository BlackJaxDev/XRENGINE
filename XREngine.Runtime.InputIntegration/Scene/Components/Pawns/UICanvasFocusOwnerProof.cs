using XREngine.Data.Core;
using XREngine.Rendering.UI;
using XREngine.Scene;
using XREngine.Scene.Transforms;

namespace XREngine.Components;

internal readonly record struct UICanvasFocusOwnerProof(
    UICanvasInputComponent Input, ObjectConstructionIdentity InputIdentity,
    SceneNode InputNode, ObjectConstructionIdentity InputNodeIdentity,
    TransformBase InputTransform, ObjectConstructionIdentity InputTransformIdentity,
    UIListTransform? List, ObjectConstructionIdentity? ListIdentity,
    SceneNode? ListNode, ObjectConstructionIdentity? ListNodeIdentity,
    UICanvasComponent? Canvas, ObjectConstructionIdentity? CanvasIdentity,
    SceneNode? CanvasNode, ObjectConstructionIdentity? CanvasNodeIdentity,
    TransformBase? CanvasTransform, ObjectConstructionIdentity? CanvasTransformIdentity,
    UIInteractableComponent? Target, ObjectConstructionIdentity? TargetIdentity,
    SceneNode? TargetNode, ObjectConstructionIdentity? TargetNodeIdentity,
    TransformBase? TargetTransform, ObjectConstructionIdentity? TargetTransformIdentity)
{
    internal static UICanvasFocusOwnerProof? Capture(UICanvasInputComponent input,
        UIListTransform? list, UICanvasComponent? canvas, UIInteractableComponent? target)
    {
        if (input.SceneNode is not { IsTransformNull: false } inputNode ||
            !inputNode.Components.Contains(input) ||
            list is not null && (list.SceneNode is not { IsTransformNull: false } listNode ||
                !ReferenceEquals(listNode.Transform, list)) ||
            canvas is not null && (canvas.SceneNode is not { IsTransformNull: false } canvasNode ||
                !canvasNode.Components.Contains(canvas)) ||
            target is not null &&
                (target.SceneNode is not { IsTransformNull: false } targetNode ||
                 !targetNode.Components.Contains(target)))
            return null;
        TransformBase inputTransform = inputNode.Transform;
        SceneNode? capturedListNode = list?.SceneNode;
        SceneNode? capturedCanvasNode = canvas?.SceneNode;
        TransformBase? capturedCanvasTransform = capturedCanvasNode is { IsTransformNull: false } &&
            capturedCanvasNode.Transform is UICanvasTransform existingCanvasTransform
            ? existingCanvasTransform : null;
        if (canvas is not null && capturedCanvasTransform is null) return null;
        SceneNode? capturedTargetNode = target?.SceneNode;
        TransformBase? capturedTargetTransform = capturedTargetNode?.Transform;
        return new(input, ObjectConstructionIdentity.Capture(input),
            inputNode, ObjectConstructionIdentity.Capture(inputNode),
            inputTransform, ObjectConstructionIdentity.Capture(inputTransform),
            list, list is null ? null : ObjectConstructionIdentity.Capture(list),
            capturedListNode, capturedListNode is null ? null :
                ObjectConstructionIdentity.Capture(capturedListNode),
            canvas, canvas is null ? null : ObjectConstructionIdentity.Capture(canvas),
            capturedCanvasNode, capturedCanvasNode is null ? null :
                ObjectConstructionIdentity.Capture(capturedCanvasNode),
            capturedCanvasTransform, capturedCanvasTransform is null ? null :
                ObjectConstructionIdentity.Capture(capturedCanvasTransform),
            target, target is null ? null : ObjectConstructionIdentity.Capture(target),
            capturedTargetNode, capturedTargetNode is null ? null :
                ObjectConstructionIdentity.Capture(capturedTargetNode),
            capturedTargetTransform, capturedTargetTransform is null ? null :
                ObjectConstructionIdentity.Capture(capturedTargetTransform));
    }

    internal bool IsCurrent => InputIdentity.IsCurrent && InputNodeIdentity.IsCurrent &&
        InputTransformIdentity.IsCurrent &&
        !Input.IsDestroyed && !Input.IsDestroyQueued &&
        !InputNode.IsDestroyed && !InputNode.IsDestroyQueued &&
        !InputNode.IsTransformNull &&
        !InputTransform.IsDestroyed && !InputTransform.IsDestroyQueued &&
        ReferenceEquals(Input.SceneNode, InputNode) &&
        ReferenceEquals(InputNode.Transform, InputTransform) &&
        InputNode.Components.Contains(Input) &&
        (List is null || ListIdentity?.IsCurrent == true &&
            ListNodeIdentity?.IsCurrent == true &&
            !List.IsDestroyed && !List.IsDestroyQueued &&
            !ListNode!.IsDestroyed && !ListNode.IsDestroyQueued &&
            !ListNode.IsTransformNull &&
            ReferenceEquals(List.SceneNode, ListNode) &&
            ReferenceEquals(ListNode?.Transform, List)) &&
        (Canvas is null || CanvasIdentity?.IsCurrent == true &&
            CanvasNodeIdentity?.IsCurrent == true &&
            CanvasTransformIdentity?.IsCurrent == true &&
            !Canvas.IsDestroyed && !Canvas.IsDestroyQueued &&
            !CanvasNode!.IsDestroyed && !CanvasNode.IsDestroyQueued &&
            !CanvasNode.IsTransformNull &&
            !CanvasTransform!.IsDestroyed && !CanvasTransform.IsDestroyQueued &&
            ReferenceEquals(Canvas.SceneNode, CanvasNode) &&
            CanvasNode.Components.Contains(Canvas) &&
            ReferenceEquals(CanvasNode.Transform, CanvasTransform) &&
            ReferenceEquals(Input.GetCameraCanvasNonCreating(), Canvas) &&
            (List is null || ReferenceEquals(List.ParentCanvas, CanvasTransform))) &&
        (Target is null || TargetIdentity?.IsCurrent == true &&
            TargetNodeIdentity?.IsCurrent == true &&
            TargetTransformIdentity?.IsCurrent == true &&
            !Target.IsDestroyed && !Target.IsDestroyQueued &&
            !TargetNode!.IsDestroyed && !TargetNode.IsDestroyQueued &&
            !TargetNode.IsTransformNull &&
            ReferenceEquals(Target.SceneNode, TargetNode) &&
            ReferenceEquals(TargetNode?.Transform, TargetTransform) &&
            TargetNode!.Components.Contains(Target));
}

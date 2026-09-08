using XREngine.Data.Rendering;
using XREngine.Rendering.Models.Materials;
using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Rendering;

public partial class AdvancedRenderPipeline
{
    internal const string TransparentMotionReactiveMaskFBOName = "TransparentMotionReactiveMaskFBO";

    private void AppendAdvancedParticipatingTransparentMotion(ViewportRenderCommandContainer commands)
    {
        var motion = commands.Add<VPRC_IfElse>();
        motion.Label = "AdvancedParticipatingTransparentMotion";
        motion.ConditionEvaluator = HasAdvancedParticipatingTransparentMotionConsumers;
        var motionCommands = new ViewportRenderCommandContainer(this);

        using (motionCommands.AddUsing<VPRC_BindFBOByName>(x =>
            x.SetOptions(VelocityFBOName, write: true, clearColor: false, clearDepth: false, clearStencil: false)))
        {
            motionCommands.Add<VPRC_DepthTest>().Enable = true;
            motionCommands.Add<VPRC_DepthWrite>().Allow = false;
            VPRC_RenderMotionVectorsPass velocityReplay = AddParticipatingTransparentMotionReplay(motionCommands);
            velocityReplay.AdvancedLateTemporalOutput = EAdvancedLateTemporalOutput.Velocity;
            velocityReplay.UseMotionVectorMaterialVariant = false;
        }

        using (motionCommands.AddUsing<VPRC_BindFBOByName>(x =>
            x.SetOptions(TransparentMotionReactiveMaskFBOName, write: true, clearColor: false, clearDepth: false, clearStencil: false)))
        {
            motionCommands.Add<VPRC_DepthTest>().Enable = true;
            motionCommands.Add<VPRC_DepthWrite>().Allow = false;
            VPRC_RenderMotionVectorsPass reactiveReplay = AddParticipatingTransparentMotionReplay(motionCommands);
            reactiveReplay.AdvancedLateTemporalOutput = EAdvancedLateTemporalOutput.ReactiveMask;
            reactiveReplay.UseMotionVectorMaterialVariant = false;
        }

        motion.TrueCommands = motionCommands;
    }

    private static bool HasAdvancedParticipatingTransparentMotionConsumers()
        => HasRenderPassCommands((int)EDefaultRenderPass.WeightedBlendedOitForward)
           || HasRenderPassCommands((int)EDefaultRenderPass.TransparentForward)
           || HasRenderPassCommands((int)EDefaultRenderPass.OnTopForward)
           || HasRenderPassCommands((int)EDefaultRenderPass.PerPixelLinkedListForward)
           || HasRenderPassCommands((int)EDefaultRenderPass.DepthPeelingForward);

    private static VPRC_RenderMotionVectorsPass AddParticipatingTransparentMotionReplay(ViewportRenderCommandContainer commands)
    {
        VPRC_RenderMotionVectorsPass replay = commands.Add<VPRC_RenderMotionVectorsPass>();
        replay.SetOptions(false,
        [
            (int)EDefaultRenderPass.WeightedBlendedOitForward,
            (int)EDefaultRenderPass.TransparentForward,
            (int)EDefaultRenderPass.OnTopForward,
            (int)EDefaultRenderPass.PerPixelLinkedListForward,
            (int)EDefaultRenderPass.DepthPeelingForward,
        ]);
        replay.RequireAdvancedLateMotionParticipation = true;
        return replay;
    }

    private XRFrameBuffer CreateTransparentMotionReactiveMaskFBO()
    {
        IFrameBufferAttachement reactive = EnsureTextureAttachment(
            AdvancedTemporalHistoryContract.ReactiveMaskResourceName,
            () => throw new InvalidOperationException("Reactive-mask texture must be declared before its FBO."));
        IFrameBufferAttachement depth = EnsureTextureAttachment(AdvancedVisibilityResourceNames.DepthStencil, CreateDepthStencilTexture);
        return new XRFrameBuffer(
            (reactive, EFrameBufferAttachment.ColorAttachment0, 0, -1),
            (depth, EFrameBufferAttachment.DepthStencilAttachment, 0, -1))
        {
            ForceOvrMultiview = Stereo,
            Name = TransparentMotionReactiveMaskFBOName,
        };
    }

}

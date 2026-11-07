using XREngine.Rendering.Pipelines.Commands;

namespace XREngine.Editor.RenderPipelineWorkbench;

/// <summary>Edits on the post-process stage declarations of a <see cref="RenderPipelineDraftState"/>.</summary>
public static class RenderPipelineDraftStageEdits
{
    /// <summary>
    /// Removes the stage <paramref name="key"/> and each branch that tests whether the stage is enabled. A removed
    /// stage is never enabled, so each such branch becomes the commands of its false branch.
    /// </summary>
    /// <returns>True when the state changed.</returns>
    public static bool RemoveStage(RenderPipelineDraftState state, string key)
    {
        ArgumentNullException.ThrowIfNull(state);
        bool changed = state.Stages.RemoveAll(x => string.Equals(x.Key, key, StringComparison.Ordinal)) > 0;
        return UnwrapStageBranches(state.Commands, key) | changed;
    }

    /// <summary>Returns true when <paramref name="command"/> is a branch that tests whether stage <paramref name="key"/> is enabled.</summary>
    public static bool IsStageBranch(ViewportRenderCommand command, string key)
        => command is VPRC_IfElse { Condition: { Source: ERenderBranchConditionSource.StageEnabled } condition } &&
           string.Equals(condition.Key, key, StringComparison.Ordinal);

    private static bool UnwrapStageBranches(ViewportRenderCommandContainer container, string key)
    {
        bool changed = false;
        List<RenderCommandChildContainer> children = [];
        for (int i = 0; i < container.Count; i++)
        {
            ViewportRenderCommand command = container[i];
            if (IsStageBranch(command, key))
            {
                VPRC_IfElse branch = (VPRC_IfElse)command;
                container.RemoveAt(i);
                if (branch.FalseCommands is { } falseCommands)
                {
                    int insertAt = i;
                    while (falseCommands.Count > 0)
                    {
                        ViewportRenderCommand moved = falseCommands[0];
                        falseCommands.RemoveAt(0);
                        container.Insert(insertAt++, moved);
                    }
                }

                // Visit the moved commands too. They can hold more branches on the same stage.
                i--;
                changed = true;
                continue;
            }

            children.Clear();
            command.CollectChildContainers(children);
            for (int c = 0; c < children.Count; c++)
                changed |= UnwrapStageBranches(children[c].Container, key);
        }

        return changed;
    }
}

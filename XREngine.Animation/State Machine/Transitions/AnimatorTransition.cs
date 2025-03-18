namespace XREngine.Data.Animation
{
    public class AnimatorTransition(IAnimationState targetState) : IAnimationTransition
    {
        public IAnimationState TargetState { get; set; } = targetState;
        public List<IAnimationCondition> Conditions { get; set; } = [];

        public void AddCondition(IAnimationCondition condition)
            => Conditions.Add(condition);

        public bool CheckConditions()
            => Conditions.All(condition => condition.Evaluate());
    }
}

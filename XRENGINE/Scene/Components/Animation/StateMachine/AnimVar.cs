namespace XREngine.Components
{
    public abstract class AnimVar
    {
        public abstract void SetBool(bool value);
        public abstract void SetFloat(float value);
        public abstract void SetInt(int value);

        public abstract bool GreaterThan(AnimStateCondition condition);
        public abstract bool IsTrue();
        public abstract bool LessThan(AnimStateCondition condition);
        public abstract bool ValueEquals(AnimStateCondition condition);
    }
}

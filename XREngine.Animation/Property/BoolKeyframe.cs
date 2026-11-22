namespace XREngine.Animation
{
    public class BoolKeyframe : Keyframe, IStepKeyframe
    {
        public BoolKeyframe() { }
        public BoolKeyframe(int frameIndex, float FPS, bool value)
            : this(GetSecondForAuthoredFrame(frameIndex, FPS), value)
            => TrySetAuthoredFrameIndex(frameIndex, FPS);
        public BoolKeyframe(float second, bool value) : base()
        {
            Second = second;
            Value = value;
        }
        
        public bool Value { get; set; }
        public override Type ValueType => typeof(bool);

        public new BoolKeyframe? Next => _next as BoolKeyframe;
        public new BoolKeyframe? Prev => _prev as BoolKeyframe;

        public override void ReadFromString(string str)
        {
            int spaceIndex = str.IndexOf(' ');
            Second = float.Parse(str.AsSpan(0, spaceIndex));
            Value = bool.Parse(str.AsSpan(spaceIndex + 1));
        }
        public override string WriteToString()
            => $"{Second} {Value}";
    }
}

namespace XREngine.Animation
{
    public class BoolKeyframe : Keyframe, IStepKeyframe
    {
        public BoolKeyframe() { }
        public BoolKeyframe(int frameIndex, float FPS, bool value)
            : this(frameIndex / FPS, value) { }
        public BoolKeyframe(float second, bool value) : base()
        {
            Second = second;
            Value = value;
        }
        
        public bool Value { get; set; }
        public override Type ValueType => typeof(bool);

        public new BoolKeyframe? Next
        {
            get => _next as BoolKeyframe;
            set => SetField(ref _next, value);
        }
        public new BoolKeyframe? Prev
        {
            get => _prev as BoolKeyframe;
            set => SetField(ref _prev, value);
        }

        public override void ReadFromString(string str)
        {
            int spaceIndex = str.IndexOf(' ');
            Second = float.Parse(str.AsSpan(0, spaceIndex));
            Value = bool.Parse(str.AsSpan(spaceIndex + 1));
        }
        public override string WriteToString()
        {
            return string.Format("{0} {1}", Second, Value);
        }
    }
}

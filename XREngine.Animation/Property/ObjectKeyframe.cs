namespace XREngine.Animation
{
    public class ObjectKeyframe : Keyframe, IStepKeyframe
    {
        public object? Value { get; set; }
        public override Type ValueType => typeof(object);
        public new ObjectKeyframe? Next
        {
            get => _next as ObjectKeyframe;
            set => SetField(ref _next, value);
        }
        public new ObjectKeyframe? Prev
        {
            get => _prev as ObjectKeyframe;
            set => SetField(ref _prev, value);
        }

        public override void ReadFromString(string str)
        {
            int spaceIndex = str.IndexOf(' ');
            Second = float.Parse(str[..spaceIndex]);
            Value = str[(spaceIndex + 1)..];
        }
        public override string WriteToString() => string.Format("{0} {1}", Second, Value);
    }
}

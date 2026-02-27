namespace XREngine.Animation
{
    public class StringKeyframe : Keyframe, IStepKeyframe
    {
        public string? Value { get; set; }
        public override Type ValueType => typeof(string);
        public new StringKeyframe? Next
        {
            get => _next as StringKeyframe;
            set => SetField(ref _next, value);
        }
        public new StringKeyframe? Prev
        {
            get => _prev as StringKeyframe;
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

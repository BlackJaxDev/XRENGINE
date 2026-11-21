namespace XREngine.Animation
{
    public class StringKeyframe : Keyframe, IStepKeyframe
    {
        public string? Value { get; set; }
        public override Type ValueType => typeof(string);
        public new StringKeyframe? Next => _next as StringKeyframe;
        public new StringKeyframe? Prev => _prev as StringKeyframe;

        public override void ReadFromString(string str)
        {
            int spaceIndex = str.IndexOf(' ');
            Second = float.Parse(str[..spaceIndex]);
            Value = str[(spaceIndex + 1)..];
        }
        public override string WriteToString() => $"{Second} {Value}";
    }
}

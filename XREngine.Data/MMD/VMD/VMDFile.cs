using System.Text;

namespace XREngine.Data.MMD
{
    public class VMDFile
    {
        public const float MMDUnitsToMeters = 0.08f;
        public const float MetersToMMDUnits = 12.5f;

        public string? FilePath { get; private set; }
        public VMDHeader? Header { get; private set; }
        public BoneAnimation? BoneAnimation { get; private set; }
        public ShapeKeyAnimation? ShapeKeyAnimation { get; private set; }
        public CameraAnimation? CameraAnimation { get; private set; }
        public LampAnimation? LampAnimation { get; private set; }
        public SelfShadowAnimation? SelfShadowAnimation { get; private set; }
        public PropertyAnimation? PropertyAnimation { get; private set; }
        public uint MaxFrameCount { get; set; }

        public void Load(string path)
        {
            using var reader = new BinaryReader(File.OpenRead(path));

            FilePath = path;
            Header = new VMDHeader();
            BoneAnimation = [];
            ShapeKeyAnimation = [];
            CameraAnimation = [];
            LampAnimation = [];
            SelfShadowAnimation = [];
            PropertyAnimation = [];

            Header.Load(reader);
            try
            {
                BoneAnimation.Load(reader);
                ShapeKeyAnimation.Load(reader);
                CameraAnimation.Load(reader);
                LampAnimation.Load(reader);
                SelfShadowAnimation.Load(reader);
                PropertyAnimation.Load(reader);
            }
            catch (EndOfStreamException) { }

            MaxFrameCount = Math.Max(BoneAnimation.MaxFrameCount, ShapeKeyAnimation.MaxFrameCount);
        }

        public void Save(string? path = null)
        {
            if (Header is null || BoneAnimation is null || ShapeKeyAnimation is null || CameraAnimation is null || LampAnimation is null || SelfShadowAnimation is null || PropertyAnimation is null)
                throw new InvalidOperationException("Cannot save VMD file without loading it first.");
            
            path ??= FilePath ?? string.Empty;
            using var writer = new BinaryWriter(File.OpenWrite(path));
            Header.Save(writer);
            BoneAnimation.Save(writer);
            ShapeKeyAnimation.Save(writer);
            CameraAnimation.Save(writer);
            LampAnimation.Save(writer);
            SelfShadowAnimation.Save(writer);
            PropertyAnimation.Save(writer);
        }
        public override string ToString()
        {
            StringBuilder sb = new();
            if (Header is not null)
                sb.AppendLine(Header.ToString());
            if (BoneAnimation is not null)
                sb.AppendLine(BoneAnimation.ToString());
            if (ShapeKeyAnimation is not null)
                sb.AppendLine(ShapeKeyAnimation.ToString());
            if (CameraAnimation is not null)
                sb.AppendLine(CameraAnimation.ToString());
            if (LampAnimation is not null)
                sb.AppendLine(LampAnimation.ToString());
            if (SelfShadowAnimation is not null)
                sb.AppendLine(SelfShadowAnimation.ToString());
            if (PropertyAnimation is not null)
                sb.AppendLine(PropertyAnimation.ToString());
            return sb.ToString();
        }
    }
}

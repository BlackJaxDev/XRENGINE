//namespace XREngine.Rendering
//{
//    public struct BufferBinding : IEquatable<BufferBinding>
//    {
//        public string? Name;
//        public uint? Location;

//        public BufferBinding(string name)
//            => Name = name;

//        public BufferBinding(uint location)
//            => Location = location;

//        public readonly bool Equals(BufferBinding other)
//            => Name is not null && other.Name is not null
//                ? Name == other.Name
//                : Location == other.Location;

//        //public uint GetLocation(XRMeshRenderer renderer)
//        //{
//        //    if (Location is not null)
//        //        return Location.Value;

//        //    if (Name is null)
//        //        return uint.MaxValue;

//        //    var program = renderer?.Material?.ShaderPipelineProgram;
//        //    if (program is null)
//        //        return uint.MaxValue;

//        //    var location = program.GetUniformLocation(Name);
//        //    if (location < 0)
//        //        return uint.MaxValue;

//        //    Location = (uint)location;
//        //    return Location.Value;
//        //}

//        public static implicit operator BufferBinding(string name) => new(name);
//        public static implicit operator BufferBinding(int location) => new((uint)location);
//        public static implicit operator string(BufferBinding binding) => binding.Name ?? string.Empty;
//        public static implicit operator int(BufferBinding binding) => binding.Location.HasValue ? (int)binding.Location.Value : -1;
//        public static implicit operator uint(BufferBinding binding) => binding.Location ?? uint.MaxValue;
//        public static implicit operator BufferBinding(uint location) => new(location);

//        public override readonly bool Equals(object? obj)
//            => obj is BufferBinding binding && Equals(binding);

//        public override readonly int GetHashCode()
//        {
//            int hash = 17;
//            hash = hash * 23 + (Name?.GetHashCode() ?? 0);
//            hash = hash * 23 + (Location?.GetHashCode() ?? 0);
//            return hash;
//        }

//        public static bool operator ==(BufferBinding left, BufferBinding right)
//            => left.Equals(right);

//        public static bool operator !=(BufferBinding left, BufferBinding right)
//            => !(left == right);
//    }
//}
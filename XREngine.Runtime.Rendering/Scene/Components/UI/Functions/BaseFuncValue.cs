//using XREngine.Core.Maths.Transforms;
//using XREngine.Rendering.Models.Materials;
//using XREngine.Data.Colors;

//namespace XREngine.Rendering.UI.Functions
//{
//    public interface IBaseFuncValue : IBaseFuncArg
//    {
//        int CurrentArgumentType { get; set; }
//        //int[] AllowedArgumentTypes { get; }
//        //HashSet<IBaseFuncValue> SyncedArguments { get; }
//        bool HasConnection { get; }
//    }
//    public abstract class BaseFuncValue : BaseFuncArg, IBaseFuncValue
//    {
//        public static ColorF4 NoTypeColor { get; } = new ColorF4(0.4f, 1.0f);
//        public static ColorF4 RegularColor { get; set; } = new ColorF4(0.4f, 0.4f, 0.4f, 1.0f);
//        public static ColorF4 HighlightedColor { get; set; } = new ColorF4(0.4f, 0.6f, 0.6f, 1.0f);
//        public static ColorF4 ConnectableColor { get; set; } = new ColorF4(0.8f, 0.2f, 0.2f, 1.0f);
        
//        public BaseFuncValue(string name, IFunction parent, ColorF4 color) : base(name, parent, color) { }
        
//        public int CurrentArgumentType
//        {
//            get => _currentArgType;
//            set
//            {
//                _currentArgType = value;
//                Material.Parameter<ShaderVector4>(0).Value = GetTypeColor();
//                OnCurrentArgTypeChanged();
//            }
//        }

//        public abstract bool HasConnection { get; }
        
//        protected virtual void OnCurrentArgTypeChanged() { }
        
//        private int _currentArgType = -1;
//        public override string ToString() => Name;

//        public virtual Vector4 GetTypeColor() => NoTypeColor;
//    }
//    public abstract class BaseFuncValue<TOutput> : BaseFuncValue where TOutput : IBaseFuncValue
//    {
//        public BaseFuncValue(string name, IFunction parent) : base(name, parent, NoTypeColor) { }
        
//        public abstract bool CanConnectTo(TOutput other);
//    }
//    public enum ArgumentSyncType
//    {
//        SyncByName,
//        SyncByIndex
//    }
//}

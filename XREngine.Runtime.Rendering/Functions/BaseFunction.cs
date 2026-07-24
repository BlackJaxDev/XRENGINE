//using System.Collections.ObjectModel;
//using System.ComponentModel;
//using System.Numerics;
//using System.Reflection;
//using XREngine.Data.Colors;
//using XREngine.Rendering.UI;
//using static System.Net.Mime.MediaTypeNames;

//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    public abstract class BaseFunction : UIMaterialComponent, IShaderVarOwner, IFunction
//    {
//        public static Vector4 RegularColor { get; set; } = new Vector4(0.1f, 0.1f, 0.1f, 1.0f);
//        public static Vector4 SelectedColor { get; set; } = new Vector4(0.1f, 0.2f, 0.25f, 1.0f);
//        public static Vector4 HighlightedColor { get; set; } = new Vector4(0.1f, 0.3f, 0.4f, 1.0f);

//        public FunctionDefinition? Definition => this.GetType()?.GetCustomAttribute<FunctionDefinition>();

//        public ReadOnlyCollection<string> Keywords => Definition?.Keywords.AsReadOnly();
//        public string? FunctionName => Definition?.Name;
//        public string? Description => Definition?.Description;
//        public string? Category => Definition?.Category;

//        protected UITextRasterComponent _headerText;
//        protected UIString2D _headerString;
//        protected List<UITextRasterComponent> _inputParamTexts = new List<UITextRasterComponent>();
//        protected List<UITextRasterComponent> _outputParamTexts = new List<UITextRasterComponent>();

//        protected const int HeaderPadding = 2;
//        protected Font _paramFont = new Font("Segoe UI", 9.0f, FontStyle.Regular);
//        protected Font _headerFont = new Font("Segoe UI", 11.0f, FontStyle.Bold);

//        public BaseFunction() : base(MakeFunctionMaterial())
//        {
//            _headerText = new UITextRasterComponent
//            {
//                Name = FunctionName + " [Header Text]",
//                VerticalAlignment = EVerticalAlign.Top,
//                Height = TextRenderer.MeasureText(FunctionName, _headerFont).Height + HeaderPadding * 2,
//            };
//            _headerString = new UIString2D()
//            {
//                Text = FunctionName,
//                Font = _headerFont,
//                Format = TextFormatFlags.NoClipping | TextFormatFlags.SingleLine
//            };
//            _headerText.TextDrawer.Text.Add(_headerString);
//            ChildSockets.Add(_headerText);
//        }
//        private static XRMaterial MakeFunctionMaterial()
//        {
//            return XRMaterial.CreateUnlitColorMaterialForward(new ColorF4(0.1f, 1.0f));
//        }

//        public abstract void GetMinMax(out Vector2 min, out Vector2 max, bool searchForward = true, bool searchBackward = true);

//        protected void AddParam(BaseFuncArg arg)
//        {
//            ChildSockets.Add(arg);

//            UITextRasterComponent text = new UITextRasterComponent { Name = arg.Name + " Text", };
//            text.TextDrawer.Text.Add(new UIString2D(arg.Name, _paramFont, Color.White, TextFormatFlags.NoClipping | TextFormatFlags.SingleLine));
//            ChildSockets.Add(text);

//            if (arg is IFuncExecInput || arg is IFuncValueInput)
//            {
//                _inputParamTexts.Add(text);
//            }
//            else if (arg is IFuncExecOutput || arg is IFuncValueOutput)
//            {
//                _outputParamTexts.Add(text);
//            }
//        }

//        public override string ToString()
//            => FunctionName;

//        public static List<Type> Find<T>(string keywords) where T : IFunction
//        {
//            string[] keyArray = keywords.Split(' ');
//            Dictionary<int, List<Type>> types = new Dictionary<int, List<Type>>();
//            IEnumerable<Type> functions = Assembly.GetExecutingAssembly().GetTypes().
//                Where(x => x is T && x.IsDefined(typeof(FunctionDefinition)));
//            foreach (Type func in functions)
//            {
//                if (func.IsAbstract)
//                    continue;

//                FunctionDefinition def = func.GetCustomAttribute<FunctionDefinition>();

//                int count = 0;
//                foreach (string keyword in def.Keywords)
//                {
//                    foreach (string typedKeyword in keyArray)
//                        if (keyword.Contains(typedKeyword))
//                        {
//                            ++count;
//                            break;
//                        }
//                }
//                if (count > 0)
//                {
//                    if (types.ContainsKey(count))
//                        types[count].Add(func);
//                    else
//                        types.Add(count, new List<Type>() { func });
//                }
//            }
//            int maxVal = TMath.Max(types.Keys.ToArray());
//            return types[maxVal];
//        }
//    }
//}

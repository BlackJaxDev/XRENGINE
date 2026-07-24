//namespace XREngine.Rendering.Models.Materials.Functions
//{
//    public abstract class BaseFuncArg : UIMaterialComponent
//    {
//        public const int ConnectionBoxDims = 10;
//        public const int ConnectionBoxMargin = 5;

//        public BaseFuncArg(string name, ColorF4 color) : base(MakeArgMaterial(color))
//        {
//            _name = name;
//            Size.Value = new Vector2(ConnectionBoxDims);
//        }
//        public BaseFuncArg(string name, IFunction parent, ColorF4 color) : base(MakeArgMaterial(color))
//        {
//            _name = name;
//            _parent = parent;
//            Size.Value = new Vector2(ConnectionBoxDims);
//        }

//        public BaseFunction OwningFunction { get; internal set; }
//        public abstract bool IsOutput { get; }
//        public override string ToString() => Name;
//        public int ArgumentIndex { get; internal set; }

//        public abstract bool CanConnectTo(BaseFuncArg other);
//        public abstract bool ConnectTo(BaseFuncArg other);

//        private static XRMaterial MakeArgMaterial(ColorF4 color)
//        {
//            return XRMaterial.CreateUnlitColorMaterialForward(color);
//        }

//        /// <summary>
//        /// Returns an interpolated point from this argument to the given point.
//        /// Used for rendering the material editor graph.
//        /// </summary>
//        /// <param name="t">Time from this argument's point to the given point, 0.0f to 1.0f continuous.</param>
//        /// <returns>The interpolated point.</returns>
//        //public Vector2 BezierToPointAsPoint(Vector2 point, float time)
//        //{
//        //    Vector2 p0 = ScreenTranslation;

//        //    Vector2 p1 = p0;
//        //    p1.X += IsOutput ? 10.0f : -10.0f;

//        //    Vector2 p3 = point;

//        //    Vector2 p2 = p3;
//        //    p2.X += ScreenTranslation.X < point.X ? -10.0f : 10.0f;

//        //    return Interp.CubicBezier(p0, p1, p2, p3, time);
//        //}
//        //public Vector2[] BezierToPointAsPoints(Vector2 point, int count)
//        //{
//        //    Vector2 p0 = ScreenTranslation;

//        //    Vector2 p1 = p0;
//        //    p1.X += IsOutput ? 10.0f : -10.0f;

//        //    Vector2 p3 = point;

//        //    Vector2 p2 = p3;
//        //    p2.X += ScreenTranslation.X < point.X ? -10.0f : 10.0f;

//        //    return Interp.GetBezierPoints(p0, p1, p2, p3, count);
//        //}
//    }
//}

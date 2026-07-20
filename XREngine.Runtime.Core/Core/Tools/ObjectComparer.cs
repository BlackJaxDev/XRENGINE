namespace XREngine.Core.Tools
{
    //public class ObjectComparer
    //{
    //    public class Result
    //    {
    //        public bool Equal => Differences.Count == 0;
    //        public SortedDictionary<string, Difference> Differences { get; } = new SortedDictionary<string, Difference>();
    //        public class Difference
    //        {
    //            public object LeftValue { get; private set; }
    //            public object RightValue { get; private set; }
    //            public Difference(object left, object right)
    //            {
    //                LeftValue = left;
    //                RightValue = right;
    //            }
    //        }
    //        public override string ToString()
    //        {
    //            string s = "";
    //            foreach (var d in Differences)
    //                s += d.Key + "\n" + d.Value.ToString();
    //            return s;
    //        }
    //    }
    //    public Result CompareEquality(object left, object right)
    //    {
    //        Result r = new Result();

    //        Type type = left.GetType();
    //        if (type != right.GetType())
    //        {
    //            r.Differences.Add("/", new Result.Difference(left, right));
    //            return r;
    //        }

    //        CompareEquality(r, left, right);

    //        MemberInfo[] members = type.GetMembers(
    //            BindingFlags.FlattenHierarchy | 
    //            BindingFlags.Public | 
    //            BindingFlags.NonPublic | 
    //            BindingFlags.Instance);

    //        //Type memberType;
    //        object leftValue, rightValue;
    //        foreach (MemberInfo member in members)
    //        {
    //            if (member is FieldInfo field)
    //            {
    //                leftValue = field.GetValue(left);
    //                rightValue = field.GetValue(right);
    //            }
    //            else if (member is PropertyInfo prop)
    //            {
    //                leftValue = prop.GetValue(left);
    //                rightValue = prop.GetValue(right);
    //            }
    //            else
    //                continue;


    //        }

    //        return r;
    //    }
    //    private void CompareEquality(Result r, object left, object right)
    //    {

    //    }
    //}
}

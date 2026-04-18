using System.ComponentModel;
using System.Reflection;

namespace XREngine.Extensions
{
    public static partial class EnumExtensions
    {
        public static string GetDescription(this Enum e)
        {
            Type t = e.GetType();
            MemberInfo[] memberInfo = t.GetMember(e.ToString());
            if ((memberInfo != null && memberInfo.Length > 0))
            {
                var attribs = memberInfo[0].GetCustomAttributes(typeof(DescriptionAttribute), false);
                if ((attribs != null && attribs.Length > 0))
                    return ((DescriptionAttribute)attribs[0]).Description;
            }
            return e.ToString();
        }

        //Converts one enum to the other using names.
        public static Enum ConvertByName(this Enum e, Type otherEnum)
            => (Enum)Enum.Parse(otherEnum, e.ToString());
    }
}

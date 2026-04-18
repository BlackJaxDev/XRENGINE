using System.Reflection;

namespace XREngine.Extensions
{
    public static class MethodInfoExtensions
    {
        public static bool IsAttributeDefined(this MemberInfo info, Type attributeType)
            => info.IsDefined(attributeType);
        public static bool IsAttributeDefined(this Type type, Type attributeType)
            => type.IsDefined(attributeType);
        public static string GetFriendlyName(this MethodBase method, bool nameOnly = false, string openBracket = "<", string closeBracket = ">")
        {
            if (method is null)
                return "null";
            
            string friendlyName = "";

            if (!nameOnly)
            {
                if (method.IsPublic)
                    friendlyName += "public ";
                if (method.IsPrivate)
                    friendlyName += "private ";
                if (method.IsFamily)
                    friendlyName += "protected ";
                if (method.IsAssembly)
                    friendlyName += "internal ";
                if (method.IsFinal)
                    friendlyName += "sealed ";
                if (method.IsStatic)
                    friendlyName += "static ";

                MethodInfo? realMethod = method as MethodInfo;

                //if (method.IsHideBySig && method.DeclaringType.IsAssignableFrom(method.ReflectedType))
                //    friendlyName += "new ";
                if (method.IsVirtual)
                    friendlyName += "virtual ";
                if (realMethod != null && realMethod.GetBaseDefinition() != realMethod)
                    friendlyName += "override ";
                if (method.IsAbstract)
                    friendlyName += "abstract ";

                if (realMethod != null)
                    friendlyName += realMethod.ReturnType.GetFriendlyName() + " ";
            }

            if (!method.IsSpecialName)
                friendlyName += method.Name;
            else 
            {
                string name = method.ReflectedType?.GetFriendlyName() ?? string.Empty;
                int index = name.IndexOf('<');
                if (index > 0)
                    name = name.Substring(0, index);
                if (method.IsConstructor)
                    friendlyName += name;
                else
                    friendlyName += "~" + name;
            }

            bool first = true;
            if (method.IsGenericMethod)
            {
                friendlyName += openBracket;
                Type[] genericArgs = method.GetGenericArguments();
                foreach (Type generic in genericArgs)
                {
                    if (first)
                        first = false;
                    else
                        friendlyName += ", ";
                    friendlyName += generic.GetFriendlyName(openBracket, closeBracket);
                }
                friendlyName += closeBracket;
            }
            friendlyName += "(";
            ParameterInfo[] parameters = method.GetParameters();
            first = true;
            foreach (var p in parameters)
            {
                if (first)
                    first = false;
                else
                    friendlyName += ", ";

                if (p.IsIn)
                    friendlyName += "in ";
                if (p.IsOut)
                    friendlyName += "out ";
                if (p.ParameterType.IsByRef)
                    friendlyName += "ref ";

                string typeName = p.ParameterType.GetFriendlyName(openBracket, closeBracket);
                friendlyName += typeName + " " + p.Name;
                if (p.HasDefaultValue)
                {
                    friendlyName += " = ";
                    if (p.DefaultValue is null)
                    {
                        friendlyName += "null";
                    }
                    else
                    {
                        if (p.ParameterType == typeof(string))
                            friendlyName += "\"" + p.DefaultValue.ToString() + "\"";
                        else if(p.ParameterType == typeof(char))
                            friendlyName += "\'" + p.DefaultValue.ToString() + "\'";
                        else
                            friendlyName += p.DefaultValue.ToString();
                    }
                }
            }
            friendlyName += ")";
            return friendlyName;
        }
    }
}

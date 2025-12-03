using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Extensions
{
    public enum EGenericVarianceFlag
    {
        None,
        CovariantOut,
        ContravariantIn,
    }
    public enum ETypeConstraintFlag
    {
        None,
        Struct,             //struct
        Class,              //class
        NewClass,           //class, new()
        NewStructOrClass,   //new()
    }
    public static partial class TypeExtension
    {
        private static readonly Dictionary<Type, string> DefaultDictionary = new()
        {
            { typeof(void),     "void"      },
            { typeof(char),     "char"      },
            { typeof(bool),     "bool"      },
            { typeof(byte),     "byte"      },
            { typeof(sbyte),    "sbyte"     },
            { typeof(short),    "short"     },
            { typeof(ushort),   "ushort"    },
            { typeof(int),      "int"       },
            { typeof(uint),     "uint"      },
            { typeof(long),     "long"      },
            { typeof(ulong),    "ulong"     },
            { typeof(float),    "float"     },
            { typeof(double),   "double"    },
            { typeof(decimal),  "decimal"   },
            { typeof(string),   "string"    },
            { typeof(object),   "object"    },
        };

        public static bool IsAssignableFrom(this Type type, Type other) => other.IsAssignableTo(type);
        public static bool IsAssignableTo(this Type type, Type other) => other.IsAssignableFrom(type);

        public static object? PtrToStructure(this Type type, IntPtr address)
            => Marshal.PtrToStructure(address, type);

        public static object? CreateInstance([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] this Type type)
            => Activator.CreateInstance(type);
        public static object? CreateInstance([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] this Type type, params object[] args)
            => Activator.CreateInstance(type, args);
        public static T? CreateInstance<T>([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] this Type type)
        {
            var obj = Activator.CreateInstance(type);
            return obj is null ? default : (T)obj;
        }
        public static T? CreateInstance<T>([DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] this Type type, params object[] args)
        {
            var obj = Activator.CreateInstance(type, args);
            return obj is null ? default : (T)obj;
        }

        public static bool HasCustomAttribute<T>(this Type type) where T : Attribute
            => type.GetCustomAttribute<T>() != null;

        public static object ParseEnum(this Type type, string value)
            => Enum.Parse(type, value);
        public static Type? GetUnderlyingNullableType(this Type type)
            => Nullable.GetUnderlyingType(type);

        /// <summary>
        /// Returns the type as a string in the form that it is written in code.
        /// </summary>
        public static string GetFriendlyName(this Type type, string openBracket = "<", string closeBracket = ">")
            => type.GetFriendlyName(DefaultDictionary, openBracket, closeBracket);
        /// <summary>
        /// Returns the type as a string in the form that it is written in code.
        /// </summary>
        public static string GetFriendlyName(this Type type, Dictionary<Type, string> translations, string openBracket = "<", string closeBracket = ">")
        {
            if (type is null)
                return "null";
            else if (translations.TryGetValue(type, out string? value))
                return value;
            else if (type.IsArray)
                return type.GetElementType()?.GetFriendlyName(translations, openBracket, closeBracket) + "[]";
            else if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
                return GetFriendlyName(type.GetGenericArguments()[0], translations, openBracket, closeBracket) + "?";
            else if (type.IsGenericType)
                return type.Name.Split('`')[0] + openBracket + string.Join(", ", type.GetGenericArguments().Select(x => GetFriendlyName(x, translations, openBracket, closeBracket))) + closeBracket;
            else
                return type.Name;
        }

        /// <summary>
        /// Returns the default value for the given type. Similar to default(T).
        /// </summary>
        public static object? GetDefaultValue(this Type t)
            => t is null ? null : (t.IsValueType && Nullable.GetUnderlyingType(t) is null) ? t.CreateInstance() : null;

        public static void GetGenericParameterConstraints(this Type genericTypeParam, out EGenericVarianceFlag gvf, out ETypeConstraintFlag tcf)
        {
            GenericParameterAttributes gpa = genericTypeParam.GenericParameterAttributes;
            GenericParameterAttributes variance = gpa & GenericParameterAttributes.VarianceMask;
            GenericParameterAttributes constraints = gpa & GenericParameterAttributes.SpecialConstraintMask;

            gvf = EGenericVarianceFlag.None;
            tcf = ETypeConstraintFlag.None;

            if (variance != GenericParameterAttributes.None)
            {
                if ((variance & GenericParameterAttributes.Covariant) != 0)
                    gvf = EGenericVarianceFlag.CovariantOut;
                else
                    gvf = EGenericVarianceFlag.ContravariantIn;
            }

            if (constraints != GenericParameterAttributes.None)
            {
                if ((constraints & GenericParameterAttributes.NotNullableValueTypeConstraint) != 0)
                    tcf = ETypeConstraintFlag.Struct;
                else
                {
                    if ((constraints & GenericParameterAttributes.DefaultConstructorConstraint) != 0)
                        tcf = ETypeConstraintFlag.NewStructOrClass;
                    if ((constraints & GenericParameterAttributes.ReferenceTypeConstraint) != 0)
                    {
                        if (tcf == ETypeConstraintFlag.NewStructOrClass)
                            tcf = ETypeConstraintFlag.NewClass;
                        else
                            tcf = ETypeConstraintFlag.Class;
                    }
                }
            }
        }
        public static bool FitsConstraints(this Type type, EGenericVarianceFlag gvf, ETypeConstraintFlag tcf)
        {
            if (gvf != EGenericVarianceFlag.None)
                throw new Exception();

            return tcf switch
            {
                ETypeConstraintFlag.Class => type.IsClass,
                ETypeConstraintFlag.NewClass => type.IsClass && type.GetConstructor(Array.Empty<Type>()) != null,
                ETypeConstraintFlag.NewStructOrClass => type.GetConstructor(Array.Empty<Type>()) != null,
                ETypeConstraintFlag.Struct => type.IsValueType,
                _ => true,
            };
        }

        public static T? GetCustomAttributeExt<T>(this Type type) where T : Attribute
        {
            T[] types = type.GetCustomAttributesExt<T>();
            if (types.Length > 0)
                return types[0];
            return null;
        }
        public static T[] GetCustomAttributesExt<T>(this Type type) where T : Attribute
        {
            List<T> list = new();
            Type? t = type;
            while (t != null)
            {
                list.AddRange(t.GetCustomAttributes<T>());
                t = t.BaseType;
            }
            return list.ToArray();
        }
        public static MemberInfo[] GetMembersExt(this Type type, BindingFlags bindingAttr)
        {
            bindingAttr &= ~BindingFlags.FlattenHierarchy;
            bindingAttr |= BindingFlags.DeclaredOnly;
            List<MemberInfo> members = new();
            Type? t = type;
            while (t != null)
            {
                members.AddRange(t.GetMembers(bindingAttr));
                t = t.BaseType;
            }
            return members.ToArray();
        }
        public static bool AnyBaseTypeMatches(this Type type, Predicate<Type> match)
        {
            Type? t = type;
            while (t is not null && t.BaseType != t)
            {
                if (match(t))
                    return true;
                
                t = t.BaseType;
            }
            return false;
        }
        public static bool AnyBaseTypeMatches(this Type type, Predicate<Type> match, out Type? matchingType)
        {
            Type? t = type;
            while (t is not null && t.BaseType != t)
            {
                if (match(t))
                {
                    matchingType = t;
                    return true;
                }
                t = t.BaseType;
            }
            matchingType = null;
            return false;
        }
    }
}

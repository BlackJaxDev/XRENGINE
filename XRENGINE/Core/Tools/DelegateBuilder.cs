using System.Linq.Expressions;
using System.Reflection;

namespace XREngine.Core.Tools
{
    public class DelegateBuilder
    {
        public static T BuildDelegate<T>(MethodInfo method, params object[] missingParamValues)
        {
            if (missingParamValues.Length == 0 && TryCreateDirectDelegate(method, out T? directDelegate))
                return directDelegate!;

            var queueMissingParams = new Queue<object>(missingParamValues);

            var dgtMi = typeof(T).GetMethod("Invoke") ?? throw new InvalidOperationException($"Type {typeof(T)} does not have an Invoke method");
            var dgtParams = dgtMi.GetParameters();

            var paramsOfDelegate = dgtParams
                .Select(tp => Expression.Parameter(tp.ParameterType, tp.Name))
                .ToArray();

            var methodParams = method.GetParameters();

            if (method.IsStatic)
            {
                var paramsToPass = methodParams
                    .Select((p, i) => CreateParam(paramsOfDelegate, i, p, queueMissingParams))
                    .ToArray();

                var expr = Expression.Lambda<T>(
                    Expression.Call(method, paramsToPass),
                    paramsOfDelegate);

                return CompileDelegate(expr);
            }
            else
            {
                var paramThis = Expression.Convert(paramsOfDelegate[0], method.DeclaringType ?? throw new InvalidOperationException("Method has no declaring type"));

                var paramsToPass = methodParams
                    .Select((p, i) => CreateParam(paramsOfDelegate, i + 1, p, queueMissingParams))
                    .ToArray();

                var expr = Expression.Lambda<T>(
                    Expression.Call(paramThis, method, paramsToPass),
                    paramsOfDelegate);

                return CompileDelegate(expr);
            }
        }

        private static bool TryCreateDirectDelegate<T>(MethodInfo method, out T? result)
        {
            try
            {
                result = (T)(object)method.CreateDelegate(typeof(T));
                return true;
            }
            catch (ArgumentException)
            {
                result = default;
                return false;
            }
        }

        private static T CompileDelegate<T>(Expression<T> expression)
            => XRRuntimeEnvironment.IsAotRuntimeBuild || !XRRuntimeEnvironment.SupportsDynamicCode
                ? expression.Compile(preferInterpretation: true)
                : expression.Compile();

        private static Expression CreateParam(ParameterExpression[] paramsOfDelegate, int i, ParameterInfo callParamType, Queue<object> queueMissingParams)
        {
            if (i < paramsOfDelegate.Length)
                return Expression.Convert(paramsOfDelegate[i], callParamType.ParameterType);

            if (queueMissingParams.Count > 0)
                return Expression.Constant(queueMissingParams.Dequeue());

            if (callParamType.ParameterType.IsValueType)
                return Expression.Constant(Activator.CreateInstance(callParamType.ParameterType));

            return Expression.Constant(null);
        }
    }
}

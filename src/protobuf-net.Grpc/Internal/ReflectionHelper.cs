using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Threading.Tasks;

namespace ProtoBuf.Grpc.Internal
{
    internal static class ReflectionHelper
    {
        public static MethodInfo GetContinueWithForTask(Type inputType, Type outputType)
        {
            var genericContinueWith = typeof(Task<>).MakeGenericType(inputType)
                .GetMethods().Where(IsDesiredContinueWithMethod).Single();
            return genericContinueWith.MakeGenericMethod(outputType);
        }

        public static bool IsDesiredContinueWithMethod(MethodInfo mi)
        {
            if (mi.Name != nameof(Task.ContinueWith))
                return false;
            var parameters = mi.GetParameters();
            if (parameters.Length != 1)
                return false;
            var funcParamType = parameters[0].ParameterType;
            if (!funcParamType.IsGenericType || funcParamType.GetGenericTypeDefinition() != typeof(Func<,>))
                return false;
            var taskArgument = funcParamType.GetGenericArguments()[0];
            return taskArgument.IsGenericType && taskArgument.GetGenericTypeDefinition() == typeof(Task<>);
        }

        internal static Type GetTupledType(params Type[] types)
        {
            return types.Length switch
            {
                0 => typeof(Tuple),
                1 => typeof(Tuple<>).MakeGenericType(types),
                2 => typeof(Tuple<,>).MakeGenericType(types),
                3 => typeof(Tuple<,,>).MakeGenericType(types),
                4 => typeof(Tuple<,,,>).MakeGenericType(types),
                5 => typeof(Tuple<,,,,>).MakeGenericType(types),
                6 => typeof(Tuple<,,,,,>).MakeGenericType(types),
                7 => typeof(Tuple<,,,,,,>).MakeGenericType(types),
                _ => typeof(Tuple<,,,,,,,>).MakeGenericType(types.Take(7)
                    .Concat(new[] { GetTupledType(types.Skip(7).ToArray()) }).ToArray())
            };
        }

        internal static Type EmitTupleConstructor(ILGenerator il, Type[] types)
        {
            Type tupleType;
            if (types.Length > 7)
            {
                var rest = EmitTupleConstructor(il, types.Skip(7).ToArray());
                tupleType = typeof(Tuple<,,,,,,,>).MakeGenericType(types.Take(7).Concat(new[] { rest }).ToArray());
            }
            else
            {
                tupleType = types.Length switch
                {
                    1 => typeof(Tuple<>).MakeGenericType(types),
                    2 => typeof(Tuple<,>).MakeGenericType(types),
                    3 => typeof(Tuple<,,>).MakeGenericType(types),
                    4 => typeof(Tuple<,,,>).MakeGenericType(types),
                    5 => typeof(Tuple<,,,,>).MakeGenericType(types),
                    6 => typeof(Tuple<,,,,,>).MakeGenericType(types),
                    7 => typeof(Tuple<,,,,,,>).MakeGenericType(types),
                    _ => throw new ArgumentException() // not actually possible
                };
            }
            var constructor = tupleType.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance).Single();
            il.Emit(OpCodes.Newobj, constructor);
            return tupleType;
        }

        internal static Expression TupleInDeconstruct(Expression instance, MethodInfo method, params Expression[] args)
        {
            var parameters = method.GetParameters();
            int nbFinalParams = parameters[parameters.Length - 1].ParameterType == typeof(CallContext) ||
                parameters[parameters.Length - 1].ParameterType == typeof(CancellationToken) ? 1 : 0;
            Expression callExpression;
            if (parameters.Length == nbFinalParams + 1)
            {
                if (parameters[0].ParameterType.IsValueType)
                {
                    // then the parameter should be wrapped in a ValueTypeWrapper
                    var valueField = args[0].Type.GetField("Value")!;
                    var unwrappedArg = Expression.Field(args[0], valueField);
                    var actualArgs = new[] { unwrappedArg }.Concat(args.Skip(1)).ToArray();

                    callExpression = Expression.Call(instance, method, actualArgs);
                }
                else
                {
                    callExpression = Expression.Call(instance, method, args);
                }
            }
            else
            {
                // assume args[0] is a Tuple with appropriate elements
                var actualArgs = Enumerable.Range(0, parameters.Length - args.Length + 1)
                    .Select(i => GetNthItem(args[0], i))
                    .Concat(args.Skip(1))
                    .ToArray();

                callExpression = Expression.Call(instance, method, actualArgs);
            }

            return ValueTypeWrapperOutConstruct(callExpression);

            Expression GetNthItem(Expression tuple, int n) => n < 7
                ? Expression.Property(tuple, tuple.Type.GetProperty($"Item{n + 1}") ??
                                             throw new InvalidOperationException($"No property Item{n + 1} found on {tuple.Type.FullName}"))
                : GetNthItem(Expression.Property(tuple, tuple.Type.GetProperty("Rest") ??
                                                        throw new InvalidOperationException($"No property Rest found on {tuple.Type.FullName}")), n - 7);
        }

        internal static Expression ValueTypeWrapperOutConstruct(Expression callResult)
        {
            if (callResult.Type == typeof(void) || callResult.Type == typeof(Task) || callResult.Type == typeof(ValueTask) ||
                (callResult.Type.IsGenericType && typeof(IAsyncEnumerable<>).IsAssignableFrom(callResult.Type.GetGenericTypeDefinition())))
                return callResult;

            if (callResult.Type.IsGenericType &&
                (callResult.Type.GetGenericTypeDefinition() == typeof(Task<>) ||
                 callResult.Type.GetGenericTypeDefinition() == typeof(ValueTask<>)))
            {
                var underlyingType = callResult.Type.GetGenericArguments().First();
                if (!underlyingType.IsValueType)
                    return callResult;

                var isValueTask = callResult.Type.GetGenericTypeDefinition() == typeof(ValueTask<>);
                Type valueTypeWrapperType = typeof(ValueTypeWrapper<>).MakeGenericType(underlyingType);
                var taskType = typeof(Task<>).MakeGenericType(underlyingType);
                var taskParam = Expression.Parameter(taskType, "t");
                var valueTypeWrapperConstructor = valueTypeWrapperType.GetConstructor(new[] { underlyingType })!;
                var resultProperty = taskType.GetProperty("Result")!;
                var lambdaBody = Expression.New(valueTypeWrapperConstructor, Expression.Property(taskParam, resultProperty));
                var continuationLambda = Expression.Lambda(lambdaBody, taskParam);
                var task = isValueTask ? Expression.Call(callResult, callResult.Type.GetMethod(nameof(ValueTask.AsTask))!) : callResult;
                var continueWithMethod = ReflectionHelper.GetContinueWithForTask(underlyingType, valueTypeWrapperType);
                var continuation = Expression.Call(task, continueWithMethod, continuationLambda);

                if (!isValueTask)
                    return continuation;

                var targetValueTaskType = typeof(ValueTask<>).MakeGenericType(valueTypeWrapperType);
                var targetValueTaskConstructor = targetValueTaskType.GetConstructor(new[] { typeof(Task<>).MakeGenericType(valueTypeWrapperType) })!;
                return Expression.New(targetValueTaskConstructor, continuation);
            }

            if (!callResult.Type.IsValueType)
                return callResult;

            var valueTypeWrapperReturnTypeConstructor = typeof(ValueTypeWrapper<>).MakeGenericType(callResult.Type).GetConstructor(new[] { callResult.Type })!;
            return Expression.New(valueTypeWrapperReturnTypeConstructor, callResult);
        }
    }
}

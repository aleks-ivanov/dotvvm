﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Dynamic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.InteropServices.ComTypes;
using System.Threading.Tasks;
using DotVVM.Framework.Binding;
using DotVVM.Framework.Controls;
using DotVVM.Framework.Runtime;
using DotVVM.Framework.Utils;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CSharp.RuntimeBinder;

namespace DotVVM.Framework.Compilation.Binding
{
    public static class ExpressionHelper
    {
        private static readonly Type ParamArrayAttributeType = typeof(ParamArrayAttribute);

        public static Expression GetMember(Expression target, string name, Type[] typeArguments = null, bool throwExceptions = true, bool onlyMemberTypes = false)
        {
            if (target is MethodGroupExpression)
                throw new Exception("Can not access member on method group.");

            var type = target.Type;
            if (type == typeof(UnknownTypeSentinel)) if (throwExceptions) throw new Exception($"Type of '{target}' could not be resolved."); else return null;

            var isStatic = target is StaticClassIdentifierExpression;

            var isGeneric = typeArguments != null && typeArguments.Length != 0;
            var genericName = isGeneric ? $"{name}`{typeArguments.Length}" : name;

            if (!isGeneric && !onlyMemberTypes && typeof(DotvvmBindableObject).IsAssignableFrom(target.Type) &&
                GetDotvvmPropertyMember(target, name) is Expression result) return result;

            var members = type.GetAllMembers(BindingFlags.Public | (isStatic ? BindingFlags.Static : BindingFlags.Instance))
                .Where(m => ((isGeneric && m is TypeInfo) ? genericName : name) == m.Name)
                .ToArray();

            if (members.Length == 0)
            {
                // We did not find any match in regular methods => try extension methods
                var extensions = type.GetAllExtensions()
                    .Where(m => ((isGeneric && m is TypeInfo) ? genericName : name) == m.Name)
                    .ToArray();
                members = extensions;

                if (members.Length == 0 && throwExceptions)
                    throw new Exception($"Could not find { (isStatic ? "static" : "instance") } member { name } on type { type.FullName }.");
                else if (members.Length == 0 && !throwExceptions)
                    return null;
            }
            if (members.Length == 1)
            {
                if (!(members[0] is TypeInfo) && onlyMemberTypes) { throw new Exception("Only type names are supported."); }

                var instance = isStatic ? null : target;
                if (members[0] is PropertyInfo)
                {
                    var property = members[0] as PropertyInfo;
                    return Expression.Property(instance, property);
                }
                else if (members[0] is FieldInfo)
                {
                    var field = members[0] as FieldInfo;
                    return Expression.Field(instance, field);
                }
                else if (members[0] is TypeInfo)
                {
                    var nonGenericType = (TypeInfo)members[0];
                    return isGeneric
                        ? new StaticClassIdentifierExpression(nonGenericType.MakeGenericType(typeArguments))
                        : new StaticClassIdentifierExpression(nonGenericType.UnderlyingSystemType);
                }
            }
            return new MethodGroupExpression() { MethodName = name, Target = target, TypeArgs = typeArguments };
        }

        static Expression GetDotvvmPropertyMember(Expression target, string name)
        {
            var property = DotvvmProperty.ResolveProperty(target.Type, name);
            if (property == null) return null;

            var field = property.DeclaringType.GetField(property.Name + "Property", BindingFlags.Static | BindingFlags.Public);
            if (field == null) return null;

            return Expression.Convert(
                Expression.Call(target, "GetValue", Type.EmptyTypes,
                    Expression.Field(null, field),
                    Expression.Constant(true)
                ),
                property.PropertyType
            );
        }

        /// <summary>
        /// Creates an expression that updates the member inside <paramref name="node"/> with a
        /// new <paramref name="value"/>.
        /// </summary>
        /// <remarks>
        /// Should <paramref name="node"/> contain a call to the
        /// <see cref="DotvvmBindableObject.GetValue(DotvvmProperty, bool)"/> method, it will be
        /// replaced with a <see cref="DotvvmBindableObject.SetValue(DotvvmProperty, object)"/>
        /// call.
        /// </remarks>
        public static Expression UpdateMember(Expression node, Expression value)
        {
            if ((node.NodeType == ExpressionType.MemberAccess
                && node is MemberExpression member
                && member.Member is PropertyInfo property
                && property.CanWrite)
                || node.NodeType == ExpressionType.Parameter
                || node.NodeType == ExpressionType.Index)
            {
                return Expression.Assign(node, Expression.Convert(value, node.Type));
            }

            var current = node;
            while (current.NodeType == ExpressionType.Convert
                && current is UnaryExpression unary)
            {
                current = unary.Operand;
            }

            if (current.NodeType == ExpressionType.Call
                && current is MethodCallExpression call
                && call.Method.DeclaringType == typeof(DotvvmBindableObject)
                && call.Method.Name == nameof(DotvvmBindableObject.GetValue)
                && call.Arguments.Count == 2
                && call.Arguments[0].Type == typeof(DotvvmProperty)
                && call.Arguments[1].Type == typeof(bool))
            {
                var propertyArgument = call.Arguments[0];
                var setValue = typeof(DotvvmBindableObject)
                    .GetMethod(nameof(DotvvmBindableObject.SetValue),
                        new[] { typeof(DotvvmProperty), typeof(object) });
                return Expression.Call(call.Object, setValue, propertyArgument, value);
            }

            return null;
        }

        public static Expression Call(Expression target, Expression[] arguments)
        {
            if (target is MethodGroupExpression)
            {
                return ((MethodGroupExpression)target).CreateMethodCall(arguments);
            }
            return Expression.Invoke(target, arguments);
        }

        public static Expression CallMethod(Expression target, BindingFlags flags, string name, Type[] typeArguments, Expression[] arguments, IDictionary<string, Expression> namedArgs = null)
        {
            // the following piece of code is nicer and more readable than method recognition done in roslyn, C# dynamic and also expression evaluator :)
            var method = FindValidMethodOveloads(target, target.Type, name, flags, typeArguments, arguments, namedArgs);

            if (method.IsExtension)
            {
                // Change to a static call
                var newArguments = CreateArgumentsForExtensionMethod(target, arguments);
                return Expression.Call(method.Method, newArguments);
            }

            return Expression.Call(target, method.Method, method.Arguments);
        }

        private static Expression[] CreateArgumentsForExtensionMethod(Expression target, Expression[] arguments)
        {
            var newArguments = new Expression[arguments.Length + 1];
            Array.Copy(arguments, 0, newArguments, 1, arguments.Length);
            newArguments[0] = target;
            return newArguments;
        }

        public static Expression CallMethod(Type target, BindingFlags flags, string name, Type[] typeArguments, Expression[] arguments, IDictionary<string, Expression> namedArgs = null)
        {
            // the following piece of code is nicer and more readable than method recognition done in roslyn, C# dynamic and also expression evaluator :)
            var method = FindValidMethodOveloads(null, target, name, flags, typeArguments, arguments, namedArgs);
            return Expression.Call(method.Method, method.Arguments);
        }


        private static MethodRecognitionResult FindValidMethodOveloads(Expression target, Type type, string name, BindingFlags flags, Type[] typeArguments, Expression[] arguments, IDictionary<string, Expression> namedArgs)
        {
            var methods = FindValidMethodOveloads(type.GetAllMembers(flags).OfType<MethodInfo>().Where(m => m.Name == name), typeArguments, arguments, namedArgs).ToList();

            if (methods.Count == 1) return methods.FirstOrDefault();
            if (methods.Count == 0)
            {
                // We did not find any match in regular methods => try extension methods
                if (target != null)
                {
                    // Change to a static call
                    var newArguments = CreateArgumentsForExtensionMethod(target, arguments);
                    var extensions = FindValidMethodOveloads(type.GetAllExtensions().OfType<MethodInfo>().Where(m => m.Name == name), typeArguments, newArguments, namedArgs).ToList();

                    // We found an extension method
                    if (extensions.Count == 1)
                    {
                        extensions[0].IsExtension = true;
                        return extensions.FirstOrDefault();
                    }

                    target = null;
                    methods = extensions;
                    arguments = newArguments;
                }

                if (methods.Count == 0)
                    throw new InvalidOperationException($"Could not find method overload nor extension method that matched '{name}'.");
            }

            // There are multiple method candidates
            methods = methods.OrderBy(s => s.CastCount).ThenBy(s => s.AutomaticTypeArgCount).ThenBy(s => s.HasParamsAttribute).ToList();
            var method = methods.FirstOrDefault();
            var method2 = methods.Skip(1).FirstOrDefault();
            if (method.AutomaticTypeArgCount == method2.AutomaticTypeArgCount && method.CastCount == method2.CastCount && method.HasParamsAttribute == method2.HasParamsAttribute)
            {
                // TODO: this behavior is not completed. Implement the same behavior as in roslyn.
                throw new InvalidOperationException($"Found ambiguous overloads of method '{name}'.");
            }
            return method;
        }

        private static IEnumerable<MethodRecognitionResult> FindValidMethodOveloads(IEnumerable<MethodInfo> methods, Type[] typeArguments, Expression[] arguments, IDictionary<string, Expression> namedArgs)
            => from m in methods
               let r = TryCallMethod(m, typeArguments, arguments, namedArgs)
               where r != null
               orderby r.CastCount descending, r.AutomaticTypeArgCount
               select r;

        class MethodRecognitionResult
        {
            public int AutomaticTypeArgCount { get; set; }
            public int CastCount { get; set; }
            public Expression[] Arguments { get; set; }
            public MethodInfo Method { get; set; }
            public int ParamsArrayCount { get; set; }
            public bool IsExtension { get; set; }
            public bool HasParamsAttribute { get; set; }
        }

        private static MethodRecognitionResult TryCallMethod(MethodInfo method, Type[] typeArguments, Expression[] positionalArguments, IDictionary<string, Expression> namedArguments)
        {
            var parameters = method.GetParameters();
            var hasParamsArrayAttributes = parameters?.LastOrDefault()?.GetCustomAttribute(ParamArrayAttributeType) is object;

            if (!TryPrepareArguments(parameters, positionalArguments, namedArguments, out var args, out var castCount))
                return null;

            int automaticTypeArgs = 0;
            // resolve generic parameters
            if (method.ContainsGenericParameters)
            {
                var genericArguments = method.GetGenericArguments();
                var typeArgs = new Type[genericArguments.Length];
                if (typeArguments != null)
                {
                    if (typeArguments.Length > typeArgs.Length) return null;
                    Array.Copy(typeArguments, typeArgs, typeArgs.Length);
                }
                var parameterTypes = parameters.Select(s => s.ParameterType).ToArray();
                if (hasParamsArrayAttributes && parameterTypes.Length > 0)
                {
                    parameterTypes[parameterTypes.Length - 1] = parameterTypes.Last().GetElementType();
                }
                for (int genericArgumentPosition = 0; genericArgumentPosition < typeArgs.Length; genericArgumentPosition++)
                {
                    if (typeArgs[genericArgumentPosition] == null)
                    {
                        // try to resolve from arguments

                        var argType = GetGenericParameterType(genericArguments[genericArgumentPosition], parameterTypes, args.Select(s => s.Type).ToArray());
                        automaticTypeArgs++;
                        if (argType != null) typeArgs[genericArgumentPosition] = argType;
                        else return null;
                    }
                }
                method = method.MakeGenericMethod(typeArgs);
                parameters = method.GetParameters();
            }
            else if (typeArguments != null) return null;

            // cast arguments
            for (int i = 0; i < args.Length; i++)
            {
                Type elm;
                if (args.Length == i + 1 && hasParamsArrayAttributes && !args[i].Type.IsArray)
                {
                    elm = parameters[i].ParameterType.GetElementType();
                    if (positionalArguments.Skip(i).Any(s => TypeConversion.ImplicitConversion(s, elm) is null))
                    {
                        return null;
                    }
                }
                else
                {
                    elm = parameters[i].ParameterType;
                }
                var casted = TypeConversion.ImplicitConversion(args[i], elm);
                if (casted == null)
                {
                    return null;
                }
                if (casted != args[i])
                {
                    castCount++;
                    args[i] = casted;
                }
                if (args.Length == i + 1 && hasParamsArrayAttributes && !args[i].Type.IsArray)
                {
                    var converted = positionalArguments.Skip(i)
                        .Select(a => TypeConversion.ImplicitConversion(a, elm))
                        .ToArray();
                    args[i] = NewArrayExpression.NewArrayInit(elm, converted);
                }
            }

            return new MethodRecognitionResult {
                CastCount = castCount,
                AutomaticTypeArgCount = automaticTypeArgs,
                Method = method,
                Arguments = args,
                ParamsArrayCount = positionalArguments.Length - args.Length,
                HasParamsAttribute = hasParamsArrayAttributes
            };
        }

        private static bool TryPrepareArguments(ParameterInfo[] parameters, Expression[] positionalArguments, IDictionary<string, Expression> namedArguments, out Expression[] arguments, out int castCount)
        {
            castCount = 0;
            arguments = null;
            var addedArguments = 0;
            var hasParamsArrayAttribute = parameters?.LastOrDefault()?.GetCustomAttribute(ParamArrayAttributeType) is object;

            // For methods without `params` arguments count must be at least equal to parameters count
            if (!hasParamsArrayAttribute && parameters.Length < positionalArguments.Length)
                return false;

            arguments = new Expression[parameters.Length];
            var copyItemsCount = !hasParamsArrayAttribute ? positionalArguments.Length : parameters.Length;

            if (hasParamsArrayAttribute && parameters.Length > positionalArguments.Length)
            {
                var parameter = parameters.Last();
                var elementType = parameter.ParameterType.GetElementType();

                // User specified no arguments for the `params` array, we need to create an empty array
                arguments[arguments.Length - 1] = Expression.NewArrayInit(elementType);

                // Last argument was just generated => do not copy
                addedArguments++;
                copyItemsCount--;
            }
            if (copyItemsCount > positionalArguments.Length)
            {
                // Check if we could use default parameters
                var defaultParametersCount = parameters.Skip(positionalArguments.Length).Where(param => param.HasDefaultValue).Count();
                if (defaultParametersCount + positionalArguments.Length >= copyItemsCount)
                    copyItemsCount = positionalArguments.Length;
                else
                    return false;
            }

            Array.Copy(positionalArguments, arguments, copyItemsCount);

            // Process named arguments
            var namedArgCount = 0;
            for (var i = positionalArguments.Length; i < arguments.Length; i++)
            {
                if (namedArguments?.ContainsKey(parameters[i].Name) == true)
                {
                    arguments[i] = namedArguments[parameters[i].Name];
                    namedArgCount++;
                }
                else if (parameters[i].HasDefaultValue)
                {
                    castCount++;
                    arguments[i] = Expression.Constant(parameters[i].DefaultValue, parameters[i].ParameterType);
                }
                else if (parameters[i].GetCustomAttribute(ParamArrayAttributeType) is object)
                {
                    break;
                }
                else return false;
            }

            // Some named arguments were not used
            if (namedArguments != null && namedArgCount != namedArguments.Count)
                return false;

            return true;
        }

        private static Type GetGenericParameterType(Type genericArg, Type[] searchedGenericTypes, Type[] expressionTypes)
        {
            for (var i = 0; i < searchedGenericTypes.Length; i++)
            {
                if (expressionTypes.Length <= i) return null;
                var sgt = searchedGenericTypes[i];
                if (sgt == genericArg)
                {
                    return expressionTypes[i];
                }
                if (sgt.IsArray)
                {
                    var elementType = sgt.GetElementType();
                    var expressionElementType = expressionTypes[i].GetElementType();
                    if (elementType == genericArg)
                        return expressionElementType;
                    else
                        return GetGenericParameterType(genericArg, searchedGenericTypes[i].GetGenericArguments(), expressionTypes[i].GetGenericArguments());
                }
                else if (sgt.IsGenericType)
                {
                    Type[] genericArguments;
                    var expression = expressionTypes[i];  

                    // Arrays need to be handled in a special way to obtain instantiation
                    if (expression.IsArray)
                        genericArguments = new[] { expression.GetElementType() };
                    else
                        genericArguments = expression.GetGenericArguments();

                    var value = GetGenericParameterType(genericArg, sgt.GetGenericArguments(), genericArguments);
                    if (value is Type) return value;
                }
            }
            return null;
        }

        public static Expression EqualsMethod(Expression left, Expression right)
        {
            Expression equatable = null;
            Expression theOther = null;
            if (typeof(IEquatable<>).IsAssignableFrom(left.Type))
            {
                equatable = left;
                theOther = right;
            }
            else if (typeof(IEquatable<>).IsAssignableFrom(right.Type))
            {
                equatable = right;
                theOther = left;
            }

            if (equatable != null)
            {
                var m = CallMethod(equatable, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy, "Equals", null, new[] { theOther });
                if (m != null) return m;
            }

            if (left.Type.GetTypeInfo().IsValueType)
            {
                equatable = left;
                theOther = right;
            }
            else if (left.Type.GetTypeInfo().IsValueType)
            {
                equatable = right;
                theOther = left;
            }

            if (equatable != null)
            {
                theOther = TypeConversion.ImplicitConversion(theOther, equatable.Type);
                if (theOther != null) return Expression.Equal(equatable, theOther);
            }

            return CallMethod(left, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy, "Equals", null, new[] { right });
        }

        public static Expression CompareMethod(Expression left, Expression right)
        {
            Type compareType = typeof(object);
            Expression equatable = null;
            Expression theOther = null;
            if (typeof(IComparable<>).IsAssignableFrom(left.Type))
            {
                equatable = left;
                theOther = right;
            }
            else if (typeof(IComparable<>).IsAssignableFrom(right.Type))
            {
                equatable = right;
                theOther = left;
            }
            else if (typeof(IComparable).IsAssignableFrom(left.Type))
            {
                equatable = left;
                theOther = right;
            }
            else if (typeof(IComparable).IsAssignableFrom(right.Type))
            {
                equatable = right;
                theOther = left;
            }

            if (equatable != null)
            {
                return CallMethod(equatable, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy, "Compare", null, new[] { theOther });
            }
            throw new NotSupportedException("IComparable is not implemented on any of specified types");
        }

        public static Expression RewriteTaskSequence(Expression left, Expression right)
        {
            // if the left side is a task, make the right side also a task and join them
            Expression rightTask;
            if (right.Type == typeof(void))
            {
                // return Task.CompletedTask
                rightTask = Expression.Call(typeof(CommandTaskSequenceHelper), nameof(CommandTaskSequenceHelper.WrapAsTask),
                    Type.EmptyTypes, Expression.Lambda(right));
            }
            else if (!typeof(Task).IsAssignableFrom(right.Type))
            {
                // wrap the right expression into Task.FromResult
                rightTask = Expression.Call(typeof(CommandTaskSequenceHelper), nameof(CommandTaskSequenceHelper.WrapAsTask),
                    new[] { right.Type }, Expression.Lambda(right));
            }
            else
            {
                // right side is also a task
                rightTask = right;
            }

            // join the tasks using CommandTaskSequenceHelper
            if (rightTask.Type.IsGenericType)
            {
                return Expression.Call(typeof(CommandTaskSequenceHelper), nameof(CommandTaskSequenceHelper.JoinTasks), new[] { rightTask.Type.GetGenericArguments()[0] }, left, Expression.Lambda(rightTask));
            }
            else
            {
                return Expression.Call(typeof(CommandTaskSequenceHelper), nameof(CommandTaskSequenceHelper.JoinTasks), Type.EmptyTypes, left, Expression.Lambda(rightTask));
            }
        }


        public static Expression UnwrapNullable(this Expression expression) =>
            expression.Type.IsNullable() ? Expression.Property(expression, "Value") : expression;

        public static Expression GetBinaryOperator(Expression left, Expression right, ExpressionType operation)
        {
            if (operation == ExpressionType.Coalesce) return Expression.Coalesce(left, right);
            if (operation == ExpressionType.Assign)
            {
                return Expression.Assign(left, TypeConversion.ImplicitConversion(right, left.Type, true, true));
            }

            // TODO: type conversions
            if (operation == ExpressionType.AndAlso) return Expression.AndAlso(left, right);
            else if (operation == ExpressionType.OrElse) return Expression.OrElse(left, right);

            var binder = (DynamicMetaObjectBinder)Microsoft.CSharp.RuntimeBinder.Binder.BinaryOperation(
                CSharpBinderFlags.None, operation, typeof(object), GetBinderArguments(2));
            var result = ApplyBinder(binder, false, left, right);
            if (result != null) return result;
            if (operation == ExpressionType.Equal) return EqualsMethod(left, right);
            if (operation == ExpressionType.NotEqual) return Expression.Not(EqualsMethod(left, right));

            // lift the operator
            if (left.Type.IsNullable() || right.Type.IsNullable())
                return GetBinaryOperator(left.UnwrapNullable(), right.UnwrapNullable(), operation);

            throw new Exception($"could not apply { operation } binary operator to { left } and { right }");
            // TODO: comparison operators
        }

        public static Expression GetUnaryOperator(Expression expr, ExpressionType operation)
        {
            var binder = (DynamicMetaObjectBinder)Microsoft.CSharp.RuntimeBinder.Binder.UnaryOperation(
                CSharpBinderFlags.None, operation, typeof(object), GetBinderArguments(1));
            return ApplyBinder(binder, true, expr);
        }

        public static Expression GetIndexer(Expression expr, Expression index)
        {
            if (expr.Type.IsArray) return Expression.ArrayIndex(expr, index);

            var indexProp = (from p in expr.Type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                             let param = p.GetIndexParameters()
                             where param.Length == 1
                             let indexConvert = TypeConversion.ImplicitConversion(index, param[0].ParameterType)
                             where indexConvert != null
                             select Expression.MakeIndex(expr, p, new[] { indexConvert })).ToArray();
            if (indexProp.Length == 0) throw new Exception($"could not find and indexer property on type { expr.Type } that accepts { index.Type } as argument");
            if (indexProp.Length > 1) throw new Exception($"more than one indexer found on type { expr.Type } that accepts { index.Type } as argument");
            return indexProp[0];
        }

        public static IEnumerable<CSharpArgumentInfo> GetBinderArguments(int count)
        {
            var arr = new CSharpArgumentInfo[count];
            for (int i = 0; i < count; i++)
            {
                arr[i] = CSharpArgumentInfo.Create(CSharpArgumentInfoFlags.UseCompileTimeType, null);
            }
            return arr;
        }

        public static Expression ApplyBinder(DynamicMetaObjectBinder binder, bool throwException, params Expression[] expressions)
        {
            var result = binder.Bind(DynamicMetaObject.Create(null, expressions[0]),
                expressions.Skip(1).Select(e =>
                    DynamicMetaObject.Create(null, e)).ToArray()
            );

            if (result.Expression.NodeType == ExpressionType.Convert)
            {
                var convert = (UnaryExpression)result.Expression;
                return convert.Operand;
            }
            if (result.Expression.NodeType == ExpressionType.Throw)
            {
                if (throwException)
                {
                    // throw the exception
                    Expression.Lambda(result.Expression).Compile().DynamicInvoke();
                }
                else return null;
            }
            return result.Expression;
        }

        public sealed class UnknownTypeSentinel { }
    }
}

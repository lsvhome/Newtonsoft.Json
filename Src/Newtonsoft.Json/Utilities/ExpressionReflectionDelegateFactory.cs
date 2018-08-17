#region License
// Copyright (c) 2007 James Newton-King
//
// Permission is hereby granted, free of charge, to any person
// obtaining a copy of this software and associated documentation
// files (the "Software"), to deal in the Software without
// restriction, including without limitation the rights to use,
// copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the
// Software is furnished to do so, subject to the following
// conditions:
//
// The above copyright notice and this permission notice shall be
// included in all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
// EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES
// OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
// NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT
// HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY,
// WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR
// OTHER DEALINGS IN THE SOFTWARE.
#endregion

#if !(NET20 || NET35)

using System.Collections.Generic;
using System.Linq;
using System.Text;
using System;
using System.Diagnostics;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json.Serialization;

namespace Newtonsoft.Json.Utilities
{
    internal class ExpressionReflectionDelegateFactory : ReflectionDelegateFactory
    {
        private static readonly ExpressionReflectionDelegateFactory _instance = new ExpressionReflectionDelegateFactory();

        internal static ReflectionDelegateFactory Instance => _instance;

        public override ObjectConstructor<object> CreateParameterizedConstructor(MethodBase method)
        {
            ValidationUtils.ArgumentNotNull(method, nameof(method));

            Type type = typeof(object);

            ParameterExpression argsParameterExpression = Expression.Parameter(typeof(object[]), "args");

            Expression callExpression = BuildMethodCall(method, type, null, argsParameterExpression);

            LambdaExpression lambdaExpression = Expression.Lambda(typeof(ObjectConstructor<object>), callExpression, argsParameterExpression);

            ObjectConstructor<object> compiled = (ObjectConstructor<object>)lambdaExpression.Compile();
            return compiled;
        }

        public override MethodCall<T, object> CreateMethodCall<T>(MethodBase method)
        {
            ValidationUtils.ArgumentNotNull(method, nameof(method));

            Type type = typeof(object);

            ParameterExpression targetParameterExpression = Expression.Parameter(type, "target");
            ParameterExpression argsParameterExpression = Expression.Parameter(typeof(object[]), "args");

            Expression callExpression = BuildMethodCall(method, type, targetParameterExpression, argsParameterExpression);

            LambdaExpression lambdaExpression = Expression.Lambda(typeof(MethodCall<T, object>), callExpression, targetParameterExpression, argsParameterExpression);

            MethodCall<T, object> compiled = (MethodCall<T, object>)lambdaExpression.Compile();
            return compiled;
        }

        private class ByRefParameter
        {
            public Expression Value;
            public ParameterExpression Variable;
            public bool IsOut;
        }

        private Expression BuildMethodCall(MethodBase method, Type type, ParameterExpression targetParameterExpression, ParameterExpression argsParameterExpression)
        {
            ParameterInfo[] parametersInfo = method.GetParameters();

            Expression[] argsExpression;
            IList<ByRefParameter> refParameterMap;
            if (parametersInfo.Length == 0)
            {
                argsExpression = CollectionUtils.ArrayEmpty<Expression>();
                refParameterMap = CollectionUtils.ArrayEmpty<ByRefParameter>();
            }
            else
            {
                argsExpression = new Expression[parametersInfo.Length];
                refParameterMap = new List<ByRefParameter>();

                for (int i = 0; i < parametersInfo.Length; i++)
                {
                    ParameterInfo parameter = parametersInfo[i];
                    Type parameterType = parameter.ParameterType;
                    bool isByRef = false;
                    if (parameterType.IsByRef)
                    {
                        parameterType = parameterType.GetElementType();
                        isByRef = true;
                    }

                    Expression indexExpression = Expression.Constant(i);

                    Expression paramAccessorExpression = Expression.ArrayIndex(argsParameterExpression, indexExpression);

                    Expression argExpression = EnsureCastExpression(paramAccessorExpression, parameterType, !isByRef);

                    if (isByRef)
                    {
                        ParameterExpression variable = Expression.Variable(parameterType);
                        refParameterMap.Add(new ByRefParameter {Value = argExpression, Variable = variable, IsOut = parameter.IsOut});

                        argExpression = variable;
                    }

                    argsExpression[i] = argExpression;
                }
            }

            Expression callExpression;
            if (method.IsConstructor)
            {
                callExpression = Expression.New((ConstructorInfo)method, argsExpression);
            }
            else if (method.IsStatic)
            {
                callExpression = Expression.Call((MethodInfo)method, argsExpression);
            }
            else
            {
                Expression readParameter = EnsureCastExpression(targetParameterExpression, method.DeclaringType);

                callExpression = Expression.Call(readParameter, (MethodInfo)method, argsExpression);
            }

            MethodInfo m = method as MethodInfo;
            if (m != null)
            {
                if (m.ReturnType != typeof(void))
                {
                    callExpression = EnsureCastExpression(callExpression, type);
                }
                else
                {
                    callExpression = Expression.Block(callExpression, Expression.Constant(null));
                }
            }
            else
            {
                callExpression = EnsureCastExpression(callExpression, type);
            }

            if (refParameterMap.Count > 0)
            {
                IList<ParameterExpression> variableExpressions = new List<ParameterExpression>();
                IList<Expression> bodyExpressions = new List<Expression>();
                foreach (ByRefParameter p in refParameterMap)
                {
                    if (!p.IsOut)
                    {
                        bodyExpressions.Add(Expression.Assign(p.Variable, p.Value));
                    }

                    variableExpressions.Add(p.Variable);
                }

                bodyExpressions.Add(callExpression);

                callExpression = Expression.Block(variableExpressions, bodyExpressions);
            }

            return callExpression;
        }

        public override Func<T> CreateDefaultConstructor<T>(Type type)
        {
            ValidationUtils.ArgumentNotNull(type, "type");

            // avoid error from expressions compiler because of abstract class
            if (type.IsAbstract())
            {
                return () => (T)Activator.CreateInstance(type);
            }

            try
            {
                Type resultType = typeof(T);

                Expression expression = Expression.New(type);

                expression = EnsureCastExpression(expression, resultType);

                LambdaExpression lambdaExpression = Expression.Lambda(typeof(Func<T>), expression);

                Func<T> compiled = (Func<T>)lambdaExpression.Compile();
                return compiled;
            }
            catch
            {
                // an error can be thrown if constructor is not valid on Win8
                // will have INVOCATION_FLAGS_NON_W8P_FX_API invocation flag
                return () => (T)Activator.CreateInstance(type);
            }
        }

        public override Func<T, object> CreateGet<T>(PropertyInfo propertyInfo)
        {
            Debug.WriteLine("CreateGet 1 " + propertyInfo?.Name);

            ValidationUtils.ArgumentNotNull(propertyInfo, nameof(propertyInfo));

            //Type instanceType = typeof(T);
            //Type resultType = typeof(object);

            //ParameterExpression parameterExpression = Expression.Parameter(instanceType, "instance");
            //Expression resultExpression;

            //MethodInfo getMethod = propertyInfo.GetGetMethod(true);

            //if (getMethod.IsStatic)
            //{
            //    resultExpression = Expression.MakeMemberAccess(null, propertyInfo);
            //}
            //else
            //{
            //    Expression readParameter = EnsureCastExpression(parameterExpression, propertyInfo.DeclaringType);

            //    resultExpression = Expression.MakeMemberAccess(readParameter, propertyInfo);
            //}

            //resultExpression = EnsureCastExpression(resultExpression, resultType);

            //LambdaExpression lambdaExpression = Expression.Lambda(typeof(Func<T, object>), resultExpression, parameterExpression);

            //Func<T, object> compiled = (Func<T, object>)lambdaExpression.Compile();
            //return compiled;

            MethodInfo getMethod = propertyInfo.GetGetMethod(true);

            bool IsNullable(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);



            Func<T, object> compiled1 = (x) =>
            {


                bool isNullableX = IsNullable(x.GetType());
                bool isNullableP = IsNullable(propertyInfo.PropertyType);

                //Type xT = isNullableX ? y.GetType().GetGenericArguments()[0] : y.GetType();
                Type xP = isNullableP ? propertyInfo.PropertyType.GetGenericArguments()[0] : propertyInfo.PropertyType;

                //Debug.WriteLine($"CreateSet 003-1-0 {xP.IsAssignableFrom(xT)} | {xT.IsAssignableFrom(xP)}");

                //if (!propertyInfo.PropertyType.IsAssignableFrom(y.GetType()))
                //{
                //    Debug.WriteLine($"CreateSet 003-1-1 {propertyInfo?.Name} IS NOT ASSIGNABLE: {x.GetType().Name} | {y.GetType().Name} | {propertyInfo.PropertyType.Name}");
                //    Debug.WriteLine($"CreateSet 003-1-2 | {propertyInfo.PropertyType.Name} {isNullableX} | {isNullableP} | {xT.Name} | {xP.Name}");
                //}
                //else
                //{
                //    Debug.WriteLine($"CreateSet 003-002 {propertyInfo?.Name} IS ASSIGNABLE");
                //}

                if (getMethod.IsStatic)
                {
                    T gg = (T)propertyInfo.GetValue(null);
                    return gg;
                }
                else
                {
                    T gg = (T)propertyInfo.GetValue(x);
                    return gg;
                }
            };

            Debug.WriteLine($"CreateSet 004 {propertyInfo?.Name}");
            return compiled1;

        }

        public override Func<T, object> CreateGet<T>(FieldInfo fieldInfo)
        {
            ValidationUtils.ArgumentNotNull(fieldInfo, nameof(fieldInfo));

            ParameterExpression sourceParameter = Expression.Parameter(typeof(T), "source");

            Expression fieldExpression;
            if (fieldInfo.IsStatic)
            {
                fieldExpression = Expression.Field(null, fieldInfo);
            }
            else
            {
                Expression sourceExpression = EnsureCastExpression(sourceParameter, fieldInfo.DeclaringType);

                fieldExpression = Expression.Field(sourceExpression, fieldInfo);
            }

            fieldExpression = EnsureCastExpression(fieldExpression, typeof(object));

            Func<T, object> compiled = Expression.Lambda<Func<T, object>>(fieldExpression, sourceParameter).Compile();
            return compiled;
        }

        public override Action<T, object> CreateSet<T>(FieldInfo fieldInfo)
        {
            ValidationUtils.ArgumentNotNull(fieldInfo, nameof(fieldInfo));

            // use reflection for structs
            // expression doesn't correctly set value
            if (fieldInfo.DeclaringType.IsValueType() || fieldInfo.IsInitOnly)
            {
                return LateBoundReflectionDelegateFactory.Instance.CreateSet<T>(fieldInfo);
            }

            ParameterExpression sourceParameterExpression = Expression.Parameter(typeof(T), "source");
            ParameterExpression valueParameterExpression = Expression.Parameter(typeof(object), "value");

            Expression fieldExpression;
            if (fieldInfo.IsStatic)
            {
                fieldExpression = Expression.Field(null, fieldInfo);
            }
            else
            {
                Expression sourceExpression = EnsureCastExpression(sourceParameterExpression, fieldInfo.DeclaringType);

                fieldExpression = Expression.Field(sourceExpression, fieldInfo);
            }

            Expression valueExpression = EnsureCastExpression(valueParameterExpression, fieldExpression.Type);

            BinaryExpression assignExpression = Expression.Assign(fieldExpression, valueExpression);

            LambdaExpression lambdaExpression = Expression.Lambda(typeof(Action<T, object>), assignExpression, sourceParameterExpression, valueParameterExpression);

            Action<T, object> compiled = (Action<T, object>)lambdaExpression.Compile();
            return compiled;
        }

        public override Action<T, object> CreateSet<T>(PropertyInfo propertyInfo)
        {
            Debug.WriteLine("CreateSet 1 " + propertyInfo?.Name);

            ValidationUtils.ArgumentNotNull(propertyInfo, nameof(propertyInfo));

            // use reflection for structs
            // expression doesn't correctly set value
            if (propertyInfo.DeclaringType.IsValueType())
            {
                return LateBoundReflectionDelegateFactory.Instance.CreateSet<T>(propertyInfo);
            }

            Type instanceType = typeof(T);
            Type valueType = typeof(object);

            ParameterExpression instanceParameter = Expression.Parameter(instanceType, "instance");

            ParameterExpression valueParameter = Expression.Parameter(valueType, "value");
            Expression readValueParameter = EnsureCastExpression(valueParameter, propertyInfo.PropertyType);

            MethodInfo setMethod = propertyInfo.GetSetMethod(true);

            try
            {
                Debug.WriteLine($"CreateSet 011-1-0 {setMethod != null} {readValueParameter != null}");
                Expression setExpression;
                if (setMethod.IsStatic)
                {
                    Debug.WriteLine($"CreateSet 011-1-1");
                    setExpression = Expression.Call(setMethod, readValueParameter);
                }
                else
                {
                    Debug.WriteLine($"CreateSet 011-1-2");
                    Expression readInstanceParameter = EnsureCastExpression(instanceParameter, propertyInfo.DeclaringType);

                    Debug.WriteLine($"CreateSet 011-1-3 {readInstanceParameter != null}");
                    setExpression = Expression.Call(readInstanceParameter, setMethod, readValueParameter);
                    Debug.WriteLine($"CreateSet 011-1-4 {setExpression != null}");
                }

                var a = typeof(LambdaExpression).Assembly;
                
                Debug.WriteLine($"CreateSet 011-2-0  {a.FullName} {a.Location}");
                LambdaExpression lambdaExpression = Expression.Lambda(typeof(Action<T, object>), setExpression, instanceParameter, valueParameter);
                Debug.WriteLine($"CreateSet 011-2-1 {lambdaExpression != null}");
                Debug.WriteLine($"CreateSet 011-2-2 {lambdaExpression.Name}  {lambdaExpression.Body}  {lambdaExpression.NodeType}  {lambdaExpression.Parameters}  {lambdaExpression.ReturnType}");



                Action<T, object> compiled = (Action<T, object>)lambdaExpression.Compile();
                Debug.WriteLine($"CreateSet 011-3-0 {compiled != null}");
                return compiled;

            }
            catch (Exception e)
            {

                Debug.WriteLine(e.ToString());
                //e.Process();
                //System.Console.WriteLine(e);
                //throw;


                bool IsNullable(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>);



                Action<T, object> compiled1 = (x, y) =>
                {


                    bool isNullableX = IsNullable(x.GetType());
                    bool isNullableP = IsNullable(propertyInfo.PropertyType);

                    Type xT = isNullableX ? y.GetType().GetGenericArguments()[0] : y.GetType();
                    Type xP = isNullableP ? propertyInfo.PropertyType.GetGenericArguments()[0] : propertyInfo.PropertyType;

                    Debug.WriteLine($"CreateSet 003-1-0 {xP.IsAssignableFrom(xT)} | {xT.IsAssignableFrom(xP)}");

                    if (!propertyInfo.PropertyType.IsAssignableFrom(y.GetType()))
                    {
                        Debug.WriteLine($"CreateSet 003-1-1 {propertyInfo?.Name} IS NOT ASSIGNABLE: {x.GetType().Name} | {y.GetType().Name} | {propertyInfo.PropertyType.Name}");
                        Debug.WriteLine($"CreateSet 003-1-2 | {propertyInfo.PropertyType.Name} {isNullableX} | {isNullableP} | {xT.Name} | {xP.Name}");
                    }
                    else
                    {
                        Debug.WriteLine($"CreateSet 003-002 {propertyInfo?.Name} IS ASSIGNABLE");
                    }

                    if (setMethod.IsStatic)
                    {
                        propertyInfo.SetValue(null, y);
                    }
                    else
                    {
                        propertyInfo.SetValue(x, y);
                    }
                };

                Debug.WriteLine($"CreateSet 004 {propertyInfo?.Name}");
                return compiled1;

            }

        }
        
        private Expression EnsureCastExpression(Expression expression, Type targetType, bool allowWidening = false)
        {
            Type expressionType = expression.Type;
            
            // check if a cast or conversion is required
            if (expressionType == targetType || (!expressionType.IsValueType() && targetType.IsAssignableFrom(expressionType)))
            {
                return expression;
            }

            if (targetType.IsValueType())
            {
                Expression convert = Expression.Unbox(expression, targetType);

                if (allowWidening && targetType.IsPrimitive())
                {
                    MethodInfo toTargetTypeMethod = typeof(Convert)
                        .GetMethod("To" + targetType.Name, new[] { typeof(object) });

                    if (toTargetTypeMethod != null)
                    {
                        convert = Expression.Condition(
                            Expression.TypeIs(expression, targetType),
                            convert,
                            Expression.Call(toTargetTypeMethod, expression));
                    }
                }
                
                return Expression.Condition(
                    Expression.Equal(expression, Expression.Constant(null, typeof(object))),
                    Expression.Default(targetType), 
                    convert);
            }

            return Expression.Convert(expression, targetType);
        }
    }
}

#endif
﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.InMemory.Query.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Query.Pipeline;
using Microsoft.EntityFrameworkCore.Storage;

namespace Microsoft.EntityFrameworkCore.InMemory.Query.Pipeline
{
    public class InMemoryShapedQueryCompilingExpressionVisitor : ShapedQueryCompilingExpressionVisitor
    {
        public InMemoryShapedQueryCompilingExpressionVisitor(IEntityMaterializerSource entityMaterializerSource, bool trackQueryResults)
            : base(entityMaterializerSource, trackQueryResults)
        {
        }

        protected override Expression VisitExtension(Expression extensionExpression)
        {
            switch (extensionExpression)
            {
                case InMemoryQueryExpression inMemoryQueryExpression:
                    inMemoryQueryExpression.ApplyServerProjection();

                    return Visit(inMemoryQueryExpression.ServerQueryExpression);

                case InMemoryTableExpression inMemoryTableExpression:
                    return Expression.Call(
                        _queryMethodInfo,
                        QueryCompilationContext2.QueryContextParameter,
                        Expression.Constant(inMemoryTableExpression.EntityType));
            }

            return base.VisitExtension(extensionExpression);
        }


        protected override Expression VisitShapedQueryExpression(ShapedQueryExpression shapedQueryExpression)
        {
            var shaperLambda = InjectEntityMaterializer(shapedQueryExpression.ShaperExpression);

            var innerEnumerable = Visit(shapedQueryExpression.QueryExpression);

            var enumeratorParameter = Expression.Parameter(typeof(IEnumerator<ValueBuffer>), "enumerator");
            var hasNextParameter = Expression.Parameter(typeof(bool).MakeByRefType(), "hasNext");

            var newBody = new InMemoryProjectionBindingRemovingExpressionVisitor(
                (InMemoryQueryExpression)shapedQueryExpression.QueryExpression)
                .Visit(shaperLambda.Body);

            newBody = ReplacingExpressionVisitor.Replace(
                MoveNextMarker,
                Expression.Assign(hasNextParameter, Expression.Call(enumeratorParameter, _enumeratorMoveNextMethodInfo)),
                newBody);

            newBody = ReplacingExpressionVisitor.Replace(
                InMemoryQueryExpression.ValueBufferParameter,
                Expression.MakeMemberAccess(enumeratorParameter, _enumeratorCurrent),
                newBody);

            shaperLambda = (LambdaExpression)_createLambdaMethodInfo.MakeGenericMethod(newBody.Type)
                .Invoke(
                    null,
                    new object[]
                    {
                        newBody,
                        new [] {
                            QueryCompilationContext2.QueryContextParameter,
                            enumeratorParameter,
                            hasNextParameter
                        }
                    });

            return Expression.Call(
                _shapeMethodInfo.MakeGenericMethod(shaperLambda.ReturnType),
                innerEnumerable,
                QueryCompilationContext2.QueryContextParameter,
                Expression.Constant(shaperLambda.Compile()));
        }

        private readonly MemberInfo _enumeratorCurrent = typeof(IEnumerator<ValueBuffer>)
            .GetProperty(nameof(IEnumerator<ValueBuffer>.Current));

        private static readonly MethodInfo _enumeratorMoveNextMethodInfo
            = typeof(IEnumerator).GetTypeInfo()
                .GetRuntimeMethod(nameof(IEnumerator.MoveNext), new Type[] { });

        private static readonly MethodInfo _createLambdaMethodInfo
            = typeof(InMemoryShapedQueryCompilingExpressionVisitor).GetTypeInfo()
                .GetDeclaredMethod(nameof(CreateLambda));

        private static LambdaExpression CreateLambda<T>(Expression body, ParameterExpression[] parameters)
        {
            return Expression.Lambda<Shaper<T>>(
                body,
                parameters);
        }

        private static readonly MethodInfo _queryMethodInfo
            = typeof(InMemoryShapedQueryCompilingExpressionVisitor).GetTypeInfo()
                .GetDeclaredMethod(nameof(Query));

        private static IEnumerable<ValueBuffer> Query(
            QueryContext queryContext,
            IEntityType entityType)
        {
            return ((InMemoryQueryContext)queryContext).Store
                .GetTables(entityType)
                .SelectMany(t => t.Rows.Select(vs => new ValueBuffer(vs)));
        }

        private delegate T Shaper<T>(QueryContext queryContext, IEnumerator<ValueBuffer> enumerator, out bool hasNext);

        private static readonly MethodInfo _shapeMethodInfo
            = typeof(InMemoryShapedQueryCompilingExpressionVisitor).GetTypeInfo().GetDeclaredMethod(nameof(_Shape));

        private static IEnumerable<TResult> _Shape<TResult>(
            IEnumerable<ValueBuffer> innerEnumerable,
            QueryContext queryContext,
            Shaper<TResult> shaper)
        {
            var enumerator = innerEnumerable.GetEnumerator();
            var hasNext = enumerator.MoveNext();
            while (hasNext)
            {
                yield return shaper(queryContext, enumerator, out hasNext);
            }
        }

        private class InMemoryProjectionBindingRemovingExpressionVisitor : ExpressionVisitor
        {
            private readonly InMemoryQueryExpression _queryExpression;
            private readonly IDictionary<ParameterExpression, int> _materializationContextBindings
                = new Dictionary<ParameterExpression, int>();

            public InMemoryProjectionBindingRemovingExpressionVisitor(InMemoryQueryExpression queryExpression)
            {
                _queryExpression = queryExpression;
            }

            protected override Expression VisitBinary(BinaryExpression binaryExpression)
            {
                if (binaryExpression.NodeType == ExpressionType.Assign
                    && binaryExpression.Left is ParameterExpression parameterExpression
                    && parameterExpression.Type == typeof(MaterializationContext))
                {
                    var newExpression = (NewExpression)binaryExpression.Right;

                    var innerExpression = Visit(newExpression.Arguments[0]);

                    var entityStartIndex = ((EntityValuesExpression)innerExpression).StartIndex;
                    _materializationContextBindings[parameterExpression] = entityStartIndex;

                    var updatedExpression = Expression.New(newExpression.Constructor,
                        Expression.Constant(ValueBuffer.Empty),
                        newExpression.Arguments[1]);

                    return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, updatedExpression);
                }

                return base.VisitBinary(binaryExpression);
            }

            protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
            {
                if (methodCallExpression.Method.IsGenericMethod
                    && methodCallExpression.Method.GetGenericMethodDefinition() == EntityMaterializerSource.TryReadValueMethod)
                {
                    var originalIndex = (int)((ConstantExpression)methodCallExpression.Arguments[1]).Value;
                    var indexOffset = methodCallExpression.Arguments[0] is ProjectionBindingExpression projectionBindingExpression
                        ? ((EntityValuesExpression)_queryExpression.GetProjectionExpression(projectionBindingExpression.ProjectionMember)).StartIndex
                        : _materializationContextBindings[(ParameterExpression)((MethodCallExpression)methodCallExpression.Arguments[0]).Object];

                    return Expression.Call(
                        methodCallExpression.Method,
                        InMemoryQueryExpression.ValueBufferParameter,
                        Expression.Constant(indexOffset + originalIndex),
                        methodCallExpression.Arguments[2]);
                }

                return base.VisitMethodCall(methodCallExpression);
            }

            protected override Expression VisitExtension(Expression extensionExpression)
            {
                if (extensionExpression is ProjectionBindingExpression projectionBindingExpression)
                {
                    return _queryExpression.GetProjectionExpression(projectionBindingExpression.ProjectionMember);
                }

                return base.VisitExtension(extensionExpression);
            }
        }
    }
}

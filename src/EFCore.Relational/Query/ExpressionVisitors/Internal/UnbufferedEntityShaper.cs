// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Linq.Expressions;
using JetBrains.Annotations;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Remotion.Linq.Clauses;

namespace Microsoft.EntityFrameworkCore.Query.ExpressionVisitors.Internal
{
    /// <summary>
    ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
    ///     directly from your code. This API may change or be removed in future releases.
    /// </summary>
    public class UnbufferedEntityShaper<TEntity> : EntityShaper, IShaper<TEntity>
        where TEntity : class
    {
        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public UnbufferedEntityShaper(
            [NotNull] IQuerySource querySource,
            bool trackingQuery,
            [NotNull] IKey key,
            [NotNull] Func<MaterializationContext, object> materializer,
            [CanBeNull] Expression materializerExpression)
            : base(querySource, trackingQuery, key, materializer, materializerExpression)
        {
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public override Type Type => typeof(TEntity);

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public virtual TEntity Shape(QueryContext queryContext, in ValueBuffer valueBuffer)
        {
            if (IsTrackingQuery)
            {
                var entry = queryContext.StateManager.TryGetEntry(Key, new object[] { }, !AllowNullResult, out var _);

                if (entry != null)
                {
                    return (TEntity)entry.Entity;
                }
            }

            return (TEntity)Materializer(
                new MaterializationContext(
                    valueBuffer,
                    queryContext.Context));
        }

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public override IShaper<TDerived> Cast<TDerived>()
            => new UnbufferedOffsetEntityShaper<TDerived>(
                QuerySource,
                IsTrackingQuery,
                Key,
                Materializer);

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public override Shaper WithOffset(int offset)
            => new UnbufferedOffsetEntityShaper<TEntity>(
                    QuerySource,
                    IsTrackingQuery,
                    Key,
                    Materializer)
                .AddOffset(offset);

        /// <summary>
        ///     This API supports the Entity Framework Core infrastructure and is not intended to be used
        ///     directly from your code. This API may change or be removed in future releases.
        /// </summary>
        public override string ToString() => "UnbufferedEntityShaper<" + typeof(TEntity).Name + ">";
    }
}

﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

namespace Microsoft.CodeAnalysis.PooledObjects;

internal sealed partial class ArrayBuilder<T> : IPooled
{
    public static PooledDisposer<ArrayBuilder<T>> GetInstance(out ArrayBuilder<T> instance)
        => GetInstance(discardLargeInstances: true, out instance);

    public static PooledDisposer<ArrayBuilder<T>> GetInstance(int capacity, out ArrayBuilder<T> instance)
    {
        instance = GetInstance(capacity);
        return new PooledDisposer<ArrayBuilder<T>>(instance);
    }

    public static PooledDisposer<ArrayBuilder<T>> GetInstance(int capacity, T fillWithValue, out ArrayBuilder<T> instance)
    {
        instance = GetInstance(capacity, fillWithValue);
        return new PooledDisposer<ArrayBuilder<T>>(instance);
    }

    public static PooledDisposer<ArrayBuilder<T>> GetInstance(bool discardLargeInstances, out ArrayBuilder<T> instance)
    {
        instance = GetInstance();
        return new PooledDisposer<ArrayBuilder<T>>(instance, discardLargeInstances);
    }

    void IPooled.Free(bool discardLargeInstances)
    {
        var pool = _pool;
        if (pool != null)
        {
            if (!discardLargeInstances || _builder.Capacity < PooledArrayLengthLimitExclusive)
            {
                if (this.Count != 0)
                {
                    this.Clear();
                }

                pool.Free(this);
            }
            else
            {
                pool.ForgetTrackedObject(this);
            }
        }
    }
}

#nullable enable

using System;
using System.Collections.Generic;

namespace SharpGen.Runtime;

public abstract partial class CallbackBase
{
    private static readonly Dictionary<Type, CallbackTypeInfo> TypeReflectionCache = new();

    private CallbackTypeInfo GetTypeInfo()
    {
        CallbackTypeInfo? info;
        var type = GetType();
        var cache = TypeReflectionCache;

        lock (cache)
        {
            if (cache.TryGetValue(type, out info))
                return info;
        }

        info = new CallbackTypeInfo(type);

        lock (cache)
            cache[type] = info;

        return info;
    }
}
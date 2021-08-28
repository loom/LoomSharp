﻿namespace Loom.Messaging
{
    using System;

    public class FullNameTypeNameResolvingStrategy : ITypeNameResolvingStrategy
    {
        public string? ResolveTypeName(Type type) => type switch
        {
            null => throw new ArgumentNullException(nameof(type)),
            _ => type.FullName
        };
    }
}

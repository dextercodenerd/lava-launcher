using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

namespace GenericLauncher.Database.Orm;

public interface ITypeHandler<T>
{
    void SetValue(DbParameter p, T value);
    T Parse(DbParameter p);
}

public sealed class TypeHandlers
{
    private readonly Dictionary<Type, object> _map = new();

    public void Add<T>(ITypeHandler<T> handler)
    {
        _map[typeof(T)] = handler;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryGet<T>(out ITypeHandler<T> h)
    {
        if (_map.TryGetValue(typeof(T), out var obj))
        {
            h = (ITypeHandler<T>)obj;
            return true;
        }

        h = default!;
        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write<T>(DbParameter p, T value)
    {
        if (TryGet<T>(out var h))
        {
            h.SetValue(p, value);
        }
        else
        {
            p.Value = value is null ? DBNull.Value : (object)value;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal T Read<T>(DbDataReader r, int ordinal)
    {
        if (r.IsDBNull(ordinal))
        {
            return default!;
        }

        if (TryGet<T>(out var h))
        {
            using var tmp = r.GetSchemaTable(); // fake parameter holder
            var p = new SqliteParameter
            {
                Value = r.GetValue(ordinal)
            };
            return h.Parse(p);
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target.IsEnum)
        {
            var u = Enum.GetUnderlyingType(target);
            var prim = Convert.ChangeType(r.GetValue(ordinal), u);
            return (T)Enum.ToObject(target, prim!);
        }

        try
        {
            return r.GetFieldValue<T>(ordinal);
        }
        catch
        {
            return (T)Convert.ChangeType(r.GetValue(ordinal), target);
        }
    }
}

public readonly struct Row
{
    private readonly DbDataReader _r;
    private readonly TypeHandlers _handlers;

    public Row(DbDataReader r, TypeHandlers handlers)
    {
        _r = r;
        _handlers = handlers;
    }

    public int Ord(string name) => _r.GetOrdinal(name);

    public bool IsNull(int i) => _r.IsDBNull(i);

    public T Get<T>(int i) => _handlers.Read<T>(_r, i);
}

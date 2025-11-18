using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace GenericLauncher.Database.Orm;

// Async Dapper-like extensions that buffer and return IEnumerable<T>.
// Assumes SqliteConnection is already open.
public static class SqliteConnectionExtensions
{
    // Global default handlers (can be configured at app startup)
    public static readonly TypeHandlers DefaultHandlers = new();

    // Parameter helper
    public static SqliteParameter AddParam<T>(this SqliteCommand cmd,
        string name,
        T value,
        TypeHandlers? handlers = null,
        DbType? dbType = null)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        if (dbType.HasValue)
        {
            p.DbType = dbType.Value;
        }

        (handlers ?? DefaultHandlers).Write(p, value);
        cmd.Parameters.Add(p);
        return (SqliteParameter)p;
    }

    // Execute (INSERT/UPDATE/DELETE)
    public static async Task<int> ExecuteAsync(this SqliteConnection conn,
        string sql,
        Action<SqliteCommand>? bind = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        if (timeoutSeconds.HasValue)
        {
            cmd.CommandTimeout = timeoutSeconds.Value;
        }

        bind?.Invoke(cmd);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // Generic payload + ad-hoc binder (kept for flexibility)
    public static async Task<int> ExecuteAsync<T>(this SqliteConnection conn,
        string sql,
        T args,
        Action<SqliteCommand, T> bind,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        if (timeoutSeconds.HasValue)
        {
            cmd.CommandTimeout = timeoutSeconds.Value;
        }

        bind(cmd, args);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // Static binder on the DTO via IParams<T>
    public static async Task<int> ExecuteAsync<T>(this SqliteConnection conn,
        string sql,
        T args,
        int? timeoutSeconds = null,
        CancellationToken ct = default) where T : IParams<T>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        if (timeoutSeconds.HasValue)
        {
            cmd.CommandTimeout = timeoutSeconds.Value;
        }

        T.Bind(cmd, args, DefaultHandlers);
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // Batch, ad-hoc binder
    public static async Task<int> ExecuteAsync<T>(this SqliteConnection conn,
        string sql,
        IEnumerable<T> batch,
        Action<SqliteCommand, T> bind,
        bool useTransaction = true,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        if (timeoutSeconds.HasValue)
        {
            cmd.CommandTimeout = timeoutSeconds.Value;
        }

        var tx = useTransaction ? conn.BeginTransaction() : null;
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }

        var affected = 0;
        try
        {
            foreach (var item in batch)
            {
                cmd.Parameters.Clear();
                bind(cmd, item);
                affected += await cmd.ExecuteNonQueryAsync(ct);
            }

            tx?.Commit();
            return affected;
        }
        catch
        {
            tx?.Rollback();
            throw;
        }
    }

    // Batch, static binder on DTO
    public static async Task<int> ExecuteAsync<T>(this SqliteConnection conn,
        string sql,
        IEnumerable<T> batch,
        bool useTransaction = true,
        int? timeoutSeconds = null,
        CancellationToken ct = default) where T : IParams<T>
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        if (timeoutSeconds.HasValue)
        {
            cmd.CommandTimeout = timeoutSeconds.Value;
        }

        var tx = useTransaction ? conn.BeginTransaction() : null;
        if (tx is not null)
        {
            cmd.Transaction = tx;
        }

        var affected = 0;
        try
        {
            foreach (var item in batch)
            {
                cmd.Parameters.Clear();
                T.Bind(cmd, item, DefaultHandlers);
                affected += await cmd.ExecuteNonQueryAsync(ct);
            }

            tx?.Commit();
            return affected;
        }
        catch
        {
            tx?.Rollback();
            throw;
        }
    }

    public static async Task<T> ExecuteScalarAsync<T>(this SqliteConnection conn,
        string sql,
        TypeHandlers? handlers = null,
        Action<SqliteCommand>? bind = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        handlers ??= DefaultHandlers;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        if (timeoutSeconds.HasValue)
        {
            cmd.CommandTimeout = timeoutSeconds.Value;
        }

        bind?.Invoke(cmd);

        var obj = await cmd.ExecuteScalarAsync(ct);
        if (obj is null || obj is DBNull)
        {
            return default!;
        }

        if (handlers.TryGet<T>(out var h))
        {
            var p = cmd.CreateParameter();
            p.Value = obj;
            return h.Parse(p);
        }

        var target = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        if (target.IsEnum)
        {
            var u = Enum.GetUnderlyingType(target);
            var prim = Convert.ChangeType(obj, u);
            return (T)Enum.ToObject(target, prim!);
        }

        return (T)Convert.ChangeType(obj, target);
    }

    // Insert returning rowid, ad-hoc binder
    public static async Task<long> ExecuteReturningRowIdAsync<T>(this SqliteConnection conn,
        string insertSql,
        T args,
        Action<SqliteCommand, T> bind,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = insertSql;
            cmd.CommandType = CommandType.Text;
            if (timeoutSeconds.HasValue)
            {
                cmd.CommandTimeout = timeoutSeconds.Value;
            }

            bind(cmd, args);
            _ = await cmd.ExecuteNonQueryAsync(ct);
        }

        using var scalar = conn.CreateCommand();
        scalar.CommandText = "SELECT last_insert_rowid()";
        var obj = await scalar.ExecuteScalarAsync(ct);
        return obj is long l ? l : Convert.ToInt64(obj);
    }

    // Insert returning rowid, static binder on DTO
    public static async Task<long> ExecuteReturningRowIdAsync<T>(this SqliteConnection conn,
        string insertSql,
        T args,
        int? timeoutSeconds = null,
        CancellationToken ct = default) where T : IParams<T>
    {
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = insertSql;
            cmd.CommandType = CommandType.Text;
            if (timeoutSeconds.HasValue)
            {
                cmd.CommandTimeout = timeoutSeconds.Value;
            }

            T.Bind(cmd, args, DefaultHandlers);
            _ = await cmd.ExecuteNonQueryAsync(ct);
        }

        using var scalar = conn.CreateCommand();
        scalar.CommandText = "select last_insert_rowid()";
        var obj = await scalar.ExecuteScalarAsync(ct);
        return obj is long l ? l : Convert.ToInt64(obj);
    }

    // Existing query helpers (unchanged)
    public static async Task<IEnumerable<T>> QueryAsync<T>(this SqliteConnection conn,
        string sql,
        TypeHandlers handlers,
        Func<DbDataReader, Row, T> map,
        Action<SqliteCommand>? bind = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        if (timeoutSeconds.HasValue)
        {
            cmd.CommandTimeout = timeoutSeconds.Value;
        }

        bind?.Invoke(cmd);

        using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var row = new Row(r, handlers);
        var list = new List<T>();
        while (await r.ReadAsync(ct))
        {
            list.Add(map(r, row));
        }

        return list;
    }

    public static Task<IEnumerable<T>> QueryAsync<T>(this SqliteConnection conn,
        string sql,
        TypeHandlers? handlers = null,
        Action<SqliteCommand>? bind = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default) where T : IRecord<T>
    {
        return conn.QueryAsync(sql,
            handlers ?? DefaultHandlers,
            static (r, row) => T.Read(row),
            bind,
            timeoutSeconds,
            ct);
    }

    public static async Task<IEnumerable<string>> QueryAsync(this SqliteConnection conn,
        string sql,
        Action<SqliteCommand>? bind = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandType = CommandType.Text;
        if (timeoutSeconds.HasValue)
        {
            cmd.CommandTimeout = timeoutSeconds.Value;
        }

        bind?.Invoke(cmd);

        using var r = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess, ct);
        var list = new List<string>();
        while (await r.ReadAsync(ct))
        {
            list.Add(r.IsDBNull(0) ? string.Empty : r.GetString(0));
        }

        return list;
    }

    // QuerySingle/First helpers
    public static async Task<T?> QuerySingleOrDefaultAsync<T>(
        this SqliteConnection conn,
        string sql,
        TypeHandlers? handlers = null,
        Action<SqliteCommand>? bind = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
        where T : IRecord<T>
    {
        var items = await conn.QueryAsync<T>(sql, handlers ?? DefaultHandlers, bind, timeoutSeconds, ct);
        using var e = items.GetEnumerator();
        if (!e.MoveNext())
        {
            return default;
        }

        var first = e.Current;
        if (e.MoveNext())
        {
            throw new InvalidOperationException("Sequence contains more than one element.");
        }

        return first;
    }

    public static async Task<T?> QueryFirstOrDefaultAsync<T>(
        this SqliteConnection conn,
        string sql,
        TypeHandlers? handlers = null,
        Action<SqliteCommand>? bind = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
        where T : IRecord<T>
    {
        var items = await conn.QueryAsync<T>(sql, handlers ?? DefaultHandlers, bind, timeoutSeconds, ct);
        foreach (var item in items)
        {
            return item;
        }

        return default;
    }

    public static async Task<T> QuerySingleAsync<T>(
        this SqliteConnection conn,
        string sql,
        TypeHandlers? handlers = null,
        Action<SqliteCommand>? bind = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
        where T : IRecord<T>
    {
        var val = await conn.QuerySingleOrDefaultAsync<T>(sql, handlers, bind, timeoutSeconds, ct);
        if (val is null || val.Equals(default(T)))
        {
            throw new InvalidOperationException("Sequence contains no elements.");
        }

        return val;
    }

    public static async Task<T> QueryFirstAsync<T>(
        this SqliteConnection conn,
        string sql,
        TypeHandlers? handlers = null,
        Action<SqliteCommand>? bind = null,
        int? timeoutSeconds = null,
        CancellationToken ct = default)
        where T : IRecord<T>
    {
        var val = await conn.QueryFirstOrDefaultAsync<T>(sql, handlers, bind, timeoutSeconds, ct);
        if (val is null || val.Equals(default(T)))
        {
            throw new InvalidOperationException("Sequence contains no elements.");
        }

        return val;
    }
}

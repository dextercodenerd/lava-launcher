using System;
using System.Data;
using System.Data.Common;
using GenericLauncher.Database.Orm;

namespace GenericLauncher.Database.TypeHandlers;

public class DateTimeHandler : ITypeHandler<DateTime>
{
    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public void SetValue(DbParameter parameter, DateTime value)
    {
        parameter.DbType = DbType.Int64;
        parameter.Value = (long)(value.ToUniversalTime() - Epoch).TotalSeconds;
    }

    public DateTime Parse(DbParameter parameter)
    {
        return parameter.Value switch
        {
            long longValue => Epoch.AddSeconds(longValue).ToUniversalTime(),
            _ => DateTime.MinValue.ToUniversalTime()
        };
    }
}

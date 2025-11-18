using System.Data;
using System.Data.Common;
using GenericLauncher.Database.Orm;
using GenericLauncher.Misc;

namespace GenericLauncher.Database.TypeHandlers;

public class UtcInstantHandler : ITypeHandler<UtcInstant>
{
    public void SetValue(DbParameter parameter, UtcInstant value)
    {
        parameter.DbType = DbType.Int64;
        parameter.Value = value.UnixTimeMilliseconds;
    }

    public UtcInstant Parse(DbParameter parameter)
    {
        return parameter.Value switch
        {
            long longValue => UtcInstant.FromUnixTimeMilliseconds(longValue),
            _ => UtcInstant.UnixEpoch
        };
    }
}

using System;
using System.Data;
using System.Data.Common;
using GenericLauncher.Database.Orm;

namespace GenericLauncher.Database.TypeHandlers;

public class BooleanHandler : ITypeHandler<bool>
{
    public void SetValue(DbParameter parameter, bool value)
    {
        parameter.DbType = DbType.Int64;
        parameter.Value = value ? 1 : 0;
    }

    public bool Parse(DbParameter parameter)
    {
        return parameter.Value switch
        {
            long longValue => longValue != 0,
            int intValue => intValue != 0,
            bool boolValue => boolValue,
            _ => Convert.ToBoolean(parameter.Value)
        };
    }
}

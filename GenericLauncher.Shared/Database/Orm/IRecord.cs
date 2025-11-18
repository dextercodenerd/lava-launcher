using Microsoft.Data.Sqlite;

namespace GenericLauncher.Database.Orm;

public interface IRecord<out TSelf> where TSelf : IRecord<TSelf>
{
    static abstract TSelf Read(Row row);
}

public interface IParams<in TSelf> where TSelf : IParams<TSelf>
{
    static abstract void Bind(SqliteCommand cmd, TSelf value, TypeHandlers handlers);
}

using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using GenericLauncher.Database.Orm;

namespace GenericLauncher.Database.TypeHandlers;

[JsonSerializable(typeof(List<string>))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
public partial class SqliteJsonContext : JsonSerializerContext;

public class ListStringHandler : ITypeHandler<List<string>>
{
    public void SetValue(DbParameter parameter, List<string>? value)
    {
        parameter.DbType = DbType.String;
        parameter.Value = value is null || value.Count == 0
            ? "[]"
            : JsonSerializer.Serialize(value, SqliteJsonContext.Default.ListString);
    }

    public List<string> Parse(DbParameter parameter)
    {
        return parameter.Value switch
        {
            string stringValue => string.IsNullOrWhiteSpace(stringValue)
                ? []
                : JsonSerializer.Deserialize(stringValue, SqliteJsonContext.Default.ListString) ?? [],
            _ => []
        };
    }
}

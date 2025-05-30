using Plugin;
using System;
using System.Collections.Generic;
using System.Linq;

namespace SqlcGenCsharp.Drivers.Generators;

public class CommonGen(DbDriver dbDriver)
{
    public string GetMethodParameterList(string argInterface, IEnumerable<Parameter> parameters)
    {
        return string.IsNullOrEmpty(argInterface) || !parameters.Any()
            ? // no other params
            (dbDriver.Options.ExternalConnection
                ? $"{dbDriver.ConnectionTypeName} {Variable.Connection.AsVarName()}"
                : String.Empty)
            : // we have params
            (dbDriver.Options.ExternalConnection
                ? $"{dbDriver.ConnectionTypeName} {Variable.Connection.AsVarName()}, {argInterface} {Variable.Args.AsVarName()}"
                : $"{argInterface} {Variable.Args.AsVarName()}");
    }

    public static string AddParametersToCommand(IEnumerable<Parameter> parameters)
    {
        return parameters.Select(p =>
        {
            var commandVar = Variable.Command.AsVarName();
            var param = p.Column.Name.ToPascalCase();
            var argsVar = Variable.Args.AsVarName();
            if (p.Column.IsSqlcSlice)
                return $$"""
                         for (int i = 0; i < {{argsVar}}.{{param}}.Length; i++)
                             {{commandVar}}.Parameters.AddWithValue($"@{{p.Column.Name}}Arg{i}", {{argsVar}}.{{param}}[i]);
                         """;

            var nullParamCast = p.Column.NotNull ? string.Empty : " ?? (object)DBNull.Value";
            var addParamToCommand = $"""{commandVar}.Parameters.AddWithValue("@{p.Column.Name}", {argsVar}.{param}{nullParamCast});""";
            return addParamToCommand;
        }).JoinByNewLine();
    }

    public string ConstructDapperParamsDict(IList<Parameter> parameters)
    {
        if (!parameters.Any()) return string.Empty;
        var objectType = dbDriver.AddNullableSuffixIfNeeded("object", false);
        var initParamsDict = $"var {Variable.QueryParams.AsVarName()} = new Dictionary<string, {objectType}>();";
        var argsVar = Variable.Args.AsVarName();
        var queryParamsVar = Variable.QueryParams.AsVarName();

        var dapperParamsCommands = parameters.Select(p =>
        {
            var param = p.Column.Name.ToPascalCase();
            if (p.Column.IsSqlcSlice)
                return $$"""
                        for (int i = 0; i < {{argsVar}}.{{param}}.Length; i++)
                            {{queryParamsVar}}.Add($"@{{p.Column.Name}}Arg{i}", {{argsVar}}.{{param}}[i]);
                        """;

            if (dbDriver.Enums.ContainsKey(p.Column.Type.Name))
                param += "?.ToEnumString()";
            var addParamToDict = $"{queryParamsVar}.Add(\"{p.Column.Name}\", {argsVar}.{param});";
            return addParamToDict;
        });

        return $"""
                 {initParamsDict}
                 {dapperParamsCommands.JoinByNewLine()}
                 """;
    }

    public static string AwaitReaderRow()
    {
        return $"await {Variable.Reader.AsVarName()}.ReadAsync()";
    }

    public static string InitDataReader()
    {
        return $"var {Variable.Reader.AsVarName()} = await {Variable.Command.AsVarName()}.ExecuteReaderAsync()";
    }

    public static string GetSqlTransformations(Query query, string queryTextConstant)
    {
        if (!query.Params.Any(p => p.Column.IsSqlcSlice)) return string.Empty;
        var initVariable = $"var {Variable.TransformedSql.AsVarName()} = {queryTextConstant};";

        var sqlcSliceCommands = query.Params
            .Where(p => p.Column.IsSqlcSlice)
            .Select(c =>
            {
                var sqlTextVar = Variable.TransformedSql.AsVarName();
                return $"""
                         {sqlTextVar} = Utils.TransformQueryForSliceArgs({sqlTextVar}, {Variable.Args.AsVarName()}.{c.Column.Name.ToPascalCase()}.Length, "{c.Column.Name}");
                         """;
            });

        return $"""
                 {initVariable}
                 {sqlcSliceCommands.JoinByNewLine()}
                 """;
    }

    public string InstantiateDataclass(Column[] columns, string returnInterface)
    {
        var columnsInit = new List<string>();
        var actualOrdinal = 0;
        var seenEmbed = new Dictionary<string, int>();

        foreach (var column in columns)
        {
            if (column.EmbedTable is null)
            {
                columnsInit.Add(GetAsSimpleAssignment(column, actualOrdinal));
                actualOrdinal++;
                continue;
            }

            var tableFieldType = column.EmbedTable.Name.ToModelName(column.EmbedTable.Schema, dbDriver.DefaultSchema);
            var tableFieldName = seenEmbed.TryGetValue(tableFieldType, out var value)
                ? $"{tableFieldType}{value}" : tableFieldType;
            seenEmbed.TryAdd(tableFieldType, 1);
            seenEmbed[tableFieldType]++;

            var tableColumnsInit = GetAsEmbeddedTableColumnAssignment(column, actualOrdinal);
            columnsInit.Add($"{tableFieldName} = {InstantiateDataclassInternal(tableFieldType, tableColumnsInit)}");
            actualOrdinal += tableColumnsInit.Length;
        }

        return InstantiateDataclassInternal(returnInterface, columnsInit);

        string[] GetAsEmbeddedTableColumnAssignment(Column tableColumn, int ordinal)
        {
            var schemaName = tableColumn.EmbedTable.Schema == dbDriver.DefaultSchema ? string.Empty : tableColumn.EmbedTable.Schema;
            var tableColumns = dbDriver.Tables[schemaName][tableColumn.EmbedTable.Name].Columns;
            return tableColumns
                .Select((c, o) => GetAsSimpleAssignment(c, o + ordinal))
                .ToArray();
        }

        string GetAsSimpleAssignment(Column column, int ordinal)
        {
            var readExpression = GetReadExpression(column, ordinal);
            return $"{column.Name.ToPascalCase()} = {readExpression}";
        }

        string GetReadExpression(Column column, int ordinal)
        {
            return column.NotNull
                ? dbDriver.GetColumnReader(column, ordinal)
                : $"{CheckNullExpression(ordinal)} ? {GetNullExpression(column)} : {dbDriver.GetColumnReader(column, ordinal)}";
        }

        string GetNullExpression(Column column)
        {
            var csharpType = dbDriver.GetCsharpType(column);
            if (dbDriver.Options.DotnetFramework.IsDotnetCore()) return "null";
            return dbDriver.IsTypeNullable(csharpType) ? $"({csharpType}) null" : "null";
        }

        string CheckNullExpression(int ordinal)
        {
            return $"{Variable.Reader.AsVarName()}.IsDBNull({ordinal})";
        }

        string InstantiateDataclassInternal(string name, IEnumerable<string> fieldsInit)
        {
            return $$"""
                     new {{name}}
                     {
                         {{fieldsInit.JoinByComma()}}
                     }
                     """;
        }
    }
}
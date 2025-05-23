using Microsoft.CodeAnalysis.CSharp.Syntax;
using Plugin;
using System.Linq;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;


namespace SqlcGenCsharp.Drivers.Generators;

public class ManyDeclareGen(DbDriver dbDriver)
{
    private CommonGen CommonGen { get; } = new(dbDriver);

    public MemberDeclarationSyntax Generate(string queryTextConstant, string argInterface, string returnInterface, Query query)
    {
        var parametersStr = CommonGen.GetMethodParameterList(argInterface, query.Params);
        var returnType = $"Task<List<{returnInterface}>>";
        string staticKeyword = dbDriver.Options.ExternalConnection ? "static" : string.Empty;
        return ParseMemberDeclaration($$"""
            public {{staticKeyword}} async {{returnType}} {{query.Name}}({{parametersStr}})
            {
                {{GetMethodBody(queryTextConstant, returnInterface, query)}}
            }
            """)!;
    }

    private string GetMethodBody(string queryTextConstant, string returnInterface, Query query)
    {
        var (establishConnection, connectionOpen) = dbDriver.EstablishConnection(query);
        var sqlTextTransform = CommonGen.GetSqlTransformations(query, queryTextConstant);
        var resultVar = Variable.Result.AsVarName();
        var anyEmbeddedTableExists = query.Columns.Any(c => c.EmbedTable is not null);
        return dbDriver.Options.UseDapper && !anyEmbeddedTableExists
            ? GetAsDapper()
            : GetAsDriver();

        string GetAsDapper()
        {
            var dapperParamsSection = CommonGen.ConstructDapperParamsDict(query.Params);
            var dapperArgs = dapperParamsSection != string.Empty ? $", {Variable.QueryParams.AsVarName()}" : string.Empty;
            var returnType = dbDriver.AddNullableSuffixIfNeeded(returnInterface, true);
            var sqlQuery = sqlTextTransform != string.Empty ? Variable.TransformedSql.AsVarName() : queryTextConstant;

            return dbDriver.Options.ExternalConnection
                ? $$"""
                        {{sqlTextTransform}}{{dapperParamsSection}}
                            var {{resultVar}} = await {{Variable.Connection.AsVarName()}}.QueryAsync<{{returnType}}>({{sqlQuery}}{{dapperArgs}});
                            return {{resultVar}}.AsList();
                     """
                : $$"""
                        using ({{establishConnection}})
                        {{{sqlTextTransform}}{{dapperParamsSection}}
                            var {{resultVar}} = await {{Variable.Connection.AsVarName()}}.QueryAsync<{{returnType}}>({{sqlQuery}}{{dapperArgs}});
                            return {{resultVar}}.AsList();
                        }
                     """;
        }

        string GetAsDriver()
        {
            var createSqlCommand = dbDriver.CreateSqlCommand(sqlTextTransform != string.Empty ? Variable.TransformedSql.AsVarName() : queryTextConstant);
            var commandParameters = CommonGen.AddParametersToCommand(query.Params);
            var initDataReader = CommonGen.InitDataReader();
            var awaitReaderRow = CommonGen.AwaitReaderRow();
            var dataclassInit = CommonGen.InstantiateDataclass(query.Columns.ToArray(), returnInterface);
            var readWhileExists = $$"""
                                    while ({{awaitReaderRow}})
                                    {
                                        {{resultVar}}.Add({{dataclassInit}});
                                    }
                                    """;
            return dbDriver.Options.ExternalConnection
                ? $$"""
                    using ({{createSqlCommand}})
                    {
                        {{commandParameters}}
                        using ({{initDataReader}})
                        {
                            var {{resultVar}} = new List<{{returnInterface}}>();
                            {{readWhileExists}}
                            return {{resultVar}};
                        }
                    }
                    """
                : $$"""
                    using ({{establishConnection}})
                    {
                        {{connectionOpen.AppendSemicolonUnlessEmpty()}}{{sqlTextTransform}}
                        using ({{createSqlCommand}})
                        {
                            {{commandParameters}}
                            using ({{initDataReader}})
                            {
                                var {{resultVar}} = new List<{{returnInterface}}>();
                                {{readWhileExists}}
                                return {{resultVar}};
                            }
                        }
                    }
                    """;
        }
    }
}
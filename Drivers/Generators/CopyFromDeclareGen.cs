using Microsoft.CodeAnalysis.CSharp.Syntax;
using Plugin;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace SqlcGenCsharp.Drivers.Generators;

public class CopyFromDeclareGen(DbDriver dbDriver)
{
    public MemberDeclarationSyntax Generate(string queryTextConstant, string argInterface, Query query)
    {
        string staticKeyword = dbDriver.Options.ExternalConnection ? "static" : string.Empty;
        return ParseMemberDeclaration($$"""
            public {{staticKeyword}} async Task {{query.Name}}(List<{{argInterface}}> args)
            {
                {{((ICopyFrom)dbDriver).GetCopyFromImpl(query, queryTextConstant)}}
            }
            """)!;
    }
}
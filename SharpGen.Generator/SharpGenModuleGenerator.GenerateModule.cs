using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace SharpGen.Generator;

public sealed partial class SharpGenModuleGenerator
{
    private static void GenerateModule(GeneratorExecutionContext context)
    {
        var walker = (SemanticTreeWalker) context.SyntaxContextReceiver!;

        List<GuidJob> guidJobs = new();
        List<VtblJob> vtblJobs = new();

        void HandleGuid(ITypeSymbol symbol, Guid parsedGuid) =>
            guidJobs.Add(new GuidJob(symbol, parsedGuid, Utilities.GetGuidParameters(parsedGuid)));

        void HandleVtbl(ITypeSymbol symbol, ITypeSymbol vtblTypeSymbol)
        {
            bool TypePredicate(INamedTypeSymbol x) =>
                x.AllInterfaces.Any(y => y.ToDisplayString() == CallbackableInterfaceName);

            Debug.Assert(!vtblJobs.Any(x => SymbolEqualityComparer.Default.Equals(x.InterfaceType, symbol)));

            vtblJobs.Add(
                new VtblJob(symbol, vtblTypeSymbol)
                {
                    CallbackInterfaces = symbol.AllInterfaces.Where(TypePredicate).Reverse().ToArray()
                }
            );
        }

        if (context.CancellationToken.IsCancellationRequested)
            return;

        foreach (var symbol in walker.QueuedJobs)
        {
            if (symbol.GetGuidAttribute() is { } guidAttribute)
                HandleGuid(symbol, guidAttribute);

            if (context.CancellationToken.IsCancellationRequested)
                return;

            if (symbol.GetVtblAttribute() is { } vtblAttribute)
                HandleVtbl(symbol, vtblAttribute);
        }

        if (context.CancellationToken.IsCancellationRequested)
            return;

        StatementSyntaxList body = new();

        StatementSyntax GuidTransform(GuidJob job) =>
            context.Compilation.IsSymbolAccessibleWithin(job.Type, context.Compilation.Assembly)
                ? ExpressionStatement(
                    AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        StorageField(ParseName(job.Type.ToDisplayString()), IdentifierName("Guid")),
                        ObjectCreationExpression(ParseTypeName("System.Guid"), job.GuidSyntax, default)
                    )
                )
                : Block()
                   .WithLeadingTrivia(
                        Comment(
                            $"// Type {job.Type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)} is inaccessible, but has GUID {job.Guid}"
                        )
                    );

        body.AddRange(guidJobs, GuidTransform);

        if (context.CancellationToken.IsCancellationRequested)
            return;

        if (vtblJobs.Count > 0)
        {
            body.AddRange(
                vtblJobs,
                job => ExpressionStatement(
                    AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        StorageField(ParseName(job.InterfaceType.ToDisplayString()), IdentifierName("SourceVtbl")),
                        MemberAccessExpression(
                            SyntaxKind.SimpleMemberAccessExpression,
                            ParseTypeName(job.VtblType.ToDisplayString()),
                            IdentifierName("Vtbl")
                        )
                    )
                )
            );

            body.Add(
                LocalDeclarationStatement(
                    VariableDeclaration(
                        ParseName("SharpGen.Runtime.TypeDataRegistrationHelper"),
                        SingletonSeparatedList(
                            VariableDeclarator(Identifier("helper"))
                               .WithInitializer(EqualsValueClause(ImplicitObjectCreationExpression()))
                        )
                    )
                )
            );

            if (context.CancellationToken.IsCancellationRequested)
                return;

            var helper = IdentifierName("helper");
            var add = Identifier("Add");
            foreach (var vtblJob in vtblJobs)
            {
                ExpressionStatementSyntax AddVtbl(ITypeSymbol typeSymbol)
                {
                    var name = typeSymbol.ToDisplayString();

                    var localVtbl = vtblJobs.ExclusiveOrDefault(
                        x => SymbolEqualityComparer.Default.Equals(x.InterfaceType, typeSymbol)
                    )?.VtblType;

                    if (localVtbl is null &&
                        context.Compilation.GetTypeByMetadataName(name).GetVtblAttribute() is { } vtblTypeSymbol &&
                        context.Compilation.IsSymbolAccessibleWithin(vtblTypeSymbol, context.Compilation.Assembly))
                        localVtbl = vtblTypeSymbol;

                    return ExpressionStatement(
                        InvocationExpression(
                            MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                helper,
                                localVtbl is not null
                                    ? IdentifierName(add)
                                    : GenericName(
                                        add, TypeArgumentList(SingletonSeparatedList(ParseTypeName(name)))
                                    )
                            ),
                            ArgumentList(
                                localVtbl is not null
                                    ? SingletonSeparatedList(
                                        Argument(
                                            MemberAccessExpression(
                                                SyntaxKind.SimpleMemberAccessExpression,
                                                ParseTypeName(localVtbl.ToDisplayString()),
                                                IdentifierName("Vtbl")
                                            )
                                        )
                                    )
                                    : default
                            )
                        )
                    );
                }

                body.AddRange(vtblJob.CallbackInterfaces.Append(vtblJob.InterfaceType), AddVtbl);

                if (context.CancellationToken.IsCancellationRequested)
                    return;

                body.Add(
                    ExpressionStatement(
                        InvocationExpression(
                            MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                helper,
                                GenericName(
                                    Identifier("Register"),
                                    TypeArgumentList(
                                        SingletonSeparatedList(ParseTypeName(vtblJob.InterfaceType.ToDisplayString()))
                                    )
                                )
                            )
                        )
                    )
                );
            }
        }

        if (body.Count == 0)
            return;

        if (context.CancellationToken.IsCancellationRequested)
            return;

        var staticModifier = TokenList(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword));
        var clazz = ClassDeclaration("SharpGenModuleInitializer")
                   .WithModifiers(staticModifier)
                   .AddModifiers(Token(SyntaxKind.UnsafeKeyword))
                   .WithMembers(
                        SingletonList<MemberDeclarationSyntax>(
                            MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "Initialize")
                               .WithModifiers(staticModifier)
                               .AddAttributeLists(ModuleInitializerAttributeList)
                               .WithBody(body.ToBlock())
                        )
                    );

        context.AddSource(
            "SharpGen.Module.g.cs",
            SourceText.From(GenerateCompilationUnit(clazz).ToString(), Encoding.UTF8)
        );
    }

    private sealed record GuidJob(ITypeSymbol Type, Guid Guid, ArgumentListSyntax GuidSyntax)
    {
        public readonly ITypeSymbol Type = Type ?? throw new ArgumentNullException(nameof(Type));
        public readonly ArgumentListSyntax GuidSyntax = GuidSyntax ?? throw new ArgumentNullException(nameof(GuidSyntax));
    }

    private sealed class VtblJob
    {
        public readonly ITypeSymbol InterfaceType;
        public readonly ITypeSymbol VtblType;
        public INamedTypeSymbol[] CallbackInterfaces { get; init; }

        public VtblJob(ITypeSymbol interfaceType, ITypeSymbol vtblType)
        {
            InterfaceType = interfaceType ?? throw new ArgumentNullException(nameof(interfaceType));
            VtblType = vtblType ?? throw new ArgumentNullException(nameof(vtblType));
        }
    }

    private static ExpressionSyntax StorageField(TypeSyntax typeName, SimpleNameSyntax name) =>
        MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression, TypeDataStorage,
                GenericName(Identifier("Storage"), TypeArgumentList(SingletonSeparatedList(typeName)))
            ),
            name
        );
}
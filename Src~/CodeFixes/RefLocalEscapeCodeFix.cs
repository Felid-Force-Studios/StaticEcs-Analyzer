using System.Collections.Immutable;
using System.Composition;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FFS.Libraries.StaticEcs.Analyzers.CodeFixes {
    /// <summary>
    /// CodeFixes for FFSECS0012 (ref-local passed as value argument).
    ///
    ///   • "Pass with 'ref'"        → adds the 'ref' keyword to the argument syntax. Only offered when
    ///                                  the target parameter accepts 'ref' or 'in'.
    ///   • "Take an explicit copy"  → inserts 'var &lt;name&gt;Copy = &lt;name&gt;;' before the enclosing statement
    ///                                  and replaces the argument with the new local. Documents snapshot intent
    ///                                  the same way you would when capturing a ref-local for a lambda.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RefLocalEscapeCodeFix)), Shared]
    public sealed class RefLocalEscapeCodeFix : CodeFixProvider {
        public override ImmutableArray<string> FixableDiagnosticIds { get; } =
            ImmutableArray.Create(FFSECSIds.FFSECS0012);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null) return;

            foreach (var diagnostic in context.Diagnostics) {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

                var argument = node.FirstAncestorOrSelf<ArgumentSyntax>();
                if (argument is null) continue;
                if (argument.Expression is not IdentifierNameSyntax identifier) continue;

                var passKind = TryGetParameterRefKind(semanticModel, argument, context.CancellationToken);
                var localIsReadonlyRef = IsRefReadonlyLocal(semanticModel, identifier, context.CancellationToken);
                // 'ref readonly' local can only be re-passed as 'in'; passing as 'ref' is CS8329.
                if (passKind == RefKind.Ref && !localIsReadonlyRef) {
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            title: "Pass with 'ref'",
                            createChangedDocument: ct => AddRefKindToArgumentAsync(context.Document, argument, SyntaxKind.RefKeyword, ct),
                            equivalenceKey: "FFSECS0012_PassByRef_ref"),
                        diagnostic);
                } else if (passKind == RefKind.In) {
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            title: "Pass with 'in'",
                            createChangedDocument: ct => AddRefKindToArgumentAsync(context.Document, argument, SyntaxKind.InKeyword, ct),
                            equivalenceKey: "FFSECS0012_PassByRef_in"),
                        diagnostic);
                }

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Take an explicit copy before the call",
                        createChangedDocument: ct => IntroduceCopyBeforeStatementAsync(context.Document, argument, identifier, ct),
                        equivalenceKey: "FFSECS0012_ExplicitCopy"),
                    diagnostic);
            }
        }

        private static bool IsRefReadonlyLocal(SemanticModel semanticModel, IdentifierNameSyntax identifier, CancellationToken cancellationToken) {
            var symbolInfo = semanticModel.GetSymbolInfo(identifier, cancellationToken);
            return symbolInfo.Symbol is ILocalSymbol local && local.IsRef && local.RefKind == RefKind.RefReadOnly;
        }

        private static RefKind? TryGetParameterRefKind(SemanticModel semanticModel, ArgumentSyntax argument, CancellationToken cancellationToken) {
            if (argument.Parent is not BaseArgumentListSyntax argumentList) return null;
            if (argumentList.Parent is null) return null;
            var symbolInfo = semanticModel.GetSymbolInfo(argumentList.Parent, cancellationToken);
            var candidate = symbolInfo.Symbol ?? (symbolInfo.CandidateSymbols.Length == 1 ? symbolInfo.CandidateSymbols[0] : null);
            if (candidate is not IMethodSymbol method) return null;

            var index = argumentList.Arguments.IndexOf(argument);
            if (index < 0 || index >= method.Parameters.Length) return null;
            return method.Parameters[index].RefKind;
        }

        private static async Task<Document> AddRefKindToArgumentAsync(Document document, ArgumentSyntax argument, SyntaxKind refKindKeyword, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;

            var token = SyntaxFactory.Token(refKindKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var newArgument = argument.WithRefKindKeyword(token)
                                      .WithLeadingTrivia(argument.GetLeadingTrivia());

            return document.WithSyntaxRoot(root.ReplaceNode(argument, newArgument));
        }

        private static async Task<Document> IntroduceCopyBeforeStatementAsync(Document document, ArgumentSyntax argument, IdentifierNameSyntax identifier, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;

            var enclosingStatement = argument.FirstAncestorOrSelf<StatementSyntax>();
            if (enclosingStatement is null || enclosingStatement.Parent is not BlockSyntax block) {
                // Top-level statements / expression-bodied members not supported by this fix; user falls back to manual edit or pragma.
                return document;
            }

            var copyName = identifier.Identifier.ValueText + "Copy";
            var leadingTrivia = enclosingStatement.GetLeadingTrivia();

            // 'var <name>Copy = <name>;'
            var copyDeclaration = SyntaxFactory.LocalDeclarationStatement(
                SyntaxFactory.VariableDeclaration(
                    SyntaxFactory.IdentifierName("var").WithTrailingTrivia(SyntaxFactory.Space),
                    SyntaxFactory.SingletonSeparatedList(
                        SyntaxFactory.VariableDeclarator(SyntaxFactory.Identifier(copyName))
                                     .WithInitializer(SyntaxFactory.EqualsValueClause(
                                         SyntaxFactory.Token(SyntaxKind.EqualsToken)
                                                      .WithLeadingTrivia(SyntaxFactory.Space)
                                                      .WithTrailingTrivia(SyntaxFactory.Space),
                                         SyntaxFactory.IdentifierName(identifier.Identifier.ValueText))))))
                .WithLeadingTrivia(leadingTrivia)
                .WithTrailingTrivia(SyntaxFactory.ElasticCarriageReturnLineFeed);

            var newArgument = argument.WithExpression(SyntaxFactory.IdentifierName(copyName));
            var newStatement = enclosingStatement.ReplaceNode(argument, newArgument);

            var blockStatements = block.Statements;
            var statementIndex = blockStatements.IndexOf(enclosingStatement);
            if (statementIndex < 0) return document;

            var newStatements = blockStatements.Replace(enclosingStatement, newStatement)
                                                .Insert(statementIndex, copyDeclaration);
            var newBlock = block.WithStatements(newStatements);

            return document.WithSyntaxRoot(root.ReplaceNode(block, newBlock));
        }
    }
}

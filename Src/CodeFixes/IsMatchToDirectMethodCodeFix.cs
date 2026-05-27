using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace FFS.Libraries.StaticEcs.Analyzers.CodeFixes {
    /// <summary>
    /// CodeFix for FFSECS0032 — rewrites <c>entity.IsMatch&lt;TFilter&lt;A, B&gt;&gt;()</c> as
    /// <c>entity.{Direct}&lt;A, B&gt;()</c>, optionally wrapping in <c>!</c> for <c>None*</c> filters,
    /// and collapsing <c>!!</c> when the original was already negated.
    ///
    /// The new method name and negation flag are passed from the analyzer via diagnostic properties —
    /// this keeps the codefix project free of the symbol resolution layer (which lives in Src/).
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(IsMatchToDirectMethodCodeFix)), Shared]
    public sealed class IsMatchToDirectMethodCodeFix : CodeFixProvider {
        public const string MethodNameProperty = "MethodName";
        public const string NegateProperty = "Negate";

        public override ImmutableArray<string> FixableDiagnosticIds { get; } =
            ImmutableArray.Create(FFSECSIds.FFSECS0032);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null) return;

            foreach (var diagnostic in context.Diagnostics) {
                if (!diagnostic.Properties.TryGetValue(MethodNameProperty, out var methodName) || string.IsNullOrEmpty(methodName)) continue;
                var negate = diagnostic.Properties.TryGetValue(NegateProperty, out var negStr) && negStr == "true";

                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                var invocation = node?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                if (invocation is null) continue;

                if (semanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol method) continue;
                if (method.TypeArguments.Length != 1) continue;
                if (method.TypeArguments[0] is not INamedTypeSymbol filterType) continue;

                var title = "Convert to '" + (negate ? "!" : string.Empty) + methodName + "<...>()'";

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: title,
                        createChangedDocument: ct => ApplyFixAsync(context.Document, invocation, methodName, negate, filterType, ct),
                        equivalenceKey: "FFSECS0032_" + methodName + (negate ? "_neg" : string.Empty)),
                    diagnostic);
            }
        }

        private static async Task<Document> ApplyFixAsync(Document document, InvocationExpressionSyntax invocation, string methodName, bool negate, INamedTypeSymbol filterType, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;

            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return document;

            var newGenericArgs = SyntaxFactory.SeparatedList(
                filterType.TypeArguments.Select(t => SyntaxFactory.ParseTypeName(t.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat))));
            var newName = SyntaxFactory.GenericName(
                SyntaxFactory.Identifier(methodName),
                SyntaxFactory.TypeArgumentList(newGenericArgs));

            var newMemberAccess = memberAccess.WithName(newName);
            var newInvocation = invocation
                .WithExpression(newMemberAccess)
                .WithArgumentList(SyntaxFactory.ArgumentList());

            ExpressionSyntax replacementExpr = newInvocation;
            SyntaxNode nodeToReplace = invocation;

            if (negate) {
                if (invocation.Parent is PrefixUnaryExpressionSyntax existingBang && existingBang.IsKind(SyntaxKind.LogicalNotExpression)) {
                    // Collapse !!call → call.
                    nodeToReplace = existingBang;
                    replacementExpr = newInvocation
                        .WithLeadingTrivia(existingBang.GetLeadingTrivia())
                        .WithTrailingTrivia(existingBang.GetTrailingTrivia());
                }
                else {
                    replacementExpr = SyntaxFactory.PrefixUnaryExpression(SyntaxKind.LogicalNotExpression, newInvocation)
                        .WithLeadingTrivia(invocation.GetLeadingTrivia())
                        .WithTrailingTrivia(invocation.GetTrailingTrivia());
                }
            }

            return document.WithSyntaxRoot(root.ReplaceNode(nodeToReplace, replacementExpr));
        }
    }
}

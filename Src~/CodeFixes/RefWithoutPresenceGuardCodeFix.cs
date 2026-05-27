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
    /// CodeFix for FFSECS0042 — wraps the Ref/Mut/Read invocation in a postfix null-forgiving
    /// operator: <c>entity.Ref&lt;T&gt;()</c> → <c>entity.Ref&lt;T&gt;()!</c>. C# preserves the
    /// ref-return category through <c>!</c>, so the fix is safe in every usage context (ref-local
    /// init, ref argument, ref return, value context).
    ///
    /// Skip: if the invocation is already wrapped in <c>!</c> (defensive; the analyzer also won't
    /// report in that case so the codefix wouldn't be invoked, but the guard protects against stale
    /// diagnostics during re-analysis).
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RefWithoutPresenceGuardCodeFix)), Shared]
    public sealed class RefWithoutPresenceGuardCodeFix : CodeFixProvider {
        public override ImmutableArray<string> FixableDiagnosticIds { get; } =
            ImmutableArray.Create(FFSECSIds.FFSECS0042);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null) return;

            foreach (var diagnostic in context.Diagnostics) {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                var invocation = node?.FirstAncestorOrSelf<InvocationExpressionSyntax>();
                if (invocation is null) continue;
                if (invocation.Parent is PostfixUnaryExpressionSyntax existing
                    && existing.IsKind(SyntaxKind.SuppressNullableWarningExpression)) continue;

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Suppress FFSECS0042 with '!' after the call",
                        createChangedDocument: ct => ApplyFixAsync(context.Document, invocation, ct),
                        equivalenceKey: "FFSECS0042_suppress_call"),
                    diagnostic);
            }
        }

        private static async Task<Document> ApplyFixAsync(Document document, InvocationExpressionSyntax invocation, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;

            var leading = invocation.GetLeadingTrivia();
            var trailing = invocation.GetTrailingTrivia();
            var bare = invocation.WithoutTrivia();
            var suppressed = SyntaxFactory
                .PostfixUnaryExpression(SyntaxKind.SuppressNullableWarningExpression, bare)
                .WithLeadingTrivia(leading)
                .WithTrailingTrivia(trailing);

            return document.WithSyntaxRoot(root.ReplaceNode(invocation, suppressed));
        }
    }
}

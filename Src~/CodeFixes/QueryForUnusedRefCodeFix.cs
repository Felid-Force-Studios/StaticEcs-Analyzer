using System.Collections.Generic;
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
    /// CodeFix for FFSECS0030 — replace a never-mutated <c>ref T</c> lambda parameter with <c>in T</c>
    /// and reorder the whole parameter list so it matches the API convention required by
    /// <c>WorldQuery.For(...)</c> overloads: value parameters (incl. <c>W.Entity</c>) first, then all
    /// <c>ref T</c> parameters, then all <c>in T</c> parameters. The delegates don't have overloads
    /// with interleaved <c>ref</c>/<c>in</c>, so the reorder is necessary for the fixed code to compile.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(QueryForUnusedRefCodeFix)), Shared]
    public sealed class QueryForUnusedRefCodeFix : CodeFixProvider {
        public override ImmutableArray<string> FixableDiagnosticIds { get; } =
            ImmutableArray.Create(FFSECSIds.FFSECS0030);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null) return;

            foreach (var diagnostic in context.Diagnostics) {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                var parameter = node?.FirstAncestorOrSelf<ParameterSyntax>();
                if (parameter is null) continue;
                if (!parameter.Modifiers.Any(SyntaxKind.RefKeyword)) continue;
                if (parameter.Parent is not ParameterListSyntax) continue;

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Change 'ref' to 'in' and reorder",
                        createChangedDocument: ct => ReplaceRefWithInAsync(context.Document, parameter, ct),
                        equivalenceKey: "FFSECS0030_RefToIn"),
                    diagnostic);
            }
        }

        private static async Task<Document> ReplaceRefWithInAsync(Document document, ParameterSyntax parameter, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;
            if (parameter.Parent is not ParameterListSyntax parameterList) return document;

            // Build the flipped parameter: ref → in. Strip leading trivia we accumulated for the old
            // position; the regrouping below assigns a fresh single-space leading trivia to every
            // non-first parameter so the comma/space layout is consistent.
            var refToken = parameter.Modifiers.First(t => t.IsKind(SyntaxKind.RefKeyword));
            var inToken = SyntaxFactory.Token(SyntaxKind.InKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var flippedModifiers = parameter.Modifiers.Replace(refToken, inToken);
            var flippedParameter = parameter.WithModifiers(flippedModifiers);

            // Regroup: value (no ref/in/out modifier) → ref → in. Preserve relative order in each group;
            // append the flipped parameter to the END of the in-group.
            var valueGroup = new List<ParameterSyntax>();
            var refGroup = new List<ParameterSyntax>();
            var inGroup = new List<ParameterSyntax>();

            foreach (var p in parameterList.Parameters) {
                if (p == parameter) continue; // handled separately as `flippedParameter`
                if (p.Modifiers.Any(SyntaxKind.RefKeyword)) refGroup.Add(p);
                else if (p.Modifiers.Any(SyntaxKind.InKeyword)) inGroup.Add(p);
                else valueGroup.Add(p);
            }
            inGroup.Add(flippedParameter);

            // Normalize leading trivia: first param gets none, others get a single space.
            // Roslyn inserts ", " between SeparatedList items automatically when we use the default
            // comma generation, but we still want the leading-trivia of each parameter to be a space
            // (so the rendered text is "(a, b, c)" rather than "(a,b,c)").
            var ordered = new List<ParameterSyntax>(valueGroup.Count + refGroup.Count + inGroup.Count);
            ordered.AddRange(valueGroup);
            ordered.AddRange(refGroup);
            ordered.AddRange(inGroup);

            var normalized = new List<ParameterSyntax>(ordered.Count);
            for (var i = 0; i < ordered.Count; i++) {
                var p = ordered[i];
                normalized.Add(i == 0
                    ? p.WithLeadingTrivia(SyntaxFactory.TriviaList())
                    : p.WithLeadingTrivia(SyntaxFactory.Space));
            }

            var newParameterList = parameterList.WithParameters(SyntaxFactory.SeparatedList(normalized));
            return document.WithSyntaxRoot(root.ReplaceNode(parameterList, newParameterList));
        }
    }
}

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
    /// CodeFix for FFSECS0020 — any ECS marker interface implemented by class. Replaces the
    /// <c>class</c> keyword with <c>struct</c>. Bails out when the class extends a non-object
    /// base type (structs cannot inherit) or is <c>static</c> — the user must restructure manually.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(EcsMarkerInterfaceMustBeStructCodeFix)), Shared]
    public sealed class EcsMarkerInterfaceMustBeStructCodeFix : CodeFixProvider {
        public override ImmutableArray<string> FixableDiagnosticIds { get; } =
            ImmutableArray.Create(FFSECSIds.FFSECS0020);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null || semanticModel is null) return;

            foreach (var diagnostic in context.Diagnostics) {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                var classDecl = node?.FirstAncestorOrSelf<ClassDeclarationSyntax>();
                if (classDecl is null) continue;
                // 'static class' has no struct equivalent.
                if (classDecl.Modifiers.Any(static m => m.IsKind(SyntaxKind.StaticKeyword))) continue;
                // Structs cannot inherit a base class; bail if there is one.
                if (HasExplicitBaseClass(semanticModel, classDecl, context.CancellationToken)) continue;

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Convert to 'struct'",
                        createChangedDocument: ct => ConvertClassToStructAsync(context.Document, classDecl, ct),
                        equivalenceKey: diagnostic.Id + "_ConvertToStruct"),
                    diagnostic);
            }
        }

        private static bool HasExplicitBaseClass(SemanticModel semanticModel, ClassDeclarationSyntax classDecl, CancellationToken cancellationToken) {
            if (classDecl.BaseList is null || classDecl.BaseList.Types.Count == 0) return false;
            var symbol = semanticModel.GetDeclaredSymbol(classDecl, cancellationToken);
            if (symbol is null) return false;
            var baseType = symbol.BaseType;
            // System.Object / null ⇒ no explicit base class.
            return baseType is not null && baseType.SpecialType != SpecialType.System_Object;
        }

        private static async Task<Document> ConvertClassToStructAsync(Document document, ClassDeclarationSyntax classDecl, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;

            var classKeyword = classDecl.Keyword;
            var structKeyword = SyntaxFactory.Token(classKeyword.LeadingTrivia, SyntaxKind.StructKeyword, classKeyword.TrailingTrivia);

            // 'sealed' / 'abstract' aren't allowed on structs; strip them to avoid CS0106.
            var newModifiers = SyntaxFactory.TokenList(
                classDecl.Modifiers.Where(m => !m.IsKind(SyntaxKind.SealedKeyword) && !m.IsKind(SyntaxKind.AbstractKeyword)));

            var structDecl = SyntaxFactory.StructDeclaration(
                    attributeLists: classDecl.AttributeLists,
                    modifiers: newModifiers,
                    keyword: structKeyword,
                    identifier: classDecl.Identifier,
                    typeParameterList: classDecl.TypeParameterList,
                    baseList: classDecl.BaseList,
                    constraintClauses: classDecl.ConstraintClauses,
                    openBraceToken: classDecl.OpenBraceToken,
                    members: classDecl.Members,
                    closeBraceToken: classDecl.CloseBraceToken,
                    semicolonToken: classDecl.SemicolonToken);

            return document.WithSyntaxRoot(root.ReplaceNode(classDecl, structDecl));
        }
    }
}

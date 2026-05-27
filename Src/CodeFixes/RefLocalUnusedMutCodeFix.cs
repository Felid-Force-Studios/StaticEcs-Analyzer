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
    /// CodeFix for FFSECS0013. Handles two shapes:
    /// <para>1) Ref-local binding: <c>ref var x = ref entity.Ref&lt;T&gt;()</c> → either copy
    /// (<c>var x = entity.Read&lt;T&gt;()</c>) for small payloads, or <c>ref readonly var x = ref entity.Read&lt;T&gt;()</c>.</para>
    /// <para>2) Inline read: <c>entity.Ref&lt;T&gt;().Field</c> → <c>entity.Read&lt;T&gt;().Field</c>.
    /// Member name is mapped to the read-only sibling per <see cref="TryResolveSiblingName"/>; for
    /// indexer accesses (<c>multi[i]</c>) the call shape changes to <c>multi.Get(i)</c>.</para>
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RefLocalUnusedMutCodeFix)), Shared]
    public sealed class RefLocalUnusedMutCodeFix : CodeFixProvider {
        public override ImmutableArray<string> FixableDiagnosticIds { get; } =
            ImmutableArray.Create(FFSECSIds.FFSECS0013);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null) return;
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);

            foreach (var diagnostic in context.Diagnostics) {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

                // Form 1: ref-local binding (the diagnostic location is the local-reference).
                if (TryRegisterRefLocalFix(context, diagnostic, node, semanticModel)) continue;

                // Form 2: inline read of a ref-returning member. Diagnostic location is the
                // invocation / member-access / element-access node itself.
                TryRegisterInlineFix(context, diagnostic, node, semanticModel);
            }
        }

        // ---------- Form 1: ref-local binding ----------

        private static bool TryRegisterRefLocalFix(CodeFixContext context, Diagnostic diagnostic, SyntaxNode node, SemanticModel semanticModel) {
            var declarator = node.FirstAncestorOrSelf<VariableDeclaratorSyntax>();
            if (declarator?.Initializer?.Value is not RefExpressionSyntax refExpr) return false;
            if (declarator.Parent is not VariableDeclarationSyntax declaration) return false;
            if (declaration.Variables.Count != 1) return false; // multi-declarator bailout
            if (declaration.Type is not RefTypeSyntax refType) return false;

            var inner = refExpr.Expression;
            var siblingName = TryResolveSiblingName(inner, semanticModel, context.CancellationToken, out var payloadIsSmall);
            if (siblingName is null) return false;

            if (payloadIsSmall) {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: $"Switch to '{siblingName}' (copy snapshot)",
                        createChangedDocument: ct => SwitchToReadAsCopyAsync(context.Document, declaration, declarator, refType, inner, siblingName, ct),
                        equivalenceKey: FFSECSIds.FFSECS0013 + "_SwitchToReadCopy"),
                    diagnostic);
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: $"Switch to '{siblingName}' (bound by 'ref readonly')",
                        createChangedDocument: ct => SwitchToReadReadonlyBoundAsync(context.Document, declaration, declarator, refType, inner, siblingName, ct),
                        equivalenceKey: FFSECSIds.FFSECS0013 + "_SwitchToReadRefReadonly"),
                    diagnostic);
            } else {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: $"Switch to '{siblingName}' (bound by 'ref readonly')",
                        createChangedDocument: ct => SwitchToReadReadonlyBoundAsync(context.Document, declaration, declarator, refType, inner, siblingName, ct),
                        equivalenceKey: FFSECSIds.FFSECS0013 + "_SwitchToReadRefReadonly"),
                    diagnostic);
            }
            return true;
        }

        private static async Task<Document> SwitchToReadAsCopyAsync(Document document, VariableDeclarationSyntax declaration, VariableDeclaratorSyntax declarator, RefTypeSyntax refType, ExpressionSyntax inner, string siblingName, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;

            var renamed = TryRewriteToSibling(inner, siblingName);
            if (renamed is null) return document;

            var newInitializer = declarator.Initializer.WithValue(renamed.WithTriviaFrom(declarator.Initializer.Value));
            var newDeclarator = declarator.WithInitializer(newInitializer);

            // Type: ref [readonly] T → T (drop the RefType wrapper, keep the underlying type).
            var newType = refType.Type.WithTriviaFrom(refType);

            var newVariables = declaration.Variables.Replace(declarator, newDeclarator);
            var newDeclaration = declaration.WithType(newType).WithVariables(newVariables);
            return document.WithSyntaxRoot(root.ReplaceNode(declaration, newDeclaration));
        }

        private static async Task<Document> SwitchToReadReadonlyBoundAsync(Document document, VariableDeclarationSyntax declaration, VariableDeclaratorSyntax declarator, RefTypeSyntax refType, ExpressionSyntax inner, string siblingName, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;

            var renamed = TryRewriteToSibling(inner, siblingName);
            if (renamed is null) return document;

            var refKeyword = SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var newRefValue = SyntaxFactory.RefExpression(refKeyword, renamed)
                                            .WithTriviaFrom(declarator.Initializer.Value);
            var newInitializer = declarator.Initializer.WithValue(newRefValue);
            var newDeclarator = declarator.WithInitializer(newInitializer);

            var refTypeKeyword = SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var readonlyKeyword = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var newType = SyntaxFactory.RefType(refTypeKeyword, readonlyKeyword, refType.Type.WithoutLeadingTrivia())
                                        .WithTriviaFrom(refType);

            var newVariables = declaration.Variables.Replace(declarator, newDeclarator);
            var newDeclaration = declaration.WithType(newType).WithVariables(newVariables);
            return document.WithSyntaxRoot(root.ReplaceNode(declaration, newDeclaration));
        }

        // ---------- Form 2: inline read ----------

        private static void TryRegisterInlineFix(CodeFixContext context, Diagnostic diagnostic, SyntaxNode node, SemanticModel semanticModel) {
            var refExpression = FindRefReturningExpression(node);
            if (refExpression is null) return;

            var siblingName = TryResolveSiblingName(refExpression, semanticModel, context.CancellationToken, out _);
            if (siblingName is null) return;

            context.RegisterCodeFix(
                CodeAction.Create(
                    title: $"Switch to '{siblingName}' (read-only access)",
                    createChangedDocument: ct => SwitchInlineToSiblingAsync(context.Document, refExpression, siblingName, ct),
                    equivalenceKey: FFSECSIds.FFSECS0013 + "_SwitchInline"),
                diagnostic);
        }

        private static async Task<Document> SwitchInlineToSiblingAsync(Document document, ExpressionSyntax refExpression, string siblingName, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;

            var rewritten = TryRewriteToSibling(refExpression, siblingName);
            if (rewritten is null) return document;

            return document.WithSyntaxRoot(root.ReplaceNode(refExpression, rewritten.WithTriviaFrom(refExpression)));
        }

        /// <summary>
        /// Locates the ref-returning expression at or around <paramref name="node"/>. The diagnostic
        /// location points at the <c>refOp.Syntax</c>, which is the invocation / member-access /
        /// element-access node itself, but the inner-most node found by <c>FindNode</c> can be a
        /// child identifier — climb to the appropriate enclosing expression.
        /// </summary>
        private static ExpressionSyntax FindRefReturningExpression(SyntaxNode node) {
            for (var current = node; current is not null; current = current.Parent) {
                switch (current) {
                    case InvocationExpressionSyntax invoc: return invoc;
                    case ElementAccessExpressionSyntax element: return element;
                    case MemberAccessExpressionSyntax member when member.Parent is not InvocationExpressionSyntax:
                        // Property access like `world.Resource<T>().Value`.
                        return member;
                }
            }
            return null;
        }

        // ---------- Shared helpers ----------

        /// <summary>
        /// Resolves the read-only sibling name for a writable ref-returning expression. Uses the
        /// semantic model to determine the containing type + member name; falls back to <c>null</c>
        /// if the symbol can't be resolved or has no sibling. Also sets <paramref name="payloadIsSmall"/>
        /// when the target is a generic method whose payload is &lt;=8 bytes — used by the ref-local
        /// fix to decide whether to offer the copy variant.
        /// </summary>
        private static string TryResolveSiblingName(ExpressionSyntax expression, SemanticModel semanticModel, CancellationToken cancellationToken, out bool payloadIsSmall) {
            payloadIsSmall = false;
            if (semanticModel is null) return null;
            var symbol = semanticModel.GetSymbolInfo(expression, cancellationToken).Symbol;
            if (symbol is null) return null;

            var siblingName = ResolveSiblingFromSymbol(symbol);
            if (siblingName is null) return null;

            if (symbol is IMethodSymbol method && method.TypeArguments.Length > 0) {
                var payload = method.TypeArguments[0];
                if (payload.IsValueType
                    && StructSizeEstimation.TryEstimateStructSize(payload, out var size)
                    && size <= StructSizeEstimation.SmallStructPayloadByteThreshold) {
                    payloadIsSmall = true;
                }
            }
            return siblingName;
        }

        private static string ResolveSiblingFromSymbol(ISymbol symbol) {
            var owner = symbol.ContainingType?.OriginalDefinition?.Name;
            switch (symbol) {
                case IMethodSymbol method:
                    return (owner, method.Name) switch {
                        ("Entity", "Ref") or ("Entity", "Mut") => "Read",
                        ("Components", "Ref") or ("Components", "Mut") => "Read",
                        ("Multi", "First") => "GetFirst",
                        ("Multi", "Last") => "GetLast",
                        _ => null,
                    };
                case IPropertySymbol prop:
                    if (prop.IsIndexer && owner == "Multi") return "Get";
                    return (owner, prop.Name) switch {
                        ("Resource", "Value") => "ValueRO",
                        ("NamedResource", "Value") => "ValueRO",
                        ("MultiComponentsIterator", "Current") => "CurrentRO",
                        _ => null,
                    };
            }
            return null;
        }

        /// <summary>
        /// Rewrites <paramref name="expression"/> to its read-only sibling form. For invocations
        /// and member-accesses this is a simple identifier rename; for an indexer (<c>multi[i]</c>)
        /// it transforms the shape to <c>multi.Get(i)</c>.
        /// </summary>
        private static ExpressionSyntax TryRewriteToSibling(ExpressionSyntax expression, string siblingName) {
            switch (expression) {
                case InvocationExpressionSyntax invocation when invocation.Expression is MemberAccessExpressionSyntax memberAccess: {
                    var renamed = RenameSimpleName(memberAccess.Name, siblingName);
                    if (renamed is null) return null;
                    return invocation.WithExpression(memberAccess.WithName(renamed));
                }
                case MemberAccessExpressionSyntax memberAccess: {
                    var renamed = RenameSimpleName(memberAccess.Name, siblingName);
                    if (renamed is null) return null;
                    return memberAccess.WithName(renamed);
                }
                case ElementAccessExpressionSyntax elementAccess: {
                    // multi[i] → multi.Get(i)
                    var member = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression,
                        elementAccess.Expression,
                        SyntaxFactory.IdentifierName(siblingName));
                    var argList = SyntaxFactory.ArgumentList(
                        SyntaxFactory.SeparatedList(
                            elementAccess.ArgumentList.Arguments.Select(arg => SyntaxFactory.Argument(arg.Expression))));
                    return SyntaxFactory.InvocationExpression(member, argList);
                }
            }
            return null;
        }

        private static SimpleNameSyntax RenameSimpleName(SimpleNameSyntax name, string newName) {
            return name switch {
                GenericNameSyntax generic => generic.WithIdentifier(SyntaxFactory.Identifier(newName)),
                IdentifierNameSyntax identifier => identifier.WithIdentifier(SyntaxFactory.Identifier(newName)),
                _ => null,
            };
        }
    }
}

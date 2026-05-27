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
    /// CodeFixes for FFSECS0010 (ref T) and FFSECS0011 (ref readonly T).
    ///
    /// FFSECS0010:
    ///   • Local declaration → "Bind with 'ref'"; for Ref/Mut sources also "Switch to Read&lt;T&gt;()" (snapshot).
    ///   • Argument          → "Switch to Read&lt;T&gt;()" for Ref/Mut. 'Add' has side effects, no Switch offered.
    ///
    /// FFSECS0011:
    ///   • Local declaration → "Bind with 'ref readonly'".
    ///   • Argument          → no fix (Read intent is already explicit; user silences via pragma if needed).
    ///
    /// We deliberately do NOT offer an explicit-cast fix: '(T)expr' is a no-op cast that triggers
    /// IDE0004 / ReSharper "Remove redundant cast", creating a fix-then-revert loop.
    /// Users who need "mark Changed without binding" call MarkChanged&lt;T&gt;() manually.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RefReturnNotByRefCodeFix)), Shared]
    public sealed class RefReturnNotByRefCodeFix : CodeFixProvider {
        public override ImmutableArray<string> FixableDiagnosticIds { get; } =
            ImmutableArray.Create(FFSECSIds.FFSECS0010, FFSECSIds.FFSECS0011);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null) return;
            // SemanticModel is needed to peek at the payload T's size and decide between the
            // "plain copy" and "ref readonly" variants of «Switch to Read<T>()» (FFSECS0010 only).
            // It's harmless for the FFSECS0011 path that ignores it.
            var semanticModel = await context.Document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);

            foreach (var diagnostic in context.Diagnostics) {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);

                var isReadonly = diagnostic.Id == FFSECSIds.FFSECS0011;
                var keyPrefix = diagnostic.Id;

                // Order matters: actions are displayed by the IDE in registration order. «Bind with
                // 'ref'» (the primary fix) goes first; «Switch to '*RO'» (snapshot opt-in) is offered
                // as a secondary alternative.
                var enclosingDeclarator = node.FirstAncestorOrSelf<VariableDeclaratorSyntax>();
                if (enclosingDeclarator?.Initializer?.Value is { } initValue && initValue.Span.Contains(node.Span)) {
                    RegisterDeclaratorFixes(context, diagnostic, enclosingDeclarator, initValue, node, isReadonly, keyPrefix, semanticModel);
                } else {
                    var enclosingArgument = node.FirstAncestorOrSelf<ArgumentSyntax>();
                    if (enclosingArgument is { } argument && argument.Expression.Span.Contains(node.Span)) {
                        RegisterArgumentFixes(context, diagnostic, argument, node, isReadonly, keyPrefix);
                    }
                }

                // FFSECS0010 only: explicit RO-sibling swap for Resource/NamedResource/Multi/Iterator
                // members. Registered after the primary fix so it appears below it in the IDE menu.
                if (!isReadonly) {
                    RegisterRoSnapshotFix(context, diagnostic, node, keyPrefix);
                }
            }
        }

        /// <summary>
        /// Offers «Switch to '*RO' (intentional copy)» when the offending ref-returning syntax is one
        /// of the StaticEcs members that has a paired <c>RO</c>-sibling (Resource/NamedResource <c>Value</c>,
        /// Multi <c>First/Last</c>, Multi indexer <c>[i]</c>, Iterator <c>Current</c>). The new member returns
        /// <c>ref readonly T</c> and is intentionally excluded from the analyzer's allow-list — diagnostic
        /// disappears after the swap.
        /// </summary>
        private static void RegisterRoSnapshotFix(CodeFixContext context, Diagnostic diagnostic, SyntaxNode node, string keyPrefix) {
            // Property access (Value / Current) — applies to Resource<T>, NamedResource<T>, MultiComponentsIterator<T>.
            if (node is MemberAccessExpressionSyntax member
                && member.Name is IdentifierNameSyntax idName
                && (idName.Identifier.ValueText == "Value" || idName.Identifier.ValueText == "Current")) {
                var roName = idName.Identifier.ValueText + "RO";
                var replacement = member.WithName(SyntaxFactory.IdentifierName(roName).WithTriviaFrom(member.Name));
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: $"Switch to '{roName}' (intentional copy)",
                        createChangedDocument: ct => ReplaceNodeAsync(context.Document, member, replacement, ct),
                        equivalenceKey: keyPrefix + "_SwitchToRO_" + roName),
                    diagnostic);
                return;
            }

            // Method invocation (First() / Last()) — applies to Multi<T>.
            if (node is InvocationExpressionSyntax invocation
                && invocation.Expression is MemberAccessExpressionSyntax invMember
                && invMember.Name is IdentifierNameSyntax invName
                && (invName.Identifier.ValueText == "First" || invName.Identifier.ValueText == "Last")) {
                var newName = "Get" + invName.Identifier.ValueText; // First → GetFirst, Last → GetLast
                var renamed = invocation.WithExpression(invMember.WithName(SyntaxFactory.IdentifierName(newName).WithTriviaFrom(invMember.Name)));
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: $"Switch to '{newName}()' (intentional copy)",
                        createChangedDocument: ct => ReplaceNodeAsync(context.Document, invocation, renamed, ct),
                        equivalenceKey: keyPrefix + "_SwitchToRO_" + newName),
                    diagnostic);
                return;
            }

            // Element access (expr[idx]) — applies to Multi<T> indexer. Rewrites to expr.Get(idx).
            if (node is ElementAccessExpressionSyntax elem) {
                var memberAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    elem.Expression,
                    SyntaxFactory.IdentifierName("Get"));
                var newCall = SyntaxFactory.InvocationExpression(memberAccess, SyntaxFactory.ArgumentList(elem.ArgumentList.Arguments))
                                           .WithTriviaFrom(elem);
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Switch to 'Get(idx)' (intentional copy)",
                        createChangedDocument: ct => ReplaceNodeAsync(context.Document, elem, newCall, ct),
                        equivalenceKey: keyPrefix + "_SwitchToRO_Get"),
                    diagnostic);
            }
        }

        private static async Task<Document> ReplaceNodeAsync(Document document, SyntaxNode oldNode, SyntaxNode newNode, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;
            return document.WithSyntaxRoot(root.ReplaceNode(oldNode, newNode));
        }

        private static void RegisterDeclaratorFixes(CodeFixContext context, Diagnostic diagnostic, VariableDeclaratorSyntax declarator, ExpressionSyntax initValue, SyntaxNode diagnosticNode, bool isReadonly, string keyPrefix, SemanticModel semanticModel) {
            var bindTitle = isReadonly ? "Bind with 'ref readonly'" : "Bind with 'ref'";
            context.RegisterCodeFix(
                CodeAction.Create(
                    title: bindTitle,
                    createChangedDocument: ct => AddRefToLocalAsync(context.Document, declarator, isReadonly, ct),
                    equivalenceKey: keyPrefix + "_AddRef"),
                diagnostic);

            if (isReadonly) return;
            // Diagnostic node IS the offending Ref/Mut/Add invocation (analyzer reports on it directly).
            // Chain case (e.g. `entity.Ref<T>().Val`): node is `entity.Ref<T>()`, not the whole initValue.
            if (diagnosticNode is not InvocationExpressionSyntax invocation
                || !TryGetMemberMethodName(invocation, out var methodName)) {
                return;
            }

            // 'Ref' / 'Mut' both have a snapshot-equivalent: Read<T>(). For 'Mut' this silently
            // drops change-tracking — that's an intentional choice the user makes by accepting the fix.
            // Users who need "mark only without binding" can call MarkChanged<T>() manually.
            // 'Add' has side effects (adds component) → no Switch fix.
            if (methodName is not ("Ref" or "Mut")) return;

            var isTopLevelInitializer = invocation == initValue;
            if (isTopLevelInitializer) {
                // Top-level: rename Ref/Mut→Read. The follow-up FFSECS0011 hint depends on the payload
                // size — for ≤ 8 bytes it's suppressed by the analyzer, so a plain `var x = ... Read<T>()`
                // is a complete fix. For larger payloads the bind-by-'ref readonly' form is the only
                // variant we offer (a copy of a 16+ byte struct is a pessimization).
                var payloadIsSmall = TryIsSmallPayload(invocation, semanticModel, context.CancellationToken);
                if (payloadIsSmall) {
                    // Two variants for small payloads, plain-copy listed first as the recommended one.
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            title: "Switch to 'Read<T>()' (copy snapshot)",
                            createChangedDocument: ct => SwitchToReadAsCopyAsync(context.Document, declarator, ct),
                            equivalenceKey: keyPrefix + "_SwitchToReadCopy"),
                        diagnostic);
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            title: "Switch to 'Read<T>()' (bound by 'ref readonly')",
                            createChangedDocument: ct => SwitchToReadAndBindAsync(context.Document, declarator, ct),
                            equivalenceKey: keyPrefix + "_SwitchToRead"),
                        diagnostic);
                } else {
                    context.RegisterCodeFix(
                        CodeAction.Create(
                            title: "Switch to 'Read<T>()' (snapshot, bound by 'ref readonly')",
                            createChangedDocument: ct => SwitchToReadAndBindAsync(context.Document, declarator, ct),
                            equivalenceKey: keyPrefix + "_SwitchToRead"),
                        diagnostic);
                }
            } else {
                // Chain (e.g. `var v = entity.Ref<T>().Field;`): user wants a copy of `.Field` — rename
                // only the invocation, leave the rest of the chain and the (non-ref) local binding alone.
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Switch to 'Read<T>()' (snapshot)",
                        createChangedDocument: ct => RenameInvocationAsync(context.Document, invocation, "Read", ct),
                        equivalenceKey: keyPrefix + "_SwitchToRead"),
                    diagnostic);
            }
        }

        /// <summary>
        /// True if the ref-returning invocation's payload T resolves to a struct of ≤ 8 bytes
        /// (per <see cref="StructSizeEstimation.SmallStructPayloadByteThreshold"/>). Conservative:
        /// returns false when the semantic model is null, the symbol doesn't resolve, T isn't a value
        /// type, or the size can't be estimated. False means "don't offer the small-copy variant" —
        /// the existing bind-by-'ref readonly' fix is still shown.
        /// </summary>
        private static bool TryIsSmallPayload(InvocationExpressionSyntax invocation, SemanticModel semanticModel, System.Threading.CancellationToken cancellationToken) {
            if (semanticModel is null) return false;
            var symbol = semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol as IMethodSymbol;
            if (symbol is null || symbol.TypeArguments.Length == 0) return false;
            var payload = symbol.TypeArguments[0];
            if (!payload.IsValueType) return false;
            if (!StructSizeEstimation.TryEstimateStructSize(payload, out var size)) return false;
            return size <= StructSizeEstimation.SmallStructPayloadByteThreshold;
        }

        private static void RegisterArgumentFixes(CodeFixContext context, Diagnostic diagnostic, ArgumentSyntax argument, SyntaxNode diagnosticNode, bool isReadonly, string keyPrefix) {
            if (isReadonly) return;
            // Argument expression may itself be the Ref/Mut invocation OR contain it inside a chain
            // (e.g. Method(entity.Ref<T>().Field)). Either way, the diagnostic node points at the
            // invocation that needs renaming.
            if (diagnosticNode is not InvocationExpressionSyntax invocation
                || !TryGetMemberMethodName(invocation, out var methodName)) {
                return;
            }

            if (methodName is "Ref" or "Mut") {
                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Switch to 'Read<T>()' (snapshot)",
                        createChangedDocument: ct => RenameInvocationAsync(context.Document, invocation, "Read", ct),
                        equivalenceKey: keyPrefix + "_SwitchToRead"),
                    diagnostic);
            }
            // Add → no value-returning snapshot equivalent.
        }

        private static async Task<Document> AddRefToLocalAsync(Document document, VariableDeclaratorSyntax declarator, bool isReadonly, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || declarator.Parent is not VariableDeclarationSyntax declaration) return document;
            // Changing the declaration type ('T x' → 'ref T x') would force every other declarator in the same
            // statement to also become 'ref', which is almost certainly wrong. Bail out and leave the user to split.
            if (declaration.Variables.Count != 1) return document;

            var initializer = declarator.Initializer;
            if (initializer is null) return document;

            var refKeyword = SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var originalValue = initializer.Value;
            var refExpression = SyntaxFactory.RefExpression(refKeyword, originalValue.WithoutLeadingTrivia())
                                              .WithLeadingTrivia(originalValue.GetLeadingTrivia());
            var newInitializer = initializer.WithValue(refExpression);
            var newDeclarator = declarator.WithInitializer(newInitializer);

            var refTypeKeyword = SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var readonlyKeyword = isReadonly
                ? SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space)
                : default;
            var newType = SyntaxFactory.RefType(refTypeKeyword, readonlyKeyword, declaration.Type.WithoutLeadingTrivia())
                                       .WithLeadingTrivia(declaration.Type.GetLeadingTrivia());

            var newVariables = declaration.Variables.Replace(declarator, newDeclarator);
            var newDeclaration = declaration.WithType(newType).WithVariables(newVariables);

            return document.WithSyntaxRoot(root.ReplaceNode(declaration, newDeclaration));
        }

        private static bool TryGetInvocationMethodName(ExpressionSyntax expression, out InvocationExpressionSyntax invocation, out string methodName) {
            invocation = expression as InvocationExpressionSyntax;
            methodName = null;
            return invocation is not null && TryGetMemberMethodName(invocation, out methodName);
        }

        /// <summary>Extracts the method name from <c>receiver.MethodName(...)</c> /
        /// <c>receiver.MethodName&lt;T&gt;(...)</c>. Returns false for free function invocations
        /// without a member-access expression.</summary>
        private static bool TryGetMemberMethodName(InvocationExpressionSyntax invocation, out string methodName) {
            methodName = null;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
            methodName = memberAccess.Name switch {
                GenericNameSyntax generic => generic.Identifier.ValueText,
                IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
                _ => null,
            };
            return methodName is not null;
        }

        /// <summary>
        /// Plain-copy variant of "Switch to Read": rename the Ref/Mut invocation to Read, leave the
        /// declarator type and initializer unchanged. Used when the payload T is small enough that the
        /// follow-up FFSECS0011 hint is suppressed (≤ 8 bytes) — no need to introduce a ref-readonly
        /// binding the user didn't ask for.
        /// </summary>
        private static async Task<Document> SwitchToReadAsCopyAsync(Document document, VariableDeclaratorSyntax declarator, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || declarator.Parent is not VariableDeclarationSyntax declaration) return document;
            if (declaration.Variables.Count != 1) return document;
            if (declarator.Initializer is not { } initializer) return document;
            if (!TryGetInvocationMethodName(initializer.Value, out var invocation, out _)) return document;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return document;

            SimpleNameSyntax newName = memberAccess.Name switch {
                GenericNameSyntax generic => generic.WithIdentifier(SyntaxFactory.Identifier("Read")),
                IdentifierNameSyntax identifier => identifier.WithIdentifier(SyntaxFactory.Identifier("Read")),
                _ => null,
            };
            if (newName is null) return document;
            var renamedInvocation = invocation.WithExpression(memberAccess.WithName(newName));
            return document.WithSyntaxRoot(root.ReplaceNode(invocation, renamedInvocation));
        }

        /// <summary>
        /// Combined fix for VariableDeclarator: rename invocation (Ref/Mut→Read) AND convert the local
        /// declaration to 'ref readonly var/T x = ref expr;'. Avoids the post-fix FFSECS0011 hop.
        /// </summary>
        private static async Task<Document> SwitchToReadAndBindAsync(Document document, VariableDeclaratorSyntax declarator, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || declarator.Parent is not VariableDeclarationSyntax declaration) return document;
            // Same concern as AddRefToLocalAsync: type-level rewrite must not be applied to a multi-declarator statement.
            if (declaration.Variables.Count != 1) return document;
            if (declarator.Initializer is not { } initializer) return document;
            if (!TryGetInvocationMethodName(initializer.Value, out var invocation, out _)) return document;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return document;

            // Step 1 — rename method (Ref/Mut → Read) on the invocation expression.
            SimpleNameSyntax newName = memberAccess.Name switch {
                GenericNameSyntax generic => generic.WithIdentifier(SyntaxFactory.Identifier("Read")),
                IdentifierNameSyntax identifier => identifier.WithIdentifier(SyntaxFactory.Identifier("Read")),
                _ => null,
            };
            if (newName is null) return document;
            var renamedInvocation = invocation.WithExpression(memberAccess.WithName(newName));

            // Step 2 — wrap RHS in 'ref renamedInvocation'.
            var refKeyword = SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var refExpression = SyntaxFactory.RefExpression(refKeyword, renamedInvocation)
                                              .WithLeadingTrivia(initializer.Value.GetLeadingTrivia());
            var newInitializer = initializer.WithValue(refExpression);
            var newDeclarator = declarator.WithInitializer(newInitializer);

            // Step 3 — convert LHS to 'ref readonly T' / 'ref readonly var'.
            var refTypeKeyword = SyntaxFactory.Token(SyntaxKind.RefKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var readonlyKeyword = SyntaxFactory.Token(SyntaxKind.ReadOnlyKeyword).WithTrailingTrivia(SyntaxFactory.Space);
            var newType = SyntaxFactory.RefType(refTypeKeyword, readonlyKeyword, declaration.Type.WithoutLeadingTrivia())
                                       .WithLeadingTrivia(declaration.Type.GetLeadingTrivia());

            var newVariables = declaration.Variables.Replace(declarator, newDeclarator);
            var newDeclaration = declaration.WithType(newType).WithVariables(newVariables);

            return document.WithSyntaxRoot(root.ReplaceNode(declaration, newDeclaration));
        }

        private static async Task<Document> RenameInvocationAsync(Document document, ExpressionSyntax expression, string newMethodName, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;
            if (!TryGetInvocationMethodName(expression, out var invocation, out _)) return document;
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) return document;

            SimpleNameSyntax newName = memberAccess.Name switch {
                GenericNameSyntax generic => generic.WithIdentifier(SyntaxFactory.Identifier(newMethodName)),
                IdentifierNameSyntax identifier => identifier.WithIdentifier(SyntaxFactory.Identifier(newMethodName)),
                _ => null,
            };
            if (newName is null) return document;

            var newMemberAccess = memberAccess.WithName(newName);
            var newInvocation = invocation.WithExpression(newMemberAccess);

            return document.WithSyntaxRoot(root.ReplaceNode(invocation, newInvocation));
        }

    }
}

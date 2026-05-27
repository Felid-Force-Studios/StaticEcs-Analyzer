using System;
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
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.CodeFixes {
    /// <summary>
    /// CodeFix for FFSECS0033 — rewrite
    /// <code>
    ///   foreach (var entity in W.Query&lt;All&lt;T0, ...&gt;&gt;().Entities()) {
    ///       ref var x = ref entity.Ref&lt;T0&gt;();
    ///       ...
    ///   }
    /// </code>
    /// into
    /// <code>
    ///   W.Query&lt;...&gt;().For((ref T0 x, ...) =&gt; { ... });
    /// </code>
    /// (or the UserData / Entity-parameter variants when the body needs them).
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ForeachConvertibleToForCodeFix)), Shared]
    public sealed class ForeachConvertibleToForCodeFix : CodeFixProvider {
        public override ImmutableArray<string> FixableDiagnosticIds { get; } =
            ImmutableArray.Create(FFSECSIds.FFSECS0033);

        public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context) {
            var root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root is null) return;

            foreach (var diagnostic in context.Diagnostics) {
                var node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
                var foreachStmt = node?.FirstAncestorOrSelf<ForEachStatementSyntax>();
                if (foreachStmt is null) continue;

                context.RegisterCodeFix(
                    CodeAction.Create(
                        title: "Convert to 'Query().For(...)'",
                        createChangedDocument: ct => ConvertAsync(context.Document, foreachStmt, ct),
                        equivalenceKey: FFSECSIds.FFSECS0033 + "_ConvertToFor"),
                    diagnostic);
            }
        }

        private static async Task<Document> ConvertAsync(Document document, ForEachStatementSyntax foreachStmt, CancellationToken cancellationToken) {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root is null) return document;
            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel is null) return document;

            // ── Locate the Entities() and Query() invocations in the foreach collection ──────────
            if (foreachStmt.Expression is not InvocationExpressionSyntax entitiesInvocation) return document;
            if (entitiesInvocation.Expression is not MemberAccessExpressionSyntax entitiesAccess) return document;
            if (entitiesAccess.Name.Identifier.Text != "Entities") return document;
            if (entitiesAccess.Expression is not InvocationExpressionSyntax queryInvocation) return document;
            if (!TryGetQueryName(queryInvocation, out var queryAccess, out var queryNameNode)) return document;

            var iterationLocal = semanticModel.GetDeclaredSymbol(foreachStmt, cancellationToken);
            if (iterationLocal is null) return document;

            if (foreachStmt.Statement is not BlockSyntax bodyBlock) return document;

            // ── Inventory all `entity.{Ref|Mut|Read}<T>()` invocations in the body ───────────────
            var entityAccesses = CollectEntityComponentAccesses(bodyBlock, iterationLocal, semanticModel, cancellationToken);

            // ── Resolve absorbed components: must be in some All<...> within the filter ──────────
            // We track BOTH a syntactic map (All<...> GenericNameSyntax nodes — used to preserve the
            // user's filter spelling when Query<TFilter>() is written in generic form) and a semantic
            // set (used as the canonical absorbability check — works also for value-arg overloads
            // like Query(filter) where TFilter is inferred and not written syntactically).
            var allComponentSyntax = new Dictionary<ITypeSymbol, GenericNameSyntax>(SymbolEqualityComparer.Default);
            var allComponentTypeArg = new Dictionary<ITypeSymbol, TypeSyntax>(SymbolEqualityComparer.Default);
            CollectAllComponents(queryNameNode, semanticModel, cancellationToken, allComponentSyntax, allComponentTypeArg);
            var allComponentsSemantic = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            var effectiveFilterType = ExtractEffectiveFilterType(queryInvocation, semanticModel, cancellationToken);
            if (effectiveFilterType is not null) CollectAllComponentsSemantic(effectiveFilterType, allComponentsSemantic);

            // Group entity-access entries by component type; decide ref-kind (any Ref/Mut wins over Read).
            var absorbedOrder = new List<AbsorbedComponent>();
            var absorbedByType = new Dictionary<ITypeSymbol, AbsorbedComponent>(SymbolEqualityComparer.Default);
            foreach (var access in entityAccesses) {
                if (access.Component is null) continue;
                if (!allComponentsSemantic.Contains(access.Component.OriginalDefinition)) continue;

                var key = access.Component.OriginalDefinition;
                if (!absorbedByType.TryGetValue(key, out var absorbed)) {
                    absorbed = new AbsorbedComponent {
                        Component = access.Component,
                        RefKind = access.IsRead ? RefKind.In : RefKind.Ref,
                        Name = access.RefLocalName,
                    };
                    absorbedByType[key] = absorbed;
                    absorbedOrder.Add(absorbed);
                } else {
                    if (!access.IsRead) absorbed.RefKind = RefKind.Ref;
                    if (absorbed.Name is null && access.RefLocalName is not null) absorbed.Name = access.RefLocalName;
                }
            }
            // Note: absorbedOrder may be empty — that's fine. The codefix then uses the zero-component
            // overload `For((Entity entity) => { ... })`, keeping the filter in Query<...> untouched.

            // Fill missing param names from type name (camelCase).
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var absorbed in absorbedOrder) {
                if (absorbed.Name is not null) usedNames.Add(absorbed.Name);
            }
            foreach (var absorbed in absorbedOrder) {
                if (absorbed.Name is null) {
                    absorbed.Name = DeriveParamName(absorbed.Component.Name, usedNames);
                    usedNames.Add(absorbed.Name);
                }
            }

            // ── Determine whether we need an `Entity entity` parameter ───────────────────────────
            // The lambda needs an Entity parameter when any reference to the iteration variable in the
            // body is NOT the receiver of an absorbed Ref/Mut/Read call. This covers:
            //   • Non-absorbed Ref/Mut/Read calls (T outside All<...>) — kept verbatim in the body.
            //   • `entity.Has<T>()`, `entity.Delete<T>()`, `entity.SetTag<...>()`, etc.
            //   • `entity` passed as value: `foo(entity)`, `x = entity`, `something.Field = entity`.
            var consumedEntityReceivers = new HashSet<ExpressionSyntax>();
            foreach (var access in entityAccesses) {
                if (access.Component is null) continue;
                if (!absorbedByType.ContainsKey(access.Component.OriginalDefinition)) continue;
                if (access.Invocation.Expression is MemberAccessExpressionSyntax memberAccess) {
                    consumedEntityReceivers.Add(memberAccess.Expression);
                }
            }
            var needEntityParam = HasUnabsorbedEntityUsage(bodyBlock, iterationLocal, semanticModel, consumedEntityReceivers, cancellationToken);
            // When nothing is absorbed, the lambda would have zero component params. The For overloads
            // need at least an Entity-param to bind to (`For(QueryFunctionWithEntity<TWorld>)`), so
            // force-include it even if the body never references `entity`.
            if (absorbedOrder.Count == 0) needEntityParam = true;

            // ── Capture analysis (re-derive single capture symbol if any) ────────────────────────
            var capturedSymbol = TryFindSingleCapturedSymbol(bodyBlock, iterationLocal, semanticModel, foreachStmt.Span, cancellationToken);
            // `this`-capture: instance method/property/field access on the enclosing type without an
            // explicit receiver, or a `this`/`base` expression. Mutually exclusive with capturedSymbol
            // — the analyzer guards against the mixed case.
            var enclosingType = capturedSymbol is null
                ? DetectEnclosingTypeForThisCapture(bodyBlock, semanticModel, foreachStmt, cancellationToken)
                : null;
            var hasThisCapture = enclosingType is not null;

            // Choose UserData parameter name; ensure no collision with absorbed names.
            string userDataParamName = null;
            if (capturedSymbol is not null) {
                // Derive the UserData parameter name from the captured symbol's name + "Data" suffix —
                // keeps the parameter intent-revealing (e.g. `selectedFactor` → `selectedFactorData`)
                // instead of a generic `data` that obscures the source.
                userDataParamName = capturedSymbol.Name + "Data";
                while (usedNames.Contains(userDataParamName) || SyntaxFacts.GetKeywordKind(userDataParamName) != SyntaxKind.None) {
                    userDataParamName += "_";
                }
                usedNames.Add(userDataParamName);
            } else if (hasThisCapture) {
                // `self` is the conventional name for the enclosing-instance UserData parameter. For
                // a struct system this is `ref this`, for a class it's a by-value `this` reference.
                userDataParamName = "self";
                while (usedNames.Contains(userDataParamName) || SyntaxFacts.GetKeywordKind(userDataParamName) != SyntaxKind.None) {
                    userDataParamName += "_";
                }
                usedNames.Add(userDataParamName);
            }

            // Iteration variable name (entity) — keep original name when possible.
            string entityParamName = null;
            if (needEntityParam) {
                entityParamName = iterationLocal.Name;
                while (usedNames.Contains(entityParamName)) entityParamName += "_";
                usedNames.Add(entityParamName);
            }

            // ── Build new lambda body ────────────────────────────────────────────────────────────
            var rewriter = new ForeachToLambdaRewriter(
                semanticModel,
                iterationLocal,
                absorbedByType,
                entityAccesses,
                entityParamName,
                userDataParamName,
                capturedSymbol,
                hasThisCapture ? enclosingType : null,
                cancellationToken);
            var newBody = (BlockSyntax)rewriter.Visit(bodyBlock);
            // Strip the close-brace trailing trivia (a newline carried over from the original
            // foreach `}`) so that the For ArgumentList's `)` and the ExpressionStatement's `;` glue
            // to `}` on the same line, producing `});` aligned with the foreach indent instead of
            // dangling in column 0 on a new line.
            newBody = newBody.WithCloseBraceToken(newBody.CloseBraceToken.WithTrailingTrivia());

            // ── Build lambda parameter list ──────────────────────────────────────────────────────
            var parameters = new List<ParameterSyntax>();
            if (userDataParamName is not null) {
                if (hasThisCapture) {
                    // UserData = the enclosing instance. `ref T` for struct (only legal ref-this form),
                    // by-value for class (`ref this` of a class is not a thing in C#).
                    var modifier = enclosingType.IsValueType ? SyntaxKind.RefKeyword : SyntaxKind.None;
                    parameters.Add(Parameter(userDataParamName, TypeSyntaxFor(enclosingType), modifier));
                } else {
                    var capturedType = capturedSymbol switch {
                        ILocalSymbol l => l.Type,
                        IParameterSymbol p => p.Type,
                        IFieldSymbol f => f.Type,
                        _ => null,
                    };
                    if (capturedType is null) return document;
                    parameters.Add(Parameter(userDataParamName, TypeSyntaxFor(capturedType), SyntaxKind.RefKeyword));
                }
            }
            if (entityParamName is not null) {
                // Entity is nested in World<TWorld> — qualify via the same world alias the user wrote
                // for the Query() call (e.g. `W.Query()` → `W.Entity`). Without the qualifier the
                // name binds to nothing at the call site.
                var entityType = SyntaxFactory.ParseTypeName(queryAccess.Expression.WithoutTrivia().ToString() + ".Entity");
                parameters.Add(Parameter(entityParamName, entityType, SyntaxKind.None));
            }
            foreach (var absorbed in absorbedOrder.Where(component => component.RefKind == RefKind.Ref)) {
                parameters.Add(Parameter(absorbed.Name, TypeSyntaxFor(absorbed.Component), SyntaxKind.RefKeyword));
            }
            foreach (var absorbed in absorbedOrder.Where(component => component.RefKind == RefKind.In)) {
                parameters.Add(Parameter(absorbed.Name, TypeSyntaxFor(absorbed.Component), SyntaxKind.InKeyword));
            }

            var lambda = SyntaxFactory.ParenthesizedLambdaExpression(
                SyntaxFactory.ParameterList(SyntaxFactory.SeparatedList(parameters)),
                newBody);
            if (userDataParamName is not null) {
                // Static lambda — no implicit captures because UserData carries the one external symbol.
                lambda = lambda.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(SyntaxKind.StaticKeyword)));
            }

            // ── Build the new Query<...>() filter argument (with absorbed components stripped) ───
            // Prefer the syntactic path when the user wrote Query<TFilter>() — it preserves spelling
            // and trivia. Fall back to building from semantic types when the call is Query(filter)
            // (no syntactic generic name to walk) or any other shape without GenericNameSyntax.
            SimpleNameSyntax newQueryNameNode;
            var dropArguments = false;
            if (queryNameNode is GenericNameSyntax) {
                newQueryNameNode = RewriteQueryName(queryNameNode, allComponentSyntax, allComponentTypeArg, absorbedByType);
            } else {
                newQueryNameNode = BuildQueryNameFromSemantic(queryNameNode.Identifier, effectiveFilterType, absorbedByType);
                dropArguments = true; // value-arg overloads — drop the now-redundant filter argument(s).
            }

            // ── Compose: <queryReceiver>.<NewQueryName>(queryArgs).For(...) ──────────────────────
            var newQueryInvocation = queryInvocation.WithExpression(
                queryAccess.WithName(newQueryNameNode));
            if (dropArguments) {
                newQueryInvocation = newQueryInvocation.WithArgumentList(SyntaxFactory.ArgumentList());
            }

            var forArguments = new List<ArgumentSyntax>();
            if (userDataParamName is not null) {
                if (hasThisCapture) {
                    // `For(ref this, …)` for struct enclosings, `For(this, …)` for classes.
                    var refToken = enclosingType.IsValueType
                        ? SyntaxFactory.Token(SyntaxKind.RefKeyword)
                        : default;
                    forArguments.Add(SyntaxFactory.Argument(null, refToken, SyntaxFactory.ThisExpression()));
                } else {
                    forArguments.Add(SyntaxFactory.Argument(null, SyntaxFactory.Token(SyntaxKind.RefKeyword), SyntaxFactory.IdentifierName(capturedSymbol.Name)));
                }
            }
            forArguments.Add(SyntaxFactory.Argument(lambda));
            foreach (var arg in entitiesInvocation.ArgumentList.Arguments) {
                forArguments.Add(arg);
            }

            var forCall = SyntaxFactory.InvocationExpression(
                SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression,
                    newQueryInvocation,
                    SyntaxFactory.IdentifierName("For")),
                SyntaxFactory.ArgumentList(SyntaxFactory.SeparatedList(forArguments)));

            // Mark the synthesized statement for the Roslyn formatter — without this the trivia
            // inherited from the original `foreach` body (`}` with a trailing newline) leaks into
            // the generated layout: the closing `);` of the For call ends up in column 0 instead of
            // aligning with the original `foreach` indent. `Formatter.FormatAsync(..., Formatter.Annotation, ...)`
            // touches only the annotated subtree, so the rest of the document is left alone.
            var newStatement = SyntaxFactory.ExpressionStatement(forCall)
                                            .WithTriviaFrom(foreachStmt)
                                            .WithAdditionalAnnotations(Formatter.Annotation);

            var newDocument = document.WithSyntaxRoot(root.ReplaceNode(foreachStmt, newStatement));
            return await Formatter.FormatAsync(newDocument, Formatter.Annotation, options: null, cancellationToken).ConfigureAwait(false);
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Data carriers
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private sealed class AbsorbedComponent {
            public ITypeSymbol Component;
            public RefKind RefKind;
            public string Name;
        }

        private sealed class EntityAccess {
            public InvocationExpressionSyntax Invocation;
            public ITypeSymbol Component;
            public bool IsRead; // false: Ref/Mut; true: Read
            public string RefLocalName; // non-null iff this invocation is the initializer of `ref [readonly] var X = ref invocation;`
            public LocalDeclarationStatementSyntax RefLocalDeclaration; // the full declaration statement to remove
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Body inspection
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private static List<EntityAccess> CollectEntityComponentAccesses(
            BlockSyntax body,
            ILocalSymbol iterationLocal,
            SemanticModel semanticModel,
            CancellationToken cancellationToken) {

            var result = new List<EntityAccess>();
            foreach (var invocation in body.DescendantNodes().OfType<InvocationExpressionSyntax>()) {
                if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess) continue;
                var methodName = memberAccess.Name.Identifier.Text;
                if (methodName is not ("Ref" or "Mut" or "Read")) continue;

                // Receiver must be the iteration local.
                var receiverSymbol = semanticModel.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol;
                if (receiverSymbol is null) continue;
                if (!SymbolEqualityComparer.Default.Equals(receiverSymbol, iterationLocal)) continue;

                // Resolve generic type argument T.
                if (semanticModel.GetSymbolInfo(invocation, cancellationToken).Symbol is not IMethodSymbol method) continue;
                if (method.TypeArguments.Length == 0) continue;

                var access = new EntityAccess {
                    Invocation = invocation,
                    Component = method.TypeArguments[0],
                    IsRead = methodName == "Read",
                };
                ResolveRefLocalBinder(invocation, access);
                result.Add(access);
            }
            return result;
        }

        private static void ResolveRefLocalBinder(InvocationExpressionSyntax invocation, EntityAccess access) {
            // Pattern A (ref-binding): `ref [readonly] var X = ref entity.M<T>();` — the invocation
            // is wrapped in a RefExpression which is the value of an EqualsValueClause on a ref-typed
            // declarator.
            if (invocation.Parent is RefExpressionSyntax refExpr) {
                if (refExpr.Parent is not EqualsValueClauseSyntax equalsClause) return;
                if (equalsClause.Parent is not VariableDeclaratorSyntax declarator) return;
                if (declarator.Parent is not VariableDeclarationSyntax declaration) return;
                if (declaration.Type is not RefTypeSyntax) return;
                if (declaration.Variables.Count != 1) return;
                if (declaration.Parent is not LocalDeclarationStatementSyntax statement) return;

                access.RefLocalName = declarator.Identifier.Text;
                access.RefLocalDeclaration = statement;
                return;
            }
            // Pattern B (plain copy alias): `var X = entity.M<T>();` — the invocation is directly the
            // value of an EqualsValueClause. For Read this is a value-copy that we losslessly replace
            // with an `in T` parameter; for Ref/Mut the original was already a (buggy) copy — the
            // rewrite into a `ref T` parameter matches what FFSECS0010 would push the user toward.
            if (invocation.Parent is EqualsValueClauseSyntax equalsClauseNoRef
                && equalsClauseNoRef.Parent is VariableDeclaratorSyntax declaratorNoRef
                && declaratorNoRef.Parent is VariableDeclarationSyntax declarationNoRef
                && declarationNoRef.Variables.Count == 1
                && declarationNoRef.Parent is LocalDeclarationStatementSyntax statementNoRef) {
                access.RefLocalName = declaratorNoRef.Identifier.Text;
                access.RefLocalDeclaration = statementNoRef;
            }
        }

        private static bool HasUnabsorbedEntityUsage(
            BlockSyntax body,
            ILocalSymbol iterationLocal,
            SemanticModel semanticModel,
            HashSet<ExpressionSyntax> consumedEntityReceivers,
            CancellationToken cancellationToken) {

            foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>()) {
                if (id.Identifier.Text != iterationLocal.Name) continue;
                if (consumedEntityReceivers.Contains(id)) continue;
                var symbol = semanticModel.GetSymbolInfo(id, cancellationToken).Symbol;
                if (!SymbolEqualityComparer.Default.Equals(symbol, iterationLocal)) continue;
                return true;
            }
            return false;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Filter inspection
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private static bool TryGetQueryName(InvocationExpressionSyntax queryInvocation,
                                            out MemberAccessExpressionSyntax queryAccess,
                                            out SimpleNameSyntax queryNameNode) {
            queryAccess = null;
            queryNameNode = null;
            if (queryInvocation.Expression is not MemberAccessExpressionSyntax memberAccess) return false;
            if (memberAccess.Name.Identifier.Text != "Query") return false;
            queryAccess = memberAccess;
            queryNameNode = memberAccess.Name;
            return true;
        }

        /// <summary>
        /// Walks the Query name (which may be <c>Query</c> or <c>Query&lt;TFilter&gt;</c>) and the filter
        /// type-syntax tree, populating <paramref name="byType"/> with the All&lt;...&gt; node holding
        /// each component, and <paramref name="byTypeArg"/> with the specific TypeSyntax node for the
        /// component (used later to remove it from the All&lt;...&gt; argument list).
        /// </summary>
        private static void CollectAllComponents(
            SimpleNameSyntax queryName,
            SemanticModel semanticModel,
            CancellationToken cancellationToken,
            Dictionary<ITypeSymbol, GenericNameSyntax> byType,
            Dictionary<ITypeSymbol, TypeSyntax> byTypeArg) {

            if (queryName is not GenericNameSyntax queryGeneric) return;
            foreach (var typeArg in queryGeneric.TypeArgumentList.Arguments) {
                Walk(typeArg);
            }

            void Walk(TypeSyntax type) {
                if (type is not GenericNameSyntax generic) return;
                var name = generic.Identifier.Text;
                if (name == "And") {
                    foreach (var inner in generic.TypeArgumentList.Arguments) Walk(inner);
                    return;
                }
                if (name == "All") {
                    foreach (var componentArg in generic.TypeArgumentList.Arguments) {
                        var componentType = semanticModel.GetTypeInfo(componentArg, cancellationToken).Type;
                        if (componentType is null) continue;
                        var key = componentType.OriginalDefinition;
                        if (!byType.ContainsKey(key)) {
                            byType[key] = generic;
                            byTypeArg[key] = componentArg;
                        }
                    }
                }
                // None/Any/etc. — pass-through, no inspection needed.
            }
        }

        /// <summary>
        /// Rewrites the <c>Query&lt;TFilter&gt;</c> generic name by removing absorbed components from
        /// any contained <c>All&lt;...&gt;</c>. Empty <c>All&lt;...&gt;</c> nodes are removed from their
        /// containing <c>And&lt;...&gt;</c>; if <c>And&lt;...&gt;</c> collapses to a single argument it
        /// is unwrapped; if the top-level filter disappears, returns a bare <c>Query</c> identifier.
        /// </summary>
        private static SimpleNameSyntax RewriteQueryName(
            SimpleNameSyntax queryName,
            Dictionary<ITypeSymbol, GenericNameSyntax> allByType,
            Dictionary<ITypeSymbol, TypeSyntax> allByTypeArg,
            Dictionary<ITypeSymbol, AbsorbedComponent> absorbed) {

            if (queryName is not GenericNameSyntax queryGeneric) return queryName;

            // Build set of TypeSyntax nodes to remove from any All<...>.
            var toRemove = new HashSet<TypeSyntax>();
            foreach (var pair in absorbed) {
                if (allByTypeArg.TryGetValue(pair.Key, out var typeArgNode)) toRemove.Add(typeArgNode);
            }

            var rewrittenArgs = new List<TypeSyntax>();
            foreach (var topArg in queryGeneric.TypeArgumentList.Arguments) {
                var rewritten = RewriteFilterNode(topArg, toRemove);
                if (rewritten is not null) rewrittenArgs.Add(rewritten);
            }

            if (rewrittenArgs.Count == 0) {
                return SyntaxFactory.IdentifierName(queryGeneric.Identifier);
            }
            return queryGeneric.WithTypeArgumentList(
                SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(rewrittenArgs)));
        }

        /// <summary>
        /// Returns the effective TFilter symbol — what would have been written in Query&lt;TFilter&gt;()
        /// — by inspecting the inferred return type WorldQuery&lt;TFilter&gt; of the Query call. Works
        /// for both the generic-syntactic form and the value-arg overloads (Query(filter), Query(f0, f1)).
        /// </summary>
        private static ITypeSymbol ExtractEffectiveFilterType(InvocationExpressionSyntax queryInvocation, SemanticModel semanticModel, CancellationToken cancellationToken) {
            // After overload resolution and type inference, Query<...>(...) returns WorldQuery<TFilter>.
            // TFilter is exactly what we'd have to write in the generic-name form, so we extract it
            // straight from the call's resolved return type.
            if (semanticModel.GetSymbolInfo(queryInvocation, cancellationToken).Symbol is not IMethodSymbol method) return null;
            return method.ReturnType is INamedTypeSymbol returnType && returnType.TypeArguments.Length >= 1
                ? returnType.TypeArguments[0]
                : null;
        }

        private static void CollectAllComponentsSemantic(ITypeSymbol filter, HashSet<ITypeSymbol> set) {
            if (filter is not INamedTypeSymbol named) return;
            var name = named.OriginalDefinition.Name;
            if (name == "And") {
                foreach (var inner in named.TypeArguments) CollectAllComponentsSemantic(inner, set);
                return;
            }
            if (name == "All") {
                foreach (var component in named.TypeArguments) set.Add(component.OriginalDefinition);
            }
            // None/Any/etc. — pass-through, nothing to collect.
        }

        /// <summary>
        /// Builds the new Query name (either Query or Query&lt;NewFilter&gt;) from semantic info, used
        /// when the original call had no syntactic generic name (Query(filter) overloads). Walks the
        /// semantic filter tree, omits absorbed components from any All&lt;...&gt;, and collapses empty
        /// nodes the same way the syntactic rewrite does.
        /// </summary>
        private static SimpleNameSyntax BuildQueryNameFromSemantic(SyntaxToken queryIdentifier, ITypeSymbol effectiveFilter, Dictionary<ITypeSymbol, AbsorbedComponent> absorbed) {
            if (effectiveFilter is null) return SyntaxFactory.IdentifierName(queryIdentifier);
            var absorbedSet = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var key in absorbed.Keys) absorbedSet.Add(key);
            var newFilter = BuildFilterTypeSyntaxFromSymbol(effectiveFilter, absorbedSet);
            if (newFilter is null) {
                return SyntaxFactory.IdentifierName(queryIdentifier);
            }
            return SyntaxFactory.GenericName(queryIdentifier)
                .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SingletonSeparatedList(newFilter)));
        }

        private static TypeSyntax BuildFilterTypeSyntaxFromSymbol(ITypeSymbol filter, HashSet<ITypeSymbol> absorbed) {
            if (filter is not INamedTypeSymbol named) {
                return SyntaxFactory.ParseTypeName(filter.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
            }
            var origName = named.OriginalDefinition.Name;
            if (origName == "And") {
                var kept = new List<TypeSyntax>();
                foreach (var inner in named.TypeArguments) {
                    var rewritten = BuildFilterTypeSyntaxFromSymbol(inner, absorbed);
                    if (rewritten is not null) kept.Add(rewritten);
                }
                if (kept.Count == 0) return null;
                if (kept.Count == 1) return kept[0];
                return SyntaxFactory.GenericName(SyntaxFactory.Identifier("And"))
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(kept)));
            }
            if (origName == "All") {
                var kept = new List<TypeSyntax>();
                foreach (var componentType in named.TypeArguments) {
                    if (absorbed.Contains(componentType.OriginalDefinition)) continue;
                    kept.Add(SyntaxFactory.ParseTypeName(componentType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                }
                if (kept.Count == 0) return null;
                return SyntaxFactory.GenericName(SyntaxFactory.Identifier("All"))
                    .WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(kept)));
            }
            return SyntaxFactory.ParseTypeName(named.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }

        private static TypeSyntax RewriteFilterNode(TypeSyntax node, HashSet<TypeSyntax> toRemove) {
            if (node is not GenericNameSyntax generic) return node;
            var name = generic.Identifier.Text;
            if (name == "And") {
                var keptArgs = new List<TypeSyntax>();
                foreach (var inner in generic.TypeArgumentList.Arguments) {
                    var rewritten = RewriteFilterNode(inner, toRemove);
                    if (rewritten is not null) keptArgs.Add(rewritten);
                }
                if (keptArgs.Count == 0) return null;
                if (keptArgs.Count == 1) return keptArgs[0];
                return generic.WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(keptArgs)));
            }
            if (name == "All") {
                var keptArgs = new List<TypeSyntax>();
                foreach (var componentArg in generic.TypeArgumentList.Arguments) {
                    if (toRemove.Contains(componentArg)) continue;
                    keptArgs.Add(componentArg);
                }
                if (keptArgs.Count == 0) return null;
                return generic.WithTypeArgumentList(SyntaxFactory.TypeArgumentList(SyntaxFactory.SeparatedList(keptArgs)));
            }
            return node;
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Capture analysis
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private static ISymbol TryFindSingleCapturedSymbol(BlockSyntax body, ILocalSymbol iterationLocal, SemanticModel semanticModel, Microsoft.CodeAnalysis.Text.TextSpan foreachSpan, CancellationToken cancellationToken) {
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            ISymbol captured = null;
            foreach (var id in body.DescendantNodes().OfType<IdentifierNameSyntax>()) {
                var symbol = semanticModel.GetSymbolInfo(id, cancellationToken).Symbol;
                ISymbol candidate = null;
                switch (symbol) {
                    case ILocalSymbol local:
                        if (SymbolEqualityComparer.Default.Equals(local, iterationLocal)) continue;
                        if (IsDeclaredInside(local, foreachSpan)) continue;
                        candidate = local;
                        break;
                    case IParameterSymbol param:
                        candidate = param;
                        break;
                    case IFieldSymbol field:
                        if (field.IsStatic || field.IsConst || field.IsReadOnly) continue;
                        // Only count when the receiver is `this` / `base` (explicit or implicit).
                        // Field access reached through any other expression (localVar.Field,
                        // refLocal.Field, or `new T { Field = … }` where the receiver is the
                        // implicit object-initializer target) is a member of that expression, not
                        // a capture of the enclosing scope.
                        if (semanticModel.GetOperation(id, cancellationToken) is not IFieldReferenceOperation fieldRefOp) continue;
                        if (fieldRefOp.Instance is not IInstanceReferenceOperation instanceRef) continue;
                        if (instanceRef.ReferenceKind != InstanceReferenceKind.ContainingTypeInstance) continue;
                        candidate = field;
                        break;
                    default:
                        continue;
                }
                if (seen.Add(candidate)) captured = candidate;
            }
            return seen.Count == 1 ? captured : null;
        }

        private static bool IsDeclaredInside(ISymbol symbol, Microsoft.CodeAnalysis.Text.TextSpan containerSpan) {
            foreach (var location in symbol.Locations) {
                if (!location.IsInSource) continue;
                if (containerSpan.Contains(location.SourceSpan)) return true;
            }
            return false;
        }

        /// <summary>
        /// Detects whether the foreach body references the enclosing type's `this` — directly via
        /// `this`/`base` expressions, or implicitly via instance-member access without an explicit
        /// receiver (instance method call, instance field/property read of the enclosing type).
        /// Returns the enclosing INamedTypeSymbol when any such reference is found, otherwise null.
        /// </summary>
        private static INamedTypeSymbol DetectEnclosingTypeForThisCapture(BlockSyntax body, SemanticModel semanticModel, ForEachStatementSyntax foreachStmt, CancellationToken cancellationToken) {
            // Resolve enclosing INamedTypeSymbol from the foreach position once.
            INamedTypeSymbol enclosing = null;
            ISymbol enclosingSymbol = semanticModel.GetEnclosingSymbol(foreachStmt.SpanStart, cancellationToken);
            while (enclosingSymbol is not null) {
                if (enclosingSymbol is INamedTypeSymbol named) { enclosing = named; break; }
                enclosingSymbol = enclosingSymbol.ContainingSymbol;
            }
            if (enclosing is null) return null;

            foreach (var node in body.DescendantNodes()) {
                if (node is ThisExpressionSyntax or BaseExpressionSyntax) {
                    return enclosing;
                }
                if (node is IdentifierNameSyntax id) {
                    // Skip the .Name part of MemberAccess — receiver is explicit.
                    if (id.Parent is MemberAccessExpressionSyntax m && m.Name == id) continue;
                    var symbol = semanticModel.GetSymbolInfo(id, cancellationToken).Symbol;
                    if (symbol is null || symbol.IsStatic) continue;
                    if (symbol is not (IFieldSymbol or IPropertySymbol or IMethodSymbol)) continue;
                    if (!SymbolEqualityComparer.Default.Equals(symbol.ContainingType, enclosing)) continue;
                    return enclosing;
                }
            }
            return null;
        }

        private static string ToCamelCase(string name) {
            if (string.IsNullOrEmpty(name)) return "self";
            if (!char.IsLetter(name[0])) return "_" + name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Lambda body rewrite
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private sealed class ForeachToLambdaRewriter : CSharpSyntaxRewriter {
            private readonly SemanticModel _semanticModel;
            private readonly ILocalSymbol _iterationLocal;
            private readonly Dictionary<ITypeSymbol, AbsorbedComponent> _absorbed;
            private readonly Dictionary<SyntaxNode, AbsorbedComponent> _invocationToAbsorbed;
            private readonly HashSet<LocalDeclarationStatementSyntax> _refLocalDecls;
            private readonly Dictionary<string, string> _refLocalNameToParamName;
            private readonly string _entityParamName;
            private readonly string _userDataParamName;
            private readonly ISymbol _capturedSymbol;
            private readonly INamedTypeSymbol _enclosingTypeForThis;
            private readonly CancellationToken _cancellationToken;

            public ForeachToLambdaRewriter(
                SemanticModel semanticModel,
                ILocalSymbol iterationLocal,
                Dictionary<ITypeSymbol, AbsorbedComponent> absorbedByType,
                List<EntityAccess> entityAccesses,
                string entityParamName,
                string userDataParamName,
                ISymbol capturedSymbol,
                INamedTypeSymbol enclosingTypeForThis,
                CancellationToken cancellationToken) {

                _semanticModel = semanticModel;
                _iterationLocal = iterationLocal;
                _absorbed = absorbedByType;
                _entityParamName = entityParamName;
                _userDataParamName = userDataParamName;
                _capturedSymbol = capturedSymbol;
                _enclosingTypeForThis = enclosingTypeForThis;
                _cancellationToken = cancellationToken;

                _invocationToAbsorbed = new Dictionary<SyntaxNode, AbsorbedComponent>();
                _refLocalDecls = new HashSet<LocalDeclarationStatementSyntax>();
                _refLocalNameToParamName = new Dictionary<string, string>(StringComparer.Ordinal);

                foreach (var access in entityAccesses) {
                    if (access.Component is null) continue;
                    if (!absorbedByType.TryGetValue(access.Component.OriginalDefinition, out var absorbed)) continue;
                    _invocationToAbsorbed[access.Invocation] = absorbed;
                    if (access.RefLocalDeclaration is not null) {
                        _refLocalDecls.Add(access.RefLocalDeclaration);
                        if (access.RefLocalName is not null && !_refLocalNameToParamName.ContainsKey(access.RefLocalName)) {
                            _refLocalNameToParamName[access.RefLocalName] = absorbed.Name;
                        }
                    }
                }
            }

            public override SyntaxNode VisitLocalDeclarationStatement(LocalDeclarationStatementSyntax node) {
                if (_refLocalDecls.Contains(node)) return null; // remove the absorbed ref-local declaration
                return base.VisitLocalDeclarationStatement(node);
            }

            public override SyntaxNode VisitContinueStatement(ContinueStatementSyntax node) {
                // `continue` inside the foreach maps to `return;` inside the lambda — the For loop
                // continues to the next entity afterwards. Keep trivia so indentation survives.
                return SyntaxFactory.ReturnStatement(
                    SyntaxFactory.Token(SyntaxKind.ReturnKeyword).WithTrailingTrivia(SyntaxFactory.Space),
                    expression: null,
                    semicolonToken: SyntaxFactory.Token(SyntaxKind.SemicolonToken))
                    .WithTriviaFrom(node);
            }

            public override SyntaxNode VisitInvocationExpression(InvocationExpressionSyntax node) {
                if (_invocationToAbsorbed.TryGetValue(node, out var absorbed)) {
                    return SyntaxFactory.IdentifierName(absorbed.Name).WithTriviaFrom(node);
                }
                return base.VisitInvocationExpression(node);
            }

            public override SyntaxNode VisitThisExpression(ThisExpressionSyntax node) {
                if (_enclosingTypeForThis is not null && _userDataParamName is not null) {
                    return SyntaxFactory.IdentifierName(_userDataParamName).WithTriviaFrom(node);
                }
                return base.VisitThisExpression(node);
            }

            public override SyntaxNode VisitBaseExpression(BaseExpressionSyntax node) {
                if (_enclosingTypeForThis is not null && _userDataParamName is not null) {
                    return SyntaxFactory.IdentifierName(_userDataParamName).WithTriviaFrom(node);
                }
                return base.VisitBaseExpression(node);
            }

            public override SyntaxNode VisitMemberAccessExpression(MemberAccessExpressionSyntax node) {
                // `this.selectedFactor` (or any qualified path resolving to the captured field) →
                // collapse the whole MemberAccess into the UserData parameter name. Without this the
                // identifier-only override below would produce `this.selectedFactorData`, which
                // references a non-existent field.
                if (_capturedSymbol is not null && _userDataParamName is not null) {
                    var symbol = _semanticModel.GetSymbolInfo(node, _cancellationToken).Symbol;
                    if (SymbolEqualityComparer.Default.Equals(symbol, _capturedSymbol)) {
                        return SyntaxFactory.IdentifierName(_userDataParamName).WithTriviaFrom(node);
                    }
                }
                return base.VisitMemberAccessExpression(node);
            }

            public override SyntaxNode VisitIdentifierName(IdentifierNameSyntax node) {
                // Replace ref-local identifier (e.g. `needs`) with the absorbed param name (same in the
                // canonical case, but kept for robustness when the user named the ref-local differently).
                if (_refLocalNameToParamName.TryGetValue(node.Identifier.Text, out var paramName)) {
                    var symbol = _semanticModel.GetSymbolInfo(node, _cancellationToken).Symbol;
                    if (symbol is ILocalSymbol local && local.RefKind != RefKind.None) {
                        return SyntaxFactory.IdentifierName(paramName).WithTriviaFrom(node);
                    }
                }
                // Replace the entity identifier with the lambda Entity-param name (only if we added one).
                if (_entityParamName is not null && node.Identifier.Text == _iterationLocal.Name) {
                    var symbol = _semanticModel.GetSymbolInfo(node, _cancellationToken).Symbol;
                    if (SymbolEqualityComparer.Default.Equals(symbol, _iterationLocal)) {
                        if (_entityParamName != _iterationLocal.Name) {
                            return SyntaxFactory.IdentifierName(_entityParamName).WithTriviaFrom(node);
                        }
                        return node;
                    }
                }
                // Replace captured-symbol references with the UserData parameter name.
                if (_capturedSymbol is not null && _userDataParamName is not null && node.Identifier.Text == _capturedSymbol.Name) {
                    var symbol = _semanticModel.GetSymbolInfo(node, _cancellationToken).Symbol;
                    if (SymbolEqualityComparer.Default.Equals(symbol, _capturedSymbol)) {
                        return SyntaxFactory.IdentifierName(_userDataParamName).WithTriviaFrom(node);
                    }
                }
                // `this`-capture mode: instance members of the enclosing type reached via implicit
                // `this` (e.g. bare `selectedFactor`, `UpdatePosition`, …) → `userDataParam.X`.
                if (_enclosingTypeForThis is not null && _userDataParamName is not null) {
                    // Skip when this IdentifierName is the .Name part of a MemberAccess (`X.Name`) —
                    // the receiver has already been written explicitly; we'd otherwise produce
                    // `X.userData.Name`.
                    if (!(node.Parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == node)) {
                        var symbol = _semanticModel.GetSymbolInfo(node, _cancellationToken).Symbol;
                        if (symbol is (IFieldSymbol or IPropertySymbol or IMethodSymbol)
                            && !symbol.IsStatic
                            && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, _enclosingTypeForThis)) {
                            // Build a fresh inner identifier WITHOUT trivia. Reusing `node.Identifier`
                            // would carry over the original leading newline+indent (the `HandleMove`
                            // token had a leading newline because it sat on its own line), which would
                            // be printed between the dot and the name: `self.\n    HandleMove`.
                            return SyntaxFactory.MemberAccessExpression(
                                SyntaxKind.SimpleMemberAccessExpression,
                                SyntaxFactory.IdentifierName(_userDataParamName),
                                SyntaxFactory.IdentifierName(node.Identifier.ValueText))
                                .WithTriviaFrom(node);
                        }
                    }
                }
                return base.VisitIdentifierName(node);
            }
        }

        // ─────────────────────────────────────────────────────────────────────────────────────────
        // Syntax helpers
        // ─────────────────────────────────────────────────────────────────────────────────────────

        private static ParameterSyntax Parameter(string name, TypeSyntax type, SyntaxKind modifierKind) {
            var parameter = SyntaxFactory.Parameter(SyntaxFactory.Identifier(name))
                                          .WithType(type.WithTrailingTrivia(SyntaxFactory.Space));
            if (modifierKind != SyntaxKind.None) {
                parameter = parameter.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(modifierKind).WithTrailingTrivia(SyntaxFactory.Space)));
            }
            return parameter;
        }

        private static TypeSyntax TypeSyntaxFor(ITypeSymbol type) {
            return SyntaxFactory.ParseTypeName(type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat));
        }

        private static string DeriveParamName(string typeName, HashSet<string> used) {
            if (string.IsNullOrEmpty(typeName)) typeName = "value";
            string camel;
            if (char.IsLetter(typeName[0])) {
                camel = char.ToLowerInvariant(typeName[0]) + typeName.Substring(1);
            } else {
                camel = "_" + typeName;
            }
            var candidate = camel;
            var suffix = 1;
            while (used.Contains(candidate) || SyntaxFacts.GetKeywordKind(candidate) != SyntaxKind.None) {
                candidate = camel + suffix++;
            }
            return candidate;
        }
    }
}

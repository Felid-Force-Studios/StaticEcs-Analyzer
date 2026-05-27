using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0033 — <c>foreach (var entity in W.Query&lt;All&lt;...&gt;&gt;().Entities()) { ... }</c>
    /// can be replaced with <c>W.Query&lt;...&gt;().For((ref T0, ...) =&gt; { ... })</c>.
    ///
    /// The diagnostic only fires when the rewrite is safe and useful:
    ///   • The foreach iterates exactly <c>Query&lt;TFilter&gt;().Entities(...)</c> (no extra fluent steps).
    ///   • At least one component used inside the body via <c>entity.Ref&lt;T&gt;/Mut&lt;T&gt;/Read&lt;T&gt;()</c>
    ///     is present in an <c>All&lt;...&gt;</c> filter of TFilter — those components are absorbed into the
    ///     <c>For</c> lambda as <c>ref T</c> / <c>in T</c> parameters.
    ///   • The body has no control-flow that cannot be preserved as a lambda body (no break/continue/
    ///     return/yield/goto/throw/await/nested lambdas/local functions at this level).
    ///   • The body captures at most one external variable/parameter (UserData overload covers the
    ///     1-capture case; 2+ require manual restructuring).
    ///   • Total absorbed components ≤ 6 (For overloads exist for T0..T5).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class ForeachConvertibleToForAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.ForeachEntitiesConvertibleToFor);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.QueryBuilderTerminalMethods.IsEmpty) return;
                if (symbols.QueryEntryMethods.IsEmpty) return;
                if (symbols.QueryFilterAll.IsEmpty) return;

                start.RegisterOperationAction(ctx => Analyze(ctx, symbols), OperationKind.Loop);
            });
        }

        private const int MaxLambdaComponents = 6;

        private static void Analyze(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            if (context.Operation is not IForEachLoopOperation foreachOp) return;

            // Collection must be `someQueryBuilder.Entities(...)`.
            var entitiesInvocation = OperationHelpers.UnwrapImplicitConversions(foreachOp.Collection) as IInvocationOperation;
            if (entitiesInvocation is null) return;
            var entitiesTarget = entitiesInvocation.TargetMethod?.OriginalDefinition;
            if (entitiesTarget is null) return;
            if (entitiesTarget.Name != "Entities") return;
            if (!symbols.QueryBuilderTerminalMethods.Contains(entitiesTarget)) return;

            // Receiver of Entities() must be the Query<...>() entry invocation (no fluent steps in between).
            var queryInvocation = OperationHelpers.UnwrapImplicitConversions(entitiesInvocation.Instance) as IInvocationOperation;
            if (queryInvocation is null) return;
            if (!symbols.QueryEntryMethods.Contains(queryInvocation.TargetMethod.OriginalDefinition)) return;

            // Iteration variable symbol (the `entity` local).
            var entityLocal = ExtractIterationLocal(foreachOp);
            if (entityLocal is null) return;

            // Body shape guards: no break/continue/return/yield/goto/throw/await/nested lambdas.
            if (!IsBodyShapeSupported(foreachOp.Body)) return;

            // Walk all entity usages; classify each as absorbable (Ref/Mut/Read for a component) or "other".
            var bodyOp = foreachOp.Body;
            if (bodyOp is null) return;

            var componentUsage = new Dictionary<ITypeSymbol, ComponentUsage>(SymbolEqualityComparer.Default);

            foreach (var descendant in bodyOp.DescendantsAndSelf()) {
                if (descendant is not ILocalReferenceOperation localRef) continue;
                if (!SymbolEqualityComparer.Default.Equals(localRef.Local, entityLocal)) continue;

                // What consumes this entity reference? Non-absorbable usages are tracked implicitly:
                // any T used through Ref/Mut/Read but not in All<> still ends up in componentUsage and
                // produces the "outside All<>" branch below, signalling that an Entity-param lambda
                // is needed. Pure-`entity` references (passing entity by value, .Has<T>(), etc.) make
                // the lambda need an Entity-param too — but the codefix re-derives this on its side,
                // so the analyzer doesn't need to flag it explicitly.
                if (TryClassifyEntityConsumer(localRef, symbols, out var componentType, out var refKind, out var hadRefLocalName, out var refLocalName)) {
                    if (!componentUsage.TryGetValue(componentType, out var usage)) {
                        usage = new ComponentUsage();
                    }
                    usage.SeenRefKind = MergeRefKind(usage.SeenRefKind, refKind);
                    if (hadRefLocalName && usage.PreferredName is null) usage.PreferredName = refLocalName;
                    componentUsage[componentType] = usage;
                }
            }

            // Decompose TFilter — collect All<> components only (for absorption check). Other filters
            // are pass-through and don't need to be inventoried here; the codefix walks syntax to
            // preserve them.
            var allComponents = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            if (queryInvocation.TargetMethod.TypeArguments.Length > 0) {
                if (!TryCollectAllComponents(queryInvocation.TargetMethod.TypeArguments[0], symbols, allComponents)) {
                    return; // Unsupported filter shape (Or<>, etc.) — bail to avoid wrong rewrite.
                }
            }

            // Decide absorption: a component is absorbed iff it's in some All<> AND used through entity.Ref/Mut/Read.
            var absorbedCount = 0;
            foreach (var pair in componentUsage) {
                if (allComponents.Contains(pair.Key.OriginalDefinition)) {
                    absorbedCount++;
                }
                // Otherwise the call (entity.X<T>()) stays in the body and the codefix will emit the
                // Entity-parameter For overload to keep `entity` accessible inside the lambda.
            }

            if (absorbedCount > MaxLambdaComponents) return; // For<T0..T5> caps at 6.
            // absorbedCount == 0 is allowed — the codefix falls back to the zero-component
            // For(QueryFunctionWithEntity<TWorld>) overload: `For((Entity entity) => { ... })`.

            // Captures: distinct external locals + parameters referenced in the body.
            if (!TryAnalyzeCaptures(bodyOp, entityLocal, foreachOp, out var captureCount, out var capturesSupported, out var hasThisCapture)) {
                return;
            }
            if (!capturesSupported) return;
            if (captureCount > 1) return;
            // `this`-capture + an outer local/parameter together would require packing two things into
            // UserData — V1 keeps the layout simple by skipping such combinations.
            if (hasThisCapture && captureCount >= 1) return;

            // All good — emit the diagnostic on the `foreach` keyword location.
            var location = GetForeachKeywordLocation(foreachOp) ?? foreachOp.Syntax.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(Diagnostics.ForeachEntitiesConvertibleToFor, location));
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private sealed class ComponentUsage {
            public RefKind SeenRefKind = RefKind.None;
            public string PreferredName;
        }

        /// <summary>
        /// 'ref' wins over 'in' for the absorbed parameter — any writable access requires 'ref'.
        /// </summary>
        private static RefKind MergeRefKind(RefKind existing, RefKind incoming) {
            if (existing == RefKind.Ref || incoming == RefKind.Ref) return RefKind.Ref;
            return RefKind.In;
        }

        private static ILocalSymbol ExtractIterationLocal(IForEachLoopOperation foreachOp) {
            // ControlVariable can be a declarator (var entity) or an existing local. We only support the
            // `var entity` form — re-using an outer local in a foreach loop is unusual and not worth
            // supporting in V1.
            switch (foreachOp.LoopControlVariable) {
                case IVariableDeclaratorOperation declarator:
                    return declarator.Symbol;
                case IVariableDeclarationOperation declaration when declaration.Declarators.Length == 1:
                    return declaration.Declarators[0].Symbol;
                default:
                    return null;
            }
        }

        /// <summary>
        /// Returns true if <paramref name="entityRef"/> is the receiver of an <c>entity.Ref&lt;T&gt;()</c>,
        /// <c>entity.Mut&lt;T&gt;()</c>, or <c>entity.Read&lt;T&gt;()</c> call. Outputs the component type
        /// T, the resulting parameter ref-kind (Ref/In), and — when the invocation is the initializer of
        /// a ref-local — the local's name so we can preserve it as the lambda parameter name.
        /// </summary>
        private static bool TryClassifyEntityConsumer(
            ILocalReferenceOperation entityRef,
            StaticEcsSymbols symbols,
            out ITypeSymbol componentType,
            out RefKind refKind,
            out bool hadRefLocalName,
            out string refLocalName) {

            componentType = null;
            refKind = RefKind.None;
            hadRefLocalName = false;
            refLocalName = null;

            if (entityRef.Parent is not IInvocationOperation invocation) return false;
            if (invocation.Instance is null || !ReferenceEquals(invocation.Instance, entityRef)) return false;

            var method = invocation.TargetMethod?.OriginalDefinition;
            if (method is null) return false;

            var isWrite = symbols.RefReturningTargets.Contains(method);
            var isRead = symbols.RefReadonlyReadTargets.Contains(method);
            if (!isWrite && !isRead) return false;
            if (method.Name is not ("Ref" or "Mut" or "Read")) return false;
            if (invocation.TargetMethod.TypeArguments.Length == 0) return false;

            componentType = invocation.TargetMethod.TypeArguments[0];
            refKind = isWrite ? RefKind.Ref : RefKind.In;

            // Detect ref-local binding: the invocation may be the initializer of a `ref var x = ref invocation` declarator.
            var binder = FindRefLocalBinder(invocation);
            if (binder is not null) {
                hadRefLocalName = true;
                refLocalName = binder.Name;
            }
            return true;
        }

        /// <summary>
        /// Walks up from <paramref name="invocation"/>'s parent chain (through implicit conversions)
        /// looking for a ref-local declarator whose initializer is this invocation. Returns the local
        /// symbol or null.
        /// </summary>
        private static ILocalSymbol FindRefLocalBinder(IInvocationOperation invocation) {
            var current = invocation.Parent;
            while (current is IConversionOperation conv && conv.IsImplicit) current = current.Parent;

            if (current is IVariableInitializerOperation init && init.Parent is IVariableDeclaratorOperation declarator
                && declarator.Symbol.RefKind != RefKind.None) {
                return declarator.Symbol;
            }
            // `ref var x = ref entity.Ref<T>()` written via SimpleAssignment in some IR shapes.
            if (current is ISimpleAssignmentOperation assignment
                && assignment.IsRef
                && assignment.Target is ILocalReferenceOperation localTarget
                && localTarget.Local.RefKind != RefKind.None) {
                return localTarget.Local;
            }
            return null;
        }

        /// <summary>
        /// Recursively decomposes <paramref name="filter"/>, accumulating into <paramref name="all"/>
        /// the type arguments of every All&lt;...&gt; node. Returns false if the filter has a shape
        /// that we can't safely modify in the codefix (e.g. Or&lt;...&gt; — opaque types we don't
        /// recognize are tolerated as long as they appear inside And&lt;...&gt; or at the top level,
        /// because the codefix will preserve them verbatim).
        /// </summary>
        private static bool TryCollectAllComponents(ITypeSymbol filter, StaticEcsSymbols symbols, HashSet<ITypeSymbol> all) {
            if (filter is not INamedTypeSymbol named) return false;
            var origDef = named.OriginalDefinition;

            if (symbols.QueryFilterAnd.Contains(origDef)) {
                foreach (var inner in named.TypeArguments) {
                    if (!TryCollectAllComponents(inner, symbols, all)) return false;
                }
                return true;
            }
            if (symbols.QueryFilterAll.Contains(origDef)) {
                foreach (var component in named.TypeArguments) {
                    all.Add(component.OriginalDefinition);
                }
                return true;
            }
            // None/Any/EntityIs*/etc. — opaque pass-through; the codefix preserves them as written.
            if (symbols.QueryFilterNone.Contains(origDef)) return true;
            if (symbols.QueryFilterAny.Contains(origDef)) return true;

            // Any other filter kind (Or<>, future built-ins, user-defined IQueryFilter) — we don't
            // know how to safely modify or pass it through, so abort.
            if (symbols.IQueryFilter is not null) {
                foreach (var iface in named.AllInterfaces) {
                    if (SymbolEqualityComparer.Default.Equals(iface, symbols.IQueryFilter)) {
                        return true; // user-defined filter — leave as-is; we won't touch it.
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Returns false if the body contains any operation that the codefix can't reproduce inside a
        /// lambda (control-flow that targets statements outside the foreach, async, or nested anonymous
        /// functions/local functions whose closure semantics we don't want to entangle with).
        /// </summary>
        private static bool IsBodyShapeSupported(IOperation body) {
            if (body is null) return false;
            foreach (var op in body.DescendantsAndSelf()) {
                switch (op) {
                    case IReturnOperation:
                    case IThrowOperation:
                    case IAwaitOperation:
                    case IAnonymousFunctionOperation:
                    case ILocalFunctionOperation:
                        return false;
                    case IBranchOperation branch:
                        // Break/Goto inside this foreach affect the loop itself and don't have a clean
                        // per-entity-callback equivalent. Continue IS portable — it maps to `return;`
                        // inside the lambda (For continues to the next entity afterwards).
                        if (branch.BranchKind is BranchKind.Break or BranchKind.GoTo) {
                            return false;
                        }
                        break;
                }
            }
            return true;
        }

        /// <summary>
        /// Walks <paramref name="body"/> collecting distinct external symbol captures (locals or
        /// parameters declared outside the foreach). Returns false on unexpected operation shapes that
        /// we don't want to misclassify. <paramref name="capturesSupported"/> is false when the body
        /// references <c>this</c>/an instance field/property/event — V1 can't repack those into UserData.
        /// </summary>
        private static bool TryAnalyzeCaptures(
            IOperation body,
            ILocalSymbol entityLocal,
            IForEachLoopOperation foreachOp,
            out int captureCount,
            out bool capturesSupported,
            out bool hasThisCapture) {

            captureCount = 0;
            capturesSupported = true;
            hasThisCapture = false;
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            var foreachSpan = foreachOp.Syntax.Span;

            foreach (var op in body.DescendantsAndSelf()) {
                switch (op) {
                    case ILocalReferenceOperation localRef: {
                        var local = localRef.Local;
                        if (SymbolEqualityComparer.Default.Equals(local, entityLocal)) break;
                        // Local declared inside the foreach body — internal; skip.
                        if (IsDeclaredInside(local, foreachSpan)) break;
                        if (seen.Add(local)) captureCount++;
                        break;
                    }
                    case IParameterReferenceOperation paramRef:
                        if (seen.Add(paramRef.Parameter)) captureCount++;
                        break;
                    case IFieldReferenceOperation fieldRef: {
                        var field = fieldRef.Field;
                        // Static / const fields don't need capturing — a static lambda can read them
                        // directly via the type. (Includes things like Color.white, Mathf.PI, etc.)
                        if (field.IsStatic || field.IsConst) break;
                        // Field access reached via something OTHER than `this`/`base` is not a capture
                        // of the field itself — it's a member of whatever the receiver resolves to.
                        if (!IsThisOrBase(fieldRef.Instance)) break;
                        // `this.<field>` — fold into the single `this`-capture slot. The codefix
                        // generates a `For(ref this, …)` (or `For(this, …)` for classes) and rewrites
                        // all such accesses through the UserData parameter. Readonly fields are fine
                        // here: we don't take a writable ref to the field, we take ref of `this`.
                        hasThisCapture = true;
                        break;
                    }
                    case IPropertyReferenceOperation propRef
                        when propRef.Property is { IsStatic: false }
                          && IsThisOrBase(propRef.Instance):
                        // Instance property of `this` is reachable via the UserData=`this` parameter
                        // (codefix rewrites the access). Fold into the this-capture slot.
                        hasThisCapture = true;
                        break;
                    case IInstanceReferenceOperation instanceRef:
                        // `this`/`base` as the receiver of an instance method call, or passed as a
                        // value, or any other shape — repack the whole enclosing instance into
                        // UserData via `ref this` / `this`. Object/collection/with initializer
                        // receivers (ReferenceKind != ContainingTypeInstance) are ignored.
                        if (instanceRef.ReferenceKind != InstanceReferenceKind.ContainingTypeInstance) break;
                        hasThisCapture = true;
                        break;
                }
            }
            return true;
        }

        /// <summary>
        /// True iff <paramref name="op"/> is a `this`/`base` reference (not an implicit receiver of
        /// an object/collection/with initializer, not a pattern input, etc.).
        /// </summary>
        private static bool IsThisOrBase(IOperation op) {
            return op is IInstanceReferenceOperation instanceRef
                   && instanceRef.ReferenceKind == InstanceReferenceKind.ContainingTypeInstance;
        }

        private static bool IsDeclaredInside(ISymbol symbol, Microsoft.CodeAnalysis.Text.TextSpan containerSpan) {
            foreach (var location in symbol.Locations) {
                if (!location.IsInSource) continue;
                if (containerSpan.Contains(location.SourceSpan)) return true;
            }
            return false;
        }

        private static Location GetForeachKeywordLocation(IForEachLoopOperation foreachOp) {
            if (foreachOp.Syntax is ForEachStatementSyntax syntax) {
                return syntax.ForEachKeyword.GetLocation();
            }
            return null;
        }
    }
}

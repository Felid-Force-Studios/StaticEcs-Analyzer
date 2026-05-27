using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0042 — <c>entity.Ref&lt;T&gt;()</c> / <c>Mut&lt;T&gt;()</c> / <c>Read&lt;T&gt;()</c> called without a
    /// visible static proof that T is present on the entity. Forward CFG dataflow tracks per-entity
    /// guarantee sets along all paths; intersection at join points (every path must guarantee T).
    ///
    /// Recognised proofs (each grants T for the receiver on a given edge):
    ///   • <c>entity.Has&lt;T...&gt;()</c> / <c>HasEnabled</c> / <c>HasDisabled</c> as the BranchValue of a
    ///     conditional block — added on the true edge only.
    ///   • <c>entity.IsMatch&lt;F&gt;()</c> as BranchValue — F decomposed through <c>And&lt;&gt;</c>,
    ///     collecting <c>All</c>/<c>AllOnlyDisabled</c>/<c>AllWithDisabled</c> components; added on the true edge.
    ///   • Lambda from <c>Query&lt;TFilter&gt;().For(...)</c>: the entity parameter starts with T for every
    ///     T from TFilter's <c>All*</c> components, plus T for every <c>ref T</c>/<c>in T</c> component
    ///     parameter declared in the lambda.
    ///   • <c>Invoke</c> method of an IQuery callback struct: same treatment of <c>ref/in T</c> parameters
    ///     (TFilter is not accessible at this layer — caller-site picks it).
    ///   • Prior <c>Add&lt;T&gt;</c> / <c>Set&lt;T&gt;</c> / <c>Ref&lt;T&gt;</c> / <c>Mut&lt;T&gt;</c> / <c>Read&lt;T&gt;</c> on
    ///     the same tracked entity symbol — added unconditionally on the post-call state.
    ///
    /// Invalidators (clear guarantees on the post-call state):
    ///   • <c>entity.Delete&lt;T&gt;()</c> — drops T only.
    ///   • <c>Destroy</c> / <c>MoveTo</c> / <c>Unload</c> on entity — drops every guarantee for that entity.
    ///   • Re-assignment of the entity variable — drops every guarantee for that local/parameter.
    ///
    /// V1 scope:
    ///   • Entity-instance methods only (<c>Components&lt;T&gt;.Ref/Mut/Read(entity)</c> overloads are not
    ///     check points — they would need to extract the entity from the first argument; left for later).
    ///   • Entity source must be an <see cref="ILocalSymbol"/> or <see cref="IParameterSymbol"/>; any other
    ///     receiver shape (chained call, property/field, <c>default</c>) is reported because no <c>Has</c>
    ///     guard could legally attach to it.
    ///   • Lambdas-from-For are analysed with filter-derived entry state via the Invocation hook; other
    ///     lambdas and non-local-function callables are skipped (V1 conservative under-warning).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RefWithoutPresenceGuardAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.RefWithoutPresenceGuard);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.EntityType is null) return;
                if (symbols.EntityRefAccessMethods.IsEmpty) return;

                start.RegisterOperationBlockAction(ctx => AnalyzeBlocks(ctx, symbols));
                if (!symbols.QueryBuilderForMethods.IsEmpty) {
                    start.RegisterOperationAction(ctx => AnalyzeForInvocation(ctx, symbols), OperationKind.Invocation);
                }
            });
        }

        // ── Entry points ─────────────────────────────────────────────────────────

        private static void AnalyzeBlocks(OperationBlockAnalysisContext context, StaticEcsSymbols symbols) {
            // Top-level method/local-function body. Lambda bodies are not OperationBlocks (Roslyn does
            // not raise OperationBlockAction for them) — they're analysed via AnalyzeForInvocation.
            var owner = context.OwningSymbol as IMethodSymbol;
            var entryState = BuildIQueryInvokeEntryState(owner, symbols);
            foreach (var block in context.OperationBlocks) {
                var cfg = OperationHelpers.TryCreateCfg(block);
                if (cfg is null) continue;
                var foreachGuarantees = CollectForeachGuarantees(block, symbols);
                RunDataflow(cfg, entryState, foreachGuarantees, symbols, context.ReportDiagnostic);
            }
        }

        private static void AnalyzeForInvocation(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var invocation = (IInvocationOperation)context.Operation;
            if (!symbols.QueryBuilderForMethods.Contains(invocation.TargetMethod.OriginalDefinition)) return;
            var filter = ExtractTFilterFromContainingType(invocation.TargetMethod.ContainingType, symbols);
            var allComponents = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            if (filter is not null) CollectAllComponents(filter, symbols, allComponents);

            foreach (var argument in invocation.Arguments) {
                var lambda = OperationHelpers.ExtractLambda(argument.Value);
                if (lambda is null) continue;
                var lambdaCfg = OperationHelpers.TryGetAnonymousFunctionCfg(lambda);
                if (lambdaCfg is null) continue;
                var entry = BuildLambdaEntryState(lambda.Symbol, allComponents, symbols);
                var foreachGuarantees = CollectForeachGuarantees(lambda, symbols);
                RunDataflow(lambdaCfg, entry, foreachGuarantees, symbols, context.ReportDiagnostic);
            }
        }

        // ── Foreach iter-var detection ───────────────────────────────────────────
        // Roslyn lowers `foreach (var x in W.Query<TFilter>().Entities()) body` into a CFG containing
        // an ISimpleAssignmentOperation `x = enumerator.Current` at the head of each iteration. We map
        // such iter-vars to the All*-derived component set of TFilter; at every assignment whose target
        // is one of these locals, the dataflow re-seeds state[local] with those guarantees — so the
        // body starts each iteration with proof that the iter-var holds an entity matching TFilter.

        private static Dictionary<ILocalSymbol, HashSet<ITypeSymbol>> CollectForeachGuarantees(IOperation root, StaticEcsSymbols symbols) {
            Dictionary<ILocalSymbol, HashSet<ITypeSymbol>> map = null;
            foreach (var descendant in root.DescendantsAndSelf()) {
                if (descendant is not IForEachLoopOperation foreachOp) continue;
                var iterLocal = ExtractIterationLocal(foreachOp);
                if (iterLocal is null) continue;
                if (symbols.EntityType is null) continue;
                if (!SymbolEqualityComparer.Default.Equals(iterLocal.Type.OriginalDefinition, symbols.EntityType)) continue;

                var entitiesInv = OperationHelpers.UnwrapImplicitConversions(foreachOp.Collection) as IInvocationOperation;
                if (entitiesInv is null) continue;
                var entitiesTarget = entitiesInv.TargetMethod?.OriginalDefinition;
                if (entitiesTarget is null) continue;
                if (entitiesTarget.Name != "Entities") continue;
                if (!symbols.QueryBuilderTerminalMethods.Contains(entitiesTarget)) continue;

                // Find the Query<TFilter>() entry method anywhere on the receiver chain — fluent
                // builders (e.g. .Write<...>()) may sit between Query and Entities.
                var filter = FindTFilterOnReceiverChain(entitiesInv.Instance, symbols);
                if (filter is null) continue;

                var guarantees = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                CollectAllComponents(filter, symbols, guarantees);
                if (guarantees.Count == 0) continue;
                (map ??= new Dictionary<ILocalSymbol, HashSet<ITypeSymbol>>(SymbolEqualityComparer.Default))[iterLocal] = guarantees;
            }
            return map;
        }

        private static ILocalSymbol ExtractIterationLocal(IForEachLoopOperation foreachOp) {
            switch (foreachOp.LoopControlVariable) {
                case IVariableDeclaratorOperation declarator: return declarator.Symbol;
                case IVariableDeclarationOperation declaration when declaration.Declarators.Length == 1:
                    return declaration.Declarators[0].Symbol;
                default: return null;
            }
        }

        private static ITypeSymbol FindTFilterOnReceiverChain(IOperation receiver, StaticEcsSymbols symbols) {
            var current = receiver;
            while (current is IInvocationOperation invocation) {
                var target = invocation.TargetMethod?.OriginalDefinition;
                if (target is not null && symbols.QueryEntryMethods.Contains(target)
                    && invocation.TargetMethod.TypeArguments.Length >= 1) {
                    return invocation.TargetMethod.TypeArguments[0];
                }
                current = invocation.Instance is null ? null : OperationHelpers.UnwrapImplicitConversions(invocation.Instance);
            }
            // Could also be reached via the containing-type chain when Entities() is on a fluent builder.
            if (receiver is IInvocationOperation entitiesInv) {
                var filter = ExtractTFilterFromContainingType(entitiesInv.TargetMethod.ContainingType, symbols);
                if (filter is not null) return filter;
            }
            return null;
        }

        // ── Entry-state builders ─────────────────────────────────────────────────

        private static Dictionary<ISymbol, HashSet<ITypeSymbol>> BuildLambdaEntryState(
            IMethodSymbol lambda, HashSet<ITypeSymbol> allComponents, StaticEcsSymbols symbols) {

            IParameterSymbol entity = null;
            foreach (var p in lambda.Parameters) {
                if (SymbolEqualityComparer.Default.Equals(p.Type.OriginalDefinition, symbols.EntityType)) {
                    entity = p;
                    break;
                }
            }
            if (entity is null) return null;
            var guarantees = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var component in allComponents) guarantees.Add(component.OriginalDefinition);
            foreach (var parameter in lambda.Parameters) {
                if (ReferenceEquals(parameter, entity)) continue;
                if (parameter.RefKind == RefKind.None) continue;
                guarantees.Add(parameter.Type.OriginalDefinition);
            }
            return new Dictionary<ISymbol, HashSet<ITypeSymbol>>(SymbolEqualityComparer.Default) { [entity] = guarantees };
        }

        private static Dictionary<ISymbol, HashSet<ITypeSymbol>> BuildIQueryInvokeEntryState(
            IMethodSymbol owner, StaticEcsSymbols symbols) {

            if (owner is null) return null;
            if (!IsImplementationOfQueryCallback(owner, symbols)) return null;
            IParameterSymbol entity = null;
            foreach (var p in owner.Parameters) {
                if (SymbolEqualityComparer.Default.Equals(p.Type.OriginalDefinition, symbols.EntityType)) {
                    entity = p;
                    break;
                }
            }
            if (entity is null) return null;
            var guarantees = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var p in owner.Parameters) {
                if (ReferenceEquals(p, entity)) continue;
                if (p.RefKind == RefKind.None) continue;
                guarantees.Add(p.Type.OriginalDefinition);
            }
            return new Dictionary<ISymbol, HashSet<ITypeSymbol>>(SymbolEqualityComparer.Default) { [entity] = guarantees };
        }

        private static bool IsImplementationOfQueryCallback(IMethodSymbol owner, StaticEcsSymbols symbols) {
            if (symbols.QueryCallbackInterfaces.IsEmpty) return false;
            var containing = owner.ContainingType;
            if (containing is null) return false;
            foreach (var iface in containing.AllInterfaces) {
                if (!symbols.QueryCallbackInterfaces.Contains(iface.OriginalDefinition)) continue;
                foreach (var contractMember in iface.GetMembers().OfType<IMethodSymbol>()) {
                    var impl = containing.FindImplementationForInterfaceMember(contractMember);
                    if (SymbolEqualityComparer.Default.Equals(impl, owner)) return true;
                }
            }
            return false;
        }

        // ── Dataflow ─────────────────────────────────────────────────────────────

        private static void RunDataflow(
            ControlFlowGraph cfg,
            Dictionary<ISymbol, HashSet<ITypeSymbol>> entryState,
            Dictionary<ILocalSymbol, HashSet<ITypeSymbol>> foreachGuarantees,
            StaticEcsSymbols symbols,
            Action<Diagnostic> report) {

            int n = cfg.Blocks.Length;
            if (n == 0) return;
            var entries = new Dictionary<ISymbol, HashSet<ITypeSymbol>>[n];
            var visited = new bool[n];
            var queued = new bool[n];
            var work = new Queue<int>();
            var reported = new HashSet<Location>();

            entries[0] = entryState is null
                ? new Dictionary<ISymbol, HashSet<ITypeSymbol>>(SymbolEqualityComparer.Default)
                : CloneState(entryState);
            visited[0] = true;
            queued[0] = true;
            work.Enqueue(0);

            while (work.Count > 0) {
                var idx = work.Dequeue();
                queued[idx] = false;
                var block = cfg.Blocks[idx];
                var state = CloneState(entries[idx]);

                foreach (var op in block.Operations) {
                    ProcessOp(op, state, foreachGuarantees, symbols, report, reported);
                }
                Dictionary<ISymbol, HashSet<ITypeSymbol>> trueExtras = null;
                if (block.BranchValue is not null) {
                    ProcessOp(block.BranchValue, state, foreachGuarantees, symbols, report, reported);
                    trueExtras = ComputeGuardExtras(block.BranchValue, symbols);
                }

                var condDest = block.ConditionalSuccessor?.Destination;
                var fallDest = block.FallThroughSuccessor?.Destination;
                var condKind = block.ConditionKind;

                if (condDest is not null) {
                    var outState = state;
                    if (condKind == ControlFlowConditionKind.WhenTrue && trueExtras is not null) {
                        outState = MergeExtras(state, trueExtras);
                    }
                    Propagate(condDest.Ordinal, outState, entries, visited, queued, work);
                }
                if (fallDest is not null) {
                    var outState = state;
                    if (condKind == ControlFlowConditionKind.WhenFalse && trueExtras is not null) {
                        outState = MergeExtras(state, trueExtras);
                    }
                    Propagate(fallDest.Ordinal, outState, entries, visited, queued, work);
                }
            }
        }

        private static void Propagate(
            int idx,
            Dictionary<ISymbol, HashSet<ITypeSymbol>> exit,
            Dictionary<ISymbol, HashSet<ITypeSymbol>>[] entries,
            bool[] visited,
            bool[] queued,
            Queue<int> work) {

            if (!visited[idx]) {
                entries[idx] = CloneState(exit);
                visited[idx] = true;
                if (!queued[idx]) {
                    queued[idx] = true;
                    work.Enqueue(idx);
                }
                return;
            }
            var current = entries[idx];
            var changed = false;
            // Drop keys missing in exit (intersection: present on every path).
            List<ISymbol> keysToRemove = null;
            foreach (var kv in current) {
                if (!exit.ContainsKey(kv.Key)) {
                    (keysToRemove ??= new List<ISymbol>()).Add(kv.Key);
                }
            }
            if (keysToRemove is not null) {
                foreach (var key in keysToRemove) current.Remove(key);
                changed = true;
            }
            // Intersect type sets for shared keys.
            foreach (var kv in current) {
                var otherSet = exit[kv.Key];
                List<ITypeSymbol> typesToRemove = null;
                foreach (var t in kv.Value) {
                    if (!otherSet.Contains(t)) (typesToRemove ??= new List<ITypeSymbol>()).Add(t);
                }
                if (typesToRemove is not null) {
                    foreach (var t in typesToRemove) kv.Value.Remove(t);
                    changed = true;
                }
            }
            if (changed && !queued[idx]) {
                queued[idx] = true;
                work.Enqueue(idx);
            }
        }

        private static Dictionary<ISymbol, HashSet<ITypeSymbol>> CloneState(Dictionary<ISymbol, HashSet<ITypeSymbol>> source) {
            var result = new Dictionary<ISymbol, HashSet<ITypeSymbol>>(SymbolEqualityComparer.Default);
            if (source is null) return result;
            foreach (var kv in source) {
                result[kv.Key] = new HashSet<ITypeSymbol>(kv.Value, SymbolEqualityComparer.Default);
            }
            return result;
        }

        private static Dictionary<ISymbol, HashSet<ITypeSymbol>> MergeExtras(
            Dictionary<ISymbol, HashSet<ITypeSymbol>> baseState,
            Dictionary<ISymbol, HashSet<ITypeSymbol>> extras) {

            var merged = CloneState(baseState);
            foreach (var kv in extras) {
                if (!merged.TryGetValue(kv.Key, out var set)) {
                    set = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                    merged[kv.Key] = set;
                }
                foreach (var t in kv.Value) set.Add(t);
            }
            return merged;
        }

        // ── Per-operation processing ─────────────────────────────────────────────

        private static void ProcessOp(
            IOperation op,
            Dictionary<ISymbol, HashSet<ITypeSymbol>> state,
            Dictionary<ILocalSymbol, HashSet<ITypeSymbol>> foreachGuarantees,
            StaticEcsSymbols symbols,
            Action<Diagnostic> report,
            HashSet<Location> reported) {

            foreach (var descendant in op.DescendantsAndSelf()) {
                switch (descendant) {
                    case IInvocationOperation inv:
                        HandleInvocation(inv, state, symbols, report, reported);
                        break;
                    case ISimpleAssignmentOperation assign:
                        HandleAssignment(assign, state, foreachGuarantees);
                        break;
                    case IArgumentOperation arg
                        when arg.Parameter?.RefKind is RefKind.Ref or RefKind.Out:
                        HandleRefOutArgument(arg, state, symbols);
                        break;
                }
            }
        }

        private static void HandleInvocation(
            IInvocationOperation inv,
            Dictionary<ISymbol, HashSet<ITypeSymbol>> state,
            StaticEcsSymbols symbols,
            Action<Diagnostic> report,
            HashSet<Location> reported) {

            var target = inv.TargetMethod?.OriginalDefinition;
            if (target is null) return;

            // Check point: entity.Ref/Mut/Read<T>().
            if (symbols.EntityRefAccessMethods.Contains(target) && inv.TargetMethod.TypeArguments.Length == 1) {
                var componentType = inv.TargetMethod.TypeArguments[0];
                var source = TryGetEntitySource(inv.Instance);
                var guaranteed = source is not null
                                 && state.TryGetValue(source, out var existing)
                                 && existing.Contains(componentType.OriginalDefinition);
                if (!guaranteed && !IsSuppressedByNullForgivingOperator(inv)) {
                    var location = inv.Syntax.GetLocation();
                    if (reported.Add(location)) {
                        report(Diagnostic.Create(
                            Diagnostics.RefWithoutPresenceGuard,
                            location,
                            inv.TargetMethod.Name,
                            componentType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                    }
                }
                if (source is not null) AddGuarantee(state, source, componentType);
                return;
            }

            // Add<T> / Set<T> establish T for subsequent calls.
            if ((symbols.EntityAddMethods.Contains(target) || symbols.EntitySetMethods.Contains(target))
                && inv.TargetMethod.TypeArguments.Length >= 1) {
                var source = TryGetEntitySource(inv.Instance);
                if (source is not null) AddGuarantee(state, source, inv.TargetMethod.TypeArguments[0]);
                return;
            }

            // Delete<T> — drop only T.
            if (symbols.EntityComponentInvalidators.Contains(target) && inv.TargetMethod.TypeArguments.Length >= 1) {
                var source = TryGetEntitySource(inv.Instance);
                if (source is not null && state.TryGetValue(source, out var setForSource)) {
                    setForSource.Remove(inv.TargetMethod.TypeArguments[0].OriginalDefinition);
                }
                return;
            }

            // Destroy / MoveTo / Unload — drop all for this entity.
            if (symbols.EntityFullInvalidators.Contains(target)) {
                var source = TryGetEntitySource(inv.Instance);
                if (source is not null) state.Remove(source);
            }
        }

        private static void HandleAssignment(
            ISimpleAssignmentOperation assign,
            Dictionary<ISymbol, HashSet<ITypeSymbol>> state,
            Dictionary<ILocalSymbol, HashSet<ITypeSymbol>> foreachGuarantees) {

            // ref-assignment is rebinding a ref local — leave handling to the ref-pass-through rules.
            if (assign.IsRef) return;
            switch (assign.Target) {
                case ILocalReferenceOperation localRef:
                    // Detect the lowered foreach iter-var re-binding (`x = enumerator.Current`):
                    // if the local matches a recognised foreach iter-var, re-seed its guarantees so the
                    // loop body sees the All*-derived components fresh on every iteration. The same
                    // re-seeding also fires on rare user-side `entity = ...` reassignments — that's an
                    // accepted under-warn (favours fewer false positives over fewer false negatives).
                    if (foreachGuarantees is not null && foreachGuarantees.TryGetValue(localRef.Local, out var seed)) {
                        state[localRef.Local] = new HashSet<ITypeSymbol>(seed, SymbolEqualityComparer.Default);
                    } else {
                        state.Remove(localRef.Local);
                    }
                    break;
                case IParameterReferenceOperation paramRef:
                    state.Remove(paramRef.Parameter);
                    break;
            }
        }

        private static void HandleRefOutArgument(
            IArgumentOperation arg,
            Dictionary<ISymbol, HashSet<ITypeSymbol>> state,
            StaticEcsSymbols symbols) {

            var target = ExtractRefOutTargetSymbol(arg.Value);
            if (target is null) return;

            // `out Entity` argument of a query-builder fluent terminal (e.g. `query.One(out var e)`) —
            // the callee guarantees e matches the query's TFilter. Seed All*-derived guarantees so the
            // local is treated as already-checked for those components.
            if (arg.Parameter.RefKind == RefKind.Out
                && symbols.EntityType is not null
                && arg.Parameter.Type is not null
                && SymbolEqualityComparer.Default.Equals(arg.Parameter.Type.OriginalDefinition, symbols.EntityType)
                && arg.Parent is IInvocationOperation invocation
                && invocation.TargetMethod?.ContainingType is INamedTypeSymbol containing
                && symbols.IsQueryBuilderType(containing)) {
                var filter = ExtractTFilterFromContainingType(containing, symbols);
                if (filter is not null) {
                    var guarantees = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                    CollectAllComponents(filter, symbols, guarantees);
                    if (guarantees.Count > 0) {
                        state[target] = guarantees;
                        return;
                    }
                }
            }

            // Generic ref/out: callee may overwrite — drop guarantees on the local.
            state.Remove(target);
        }

        private static ISymbol ExtractRefOutTargetSymbol(IOperation value) {
            var current = OperationHelpers.UnwrapImplicitConversions(value);
            // `out var x` is represented as IDeclarationExpressionOperation around an ILocalReference.
            if (current is IDeclarationExpressionOperation decl) current = decl.Expression;
            return current switch {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                _ => null,
            };
        }

        private static void AddGuarantee(
            Dictionary<ISymbol, HashSet<ITypeSymbol>> state,
            ISymbol source,
            ITypeSymbol componentType) {

            if (!state.TryGetValue(source, out var set)) {
                set = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                state[source] = set;
            }
            set.Add(componentType.OriginalDefinition);
        }

        // ── BranchValue → guard extras ───────────────────────────────────────────

        private static Dictionary<ISymbol, HashSet<ITypeSymbol>> ComputeGuardExtras(
            IOperation branchValue,
            StaticEcsSymbols symbols) {

            var current = OperationHelpers.UnwrapImplicitConversions(branchValue);
            if (current is not IInvocationOperation inv) return null;
            var target = inv.TargetMethod?.OriginalDefinition;
            if (target is null) return null;

            if (symbols.EntityHasMethods.Contains(target)) {
                var source = TryGetEntitySource(inv.Instance);
                if (source is null) return null;
                var set = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                foreach (var t in inv.TargetMethod.TypeArguments) set.Add(t.OriginalDefinition);
                if (set.Count == 0) return null;
                return new Dictionary<ISymbol, HashSet<ITypeSymbol>>(SymbolEqualityComparer.Default) { [source] = set };
            }

            if (symbols.EntityIsMatch is not null
                && SymbolEqualityComparer.Default.Equals(target, symbols.EntityIsMatch)
                && inv.TargetMethod.TypeArguments.Length == 1) {
                var source = TryGetEntitySource(inv.Instance);
                if (source is null) return null;
                var set = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
                CollectAllComponents(inv.TargetMethod.TypeArguments[0], symbols, set);
                if (set.Count == 0) return null;
                return new Dictionary<ISymbol, HashSet<ITypeSymbol>>(SymbolEqualityComparer.Default) { [source] = set };
            }
            return null;
        }

        // ── Filter decomposition (All / AllOnlyDisabled / AllWithDisabled, recursing through And) ──

        private static void CollectAllComponents(ITypeSymbol filter, StaticEcsSymbols symbols, HashSet<ITypeSymbol> accumulator) {
            if (filter is not INamedTypeSymbol named) return;
            var origDef = named.OriginalDefinition;
            if (symbols.QueryFilterAnd.Contains(origDef)) {
                foreach (var inner in named.TypeArguments) CollectAllComponents(inner, symbols, accumulator);
                return;
            }
            if (symbols.QueryFilterAll.Contains(origDef)) {
                foreach (var component in named.TypeArguments) accumulator.Add(component.OriginalDefinition);
            }
        }

        private static INamedTypeSymbol ExtractTFilterFromContainingType(INamedTypeSymbol containingType, StaticEcsSymbols symbols) {
            if (symbols.IQueryFilter is null) return null;
            var current = containingType;
            while (current is not null) {
                if (current.TypeArguments.Length >= 1
                    && current.TypeArguments[0] is INamedTypeSymbol candidate
                    && ImplementsIQueryFilter(candidate, symbols)) {
                    return candidate;
                }
                current = current.ContainingType;
            }
            return null;
        }

        private static bool ImplementsIQueryFilter(INamedTypeSymbol type, StaticEcsSymbols symbols) {
            foreach (var iface in type.AllInterfaces) {
                if (SymbolEqualityComparer.Default.Equals(iface, symbols.IQueryFilter)) return true;
            }
            return false;
        }

        // ── Entity source extraction ─────────────────────────────────────────────

        // ── Per-call escape hatch ────────────────────────────────────────────────
        // `entity.Ref<T>()!` — the null-forgiving postfix wraps the entire invocation. Roslyn lowers
        // this to a PostfixUnaryExpressionSyntax(SuppressNullableWarningExpression) over the invocation
        // syntax. Receiver-side `entity!.Ref<T>()` is NOT honored: the marker must annotate the
        // specific component access, not the entity itself.
        private static bool IsSuppressedByNullForgivingOperator(IInvocationOperation inv) {
            return inv.Syntax.Parent is Microsoft.CodeAnalysis.CSharp.Syntax.PostfixUnaryExpressionSyntax post
                   && post.IsKind(Microsoft.CodeAnalysis.CSharp.SyntaxKind.SuppressNullableWarningExpression);
        }

        private static ISymbol TryGetEntitySource(IOperation receiver) {
            if (receiver is null) return null;
            var unwrapped = OperationHelpers.UnwrapImplicitConversions(receiver);
            return unwrapped switch {
                ILocalReferenceOperation local => local.Local,
                IParameterReferenceOperation parameter => parameter.Parameter,
                _ => null,
            };
        }
    }
}

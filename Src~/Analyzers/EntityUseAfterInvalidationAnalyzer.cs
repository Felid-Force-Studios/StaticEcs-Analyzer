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
    /// FFSECS0040 — Component alias (ref/in parameter, or ref-local backed by entity.Ref/Mut/Add/Read)
    /// used after the source entity was invalidated.
    ///
    /// Invalidators:
    ///   • Full kill — Destroy/MoveTo/Unload — invalidates every alias of the entity.
    ///   • Per-T kill — Delete&lt;T&gt; — invalidates only aliases of component type T.
    ///
    /// Two alias sources, with different scopes:
    ///   • Parameter aliases — `(W.Entity, ref/in T)` signature where the framework guarantees the
    ///     binding (`WorldQuery.For` lambda, or `Invoke` of an `IQuery` callback struct).
    ///     We do NOT generalise to arbitrary user methods with that signature: outside the framework
    ///     the convention is unenforced and reporting would be a false positive.
    ///   • Ref-local aliases — `ref var x = ref source.Ref&lt;T&gt;()` and the like. Universal across
    ///     any body (method/lambda/local function), reachable via <see cref="OperationHelpers.WalkCfgRecursive"/>.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class EntityUseAfterInvalidationAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.EntityUseAfterInvalidation);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.EntityType is null) return;
                if (symbols.EntityFullInvalidators.IsEmpty && symbols.EntityComponentInvalidators.IsEmpty) return;
                start.RegisterOperationBlockAction(ctx => AnalyzeBlocks(ctx, symbols));
                if (!symbols.QueryBuilderForMethods.IsEmpty) {
                    start.RegisterOperationAction(ctx => AnalyzeForInvocation(ctx, symbols), OperationKind.Invocation);
                }
            });
        }

        private readonly struct Alias {
            public readonly ISymbol AliasSymbol;       // parameter or local
            public readonly ISymbol Source;            // entity-typed local or parameter
            public readonly ITypeSymbol ComponentType; // T of ref T / in T / Ref<T>()
            public Alias(ISymbol alias, ISymbol source, ITypeSymbol type) {
                AliasSymbol = alias; Source = source; ComponentType = type;
            }
        }

        // Top-level body action:
        //   • Parameter aliases — only if owner is an Invoke implementation of an IQuery callback;
        //     applied to the top-level CFG (lambda bodies inside are handled by AnalyzeForInvocation).
        //   • Ref-local aliases — universal Pattern C across every CFG in this block, including
        //     nested lambdas and local functions.
        private static void AnalyzeBlocks(OperationBlockAnalysisContext context, StaticEcsSymbols symbols) {
            var owner = context.OwningSymbol as IMethodSymbol;
            var isCallback = IsImplementationOfQueryCallback(owner, symbols);
            foreach (var block in context.OperationBlocks) {
                var topCfg = OperationHelpers.TryCreateCfg(block);
                if (topCfg is null) continue;
                if (isCallback) AnalyzeParameterAliases(topCfg, owner, symbols, context.ReportDiagnostic);
                OperationHelpers.WalkCfgRecursive(topCfg, owner, (cfg, _) =>
                    AnalyzeRefLocalAliases(cfg, symbols, context.ReportDiagnostic));
            }
        }

        // Lambda passed to a query-builder `For` — framework guarantees the
        // (entity, ref/in component) parameter convention.
        private static void AnalyzeForInvocation(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var invocation = (IInvocationOperation)context.Operation;
            if (!symbols.QueryBuilderForMethods.Contains(invocation.TargetMethod.OriginalDefinition)) return;
            foreach (var argument in invocation.Arguments) {
                var lambda = OperationHelpers.ExtractLambda(argument.Value);
                if (lambda is null) continue;
                var lambdaCfg = OperationHelpers.TryGetAnonymousFunctionCfg(lambda);
                if (lambdaCfg is not null) AnalyzeParameterAliases(lambdaCfg, lambda.Symbol, symbols, context.ReportDiagnostic);
            }
        }

        private static bool IsImplementationOfQueryCallback(IMethodSymbol owner, StaticEcsSymbols symbols) {
            if (owner is null || symbols.QueryCallbackInterfaces.IsEmpty) return false;
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

        private static void AnalyzeParameterAliases(ControlFlowGraph cfg, IMethodSymbol owner, StaticEcsSymbols symbols, Action<Diagnostic> report) {
            var aliases = new List<Alias>();
            CollectParameterAliases(owner, symbols, aliases);
            if (aliases.Count == 0) return;
            foreach (var alias in aliases) RunDataflow(cfg, alias, symbols, report);
        }

        private static void AnalyzeRefLocalAliases(ControlFlowGraph cfg, StaticEcsSymbols symbols, Action<Diagnostic> report) {
            var aliases = new List<Alias>();
            CollectRefLocalAliases(cfg, symbols, aliases);
            if (aliases.Count == 0) return;
            foreach (var alias in aliases) RunDataflow(cfg, alias, symbols, report);
        }

        private static void CollectParameterAliases(IMethodSymbol owner, StaticEcsSymbols symbols, List<Alias> aliases) {
            if (owner is null || owner.Parameters.Length < 2) return;
            IParameterSymbol entity = null;
            foreach (var p in owner.Parameters) {
                if (SymbolEqualityComparer.Default.Equals(p.Type.OriginalDefinition, symbols.EntityType)) {
                    entity = p;
                    break;
                }
            }
            if (entity is null) return;
            foreach (var p in owner.Parameters) {
                if (ReferenceEquals(p, entity)) continue;
                if (p.RefKind == RefKind.None) continue;
                aliases.Add(new Alias(p, entity, p.Type));
            }
        }

        // CFG lowers `ref var x = ref source.Ref<T>()` to ISimpleAssignmentOperation(IsRef=true,
        // Target=local, Value=invocation). Register the (local → source, T) tuple once; rebinds
        // within the body are treated as kills by the dataflow.
        private static void CollectRefLocalAliases(ControlFlowGraph cfg, StaticEcsSymbols symbols, List<Alias> aliases) {
            if (symbols.RefReturningTargets.IsEmpty && symbols.RefReadonlyReadTargets.IsEmpty) return;
            var seen = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var block in cfg.Blocks) {
                foreach (var op in block.Operations) Scan(op);
                if (block.BranchValue != null) Scan(block.BranchValue);
            }

            void Scan(IOperation root) {
                foreach (var d in root.DescendantsAndSelf()) {
                    if (d is not ISimpleAssignmentOperation a || !a.IsRef) continue;
                    if (a.Target is not ILocalReferenceOperation aliasRef) continue;
                    var value = OperationHelpers.UnwrapImplicitConversions(a.Value);
                    if (value is not IInvocationOperation inv) continue;
                    var target = inv.TargetMethod.OriginalDefinition;
                    if (!symbols.RefReturningTargets.Contains(target) && !symbols.RefReadonlyReadTargets.Contains(target)) continue;
                    if (!TryGetEntitySource(inv.Instance, symbols, out var source)) continue;
                    if (!seen.Add(aliasRef.Local)) continue;
                    var componentType = inv.TargetMethod.TypeArguments.Length > 0 ? inv.TargetMethod.TypeArguments[0] : null;
                    aliases.Add(new Alias(aliasRef.Local, source, componentType));
                }
            }
        }

        // Forward worklist dataflow per alias.
        //   Gen:   invalidator on alias.Source affecting alias.ComponentType (full or per-T match).
        //   Kill:  `ref alias = ref ...` re-binding (only ref-locals can be rebound; parameter aliases cannot).
        //   Merge: union.
        // Within one top-op kill wins over gen.
        private static void RunDataflow(ControlFlowGraph cfg, Alias alias, StaticEcsSymbols symbols, Action<Diagnostic> report) {
            int n = cfg.Blocks.Length;
            var entryTainted = new bool[n];
            var entryInvalidator = new string[n];
            var ever = new bool[n];
            var queued = new bool[n];
            var reported = new HashSet<Location>();
            var work = new Queue<int>();

            work.Enqueue(0);
            queued[0] = true;
            ever[0] = true;

            while (work.Count > 0) {
                int idx = work.Dequeue();
                queued[idx] = false;
                var block = cfg.Blocks[idx];

                bool tainted = entryTainted[idx];
                string invalidator = entryInvalidator[idx];

                foreach (var op in block.Operations)
                    ProcessOp(op, alias, ref tainted, ref invalidator, symbols, report, reported);
                if (block.BranchValue != null)
                    ProcessOp(block.BranchValue, alias, ref tainted, ref invalidator, symbols, report, reported);

                Propagate(block.FallThroughSuccessor?.Destination, tainted, invalidator, entryTainted, entryInvalidator, ever, queued, work);
                Propagate(block.ConditionalSuccessor?.Destination, tainted, invalidator, entryTainted, entryInvalidator, ever, queued, work);
            }
        }

        private static void Propagate(BasicBlock successor, bool exitTainted, string exitInvalidator,
                                      bool[] entryTainted, string[] entryInvalidator, bool[] ever, bool[] queued, Queue<int> work) {
            if (successor is null) return;
            int idx = successor.Ordinal;
            bool changed = exitTainted && !entryTainted[idx];
            if (changed) {
                entryTainted[idx] = true;
                entryInvalidator[idx] = exitInvalidator;
            }
            if (!ever[idx]) ever[idx] = true;
            else if (!changed) return;
            if (!queued[idx]) {
                queued[idx] = true;
                work.Enqueue(idx);
            }
        }

        private static void ProcessOp(IOperation op, Alias alias, ref bool tainted, ref string invalidator,
                                      StaticEcsSymbols symbols, Action<Diagnostic> report, HashSet<Location> reported) {
            bool gen = false;
            string genName = null;
            bool kill = false;

            foreach (var d in op.DescendantsAndSelf()) {
                if (d is IInvocationOperation inv
                    && TryClassifyInvalidator(inv, symbols, out var full, out var killType)
                    && IsInvocationOnSource(inv, alias.Source)
                    && Affects(full, killType, alias.ComponentType)) {
                    gen = true;
                    genName = inv.TargetMethod.Name;
                }
                if (IsRebinding(d, alias.AliasSymbol)) {
                    kill = true;
                }
            }

            if (tainted) {
                foreach (var d in op.DescendantsAndSelf()) {
                    if (!IsRefToAlias(d, alias.AliasSymbol)) continue;
                    if (IsRebindLhs(d, alias.AliasSymbol)) continue;
                    var loc = d.Syntax.GetLocation();
                    if (!reported.Add(loc)) continue;
                    report(Diagnostic.Create(Diagnostics.EntityUseAfterInvalidation, loc, alias.AliasSymbol.Name, invalidator));
                }
            }

            if (kill) {
                tainted = false;
                invalidator = null;
            } else if (gen) {
                tainted = true;
                invalidator = genName;
            }
        }

        private static bool TryClassifyInvalidator(IInvocationOperation inv, StaticEcsSymbols symbols, out bool fullKill, out ITypeSymbol killType) {
            fullKill = false;
            killType = null;
            var target = inv.TargetMethod.OriginalDefinition;
            if (symbols.EntityFullInvalidators.Contains(target)) {
                fullKill = true;
                return true;
            }
            if (symbols.EntityComponentInvalidators.Contains(target) && inv.TargetMethod.TypeArguments.Length > 0) {
                killType = inv.TargetMethod.TypeArguments[0];
                return true;
            }
            return false;
        }

        private static bool Affects(bool fullKill, ITypeSymbol killType, ITypeSymbol componentType) {
            if (fullKill) return true;
            if (killType is null || componentType is null) return false;
            return SymbolEqualityComparer.Default.Equals(killType.OriginalDefinition, componentType.OriginalDefinition);
        }

        private static bool IsInvocationOnSource(IInvocationOperation inv, ISymbol source) {
            if (inv.Instance is null) return false;
            var receiver = OperationHelpers.UnwrapImplicitConversions(inv.Instance);
            switch (receiver) {
                case ILocalReferenceOperation l: return SymbolEqualityComparer.Default.Equals(l.Local, source);
                case IParameterReferenceOperation p: return SymbolEqualityComparer.Default.Equals(p.Parameter, source);
                default: return false;
            }
        }

        private static bool TryGetEntitySource(IOperation receiver, StaticEcsSymbols symbols, out ISymbol source) {
            source = null;
            if (receiver is null) return false;
            var unwrapped = OperationHelpers.UnwrapImplicitConversions(receiver);
            ITypeSymbol type;
            switch (unwrapped) {
                case ILocalReferenceOperation local: source = local.Local; type = local.Local.Type; break;
                case IParameterReferenceOperation param: source = param.Parameter; type = param.Parameter.Type; break;
                default: return false;
            }
            if (!SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, symbols.EntityType)) {
                source = null;
                return false;
            }
            return true;
        }

        private static bool IsRefToAlias(IOperation op, ISymbol alias) {
            switch (op) {
                case ILocalReferenceOperation l: return SymbolEqualityComparer.Default.Equals(l.Local, alias);
                case IParameterReferenceOperation p: return SymbolEqualityComparer.Default.Equals(p.Parameter, alias);
                default: return false;
            }
        }

        private static bool IsRebinding(IOperation op, ISymbol alias) {
            if (op is not ISimpleAssignmentOperation a || !a.IsRef) return false;
            if (a.Target is not ILocalReferenceOperation l) return false;
            return SymbolEqualityComparer.Default.Equals(l.Local, alias);
        }

        private static bool IsRebindLhs(IOperation op, ISymbol alias) {
            if (op.Parent is not ISimpleAssignmentOperation a || !a.IsRef) return false;
            if (!ReferenceEquals(a.Target, op)) return false;
            return IsRefToAlias(op, alias);
        }
    }
}

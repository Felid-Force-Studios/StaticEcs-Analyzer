using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0041 — Entity variable (local or parameter) used after Destroy/MoveTo/Unload.
    /// Reassignment of the variable (including <c>out</c> writes) clears the taint.
    ///
    /// Universal: one entry point analyses every method/initializer/local-function body via
    /// <see cref="OperationBlockAnalysisContext"/>, then walks nested anonymous functions through
    /// the CFG's anonymous-function region API. Lambdas in any context — <c>Action</c> fields,
    /// <c>Task.Run</c>, custom helpers, <c>WorldQuery.For</c> — are covered without name matching.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class EntityHandleUseAfterInvalidationAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.EntityHandleUseAfterInvalidation);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.EntityType is null || symbols.EntityFullInvalidators.IsEmpty) return;
                start.RegisterOperationBlockAction(ctx => AnalyzeBlocks(ctx, symbols));
            });
        }

        private static void AnalyzeBlocks(OperationBlockAnalysisContext context, StaticEcsSymbols symbols) {
            var owner = context.OwningSymbol as IMethodSymbol;
            foreach (var block in context.OperationBlocks) {
                OperationHelpers.WalkCfgRecursive(block, owner, (cfg, _) => AnalyzeCfg(cfg, symbols, context.ReportDiagnostic));
            }
        }

        private static void AnalyzeCfg(ControlFlowGraph cfg, StaticEcsSymbols symbols, Action<Diagnostic> report) {
            var candidates = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var block in cfg.Blocks) {
                foreach (var op in block.Operations) CollectCandidates(op, symbols, candidates);
                if (block.BranchValue != null) CollectCandidates(block.BranchValue, symbols, candidates);
            }
            foreach (var variable in candidates) {
                RunDataflow(cfg, variable, symbols, report);
            }
        }

        private static void CollectCandidates(IOperation op, StaticEcsSymbols symbols, HashSet<ISymbol> candidates) {
            foreach (var d in op.DescendantsAndSelf()) {
                if (d is IInvocationOperation inv
                    && IsInvalidator(inv, symbols)
                    && TryGetEntityVariable(inv.Instance, symbols, out var variable)) {
                    candidates.Add(variable);
                }
            }
        }

        // Standard forward worklist dataflow.
        //   Merge:  union  — entry is tainted if any predecessor exited tainted.
        //   Gen:    invalidator call on the variable.
        //   Kill:   any reassignment of the variable (incl. `out` argument).
        // Within one top-op kill wins over gen so `entity = entity.MoveTo(...)` ends clean.
        private static void RunDataflow(ControlFlowGraph cfg, ISymbol variable, StaticEcsSymbols symbols, Action<Diagnostic> report) {
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
                    ProcessOp(op, variable, ref tainted, ref invalidator, symbols, report, reported);
                if (block.BranchValue != null)
                    ProcessOp(block.BranchValue, variable, ref tainted, ref invalidator, symbols, report, reported);

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

        private static void ProcessOp(IOperation op, ISymbol variable, ref bool tainted, ref string invalidator,
                                      StaticEcsSymbols symbols, Action<Diagnostic> report, HashSet<Location> reported) {
            bool gen = false;
            string genName = null;
            bool kill = false;

            foreach (var d in op.DescendantsAndSelf()) {
                if (d is IInvocationOperation inv
                    && IsInvalidator(inv, symbols)
                    && TryGetEntityVariable(inv.Instance, symbols, out var receiver)
                    && SymbolEqualityComparer.Default.Equals(receiver, variable)) {
                    gen = true;
                    genName = inv.TargetMethod.Name;
                }
                if (IsReassignment(d, variable) || IsOutWrite(d, variable)) {
                    kill = true;
                }
            }

            if (tainted) {
                foreach (var d in op.DescendantsAndSelf()) {
                    if (!IsRefTo(d, variable)) continue;
                    if (IsInvalidatorReceiver(d, variable, symbols)) continue;
                    if (IsReassignmentLhs(d, variable)) continue;
                    if (IsInsideOutArg(d)) continue;
                    var loc = d.Syntax.GetLocation();
                    if (!reported.Add(loc)) continue;
                    report(Diagnostic.Create(Diagnostics.EntityHandleUseAfterInvalidation, loc, variable.Name, invalidator));
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

        private static bool IsInvalidator(IInvocationOperation inv, StaticEcsSymbols symbols) =>
            symbols.EntityFullInvalidators.Contains(inv.TargetMethod.OriginalDefinition);

        private static bool TryGetEntityVariable(IOperation receiver, StaticEcsSymbols symbols, out ISymbol variable) {
            variable = null;
            if (receiver is null) return false;
            var unwrapped = OperationHelpers.UnwrapImplicitConversions(receiver);
            ITypeSymbol type;
            switch (unwrapped) {
                case ILocalReferenceOperation local: variable = local.Local; type = local.Local.Type; break;
                case IParameterReferenceOperation param: variable = param.Parameter; type = param.Parameter.Type; break;
                default: return false;
            }
            if (!SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, symbols.EntityType)) {
                variable = null;
                return false;
            }
            return true;
        }

        private static bool IsRefTo(IOperation op, ISymbol variable) {
            switch (op) {
                case ILocalReferenceOperation l: return SymbolEqualityComparer.Default.Equals(l.Local, variable);
                case IParameterReferenceOperation p: return SymbolEqualityComparer.Default.Equals(p.Parameter, variable);
                default: return false;
            }
        }

        private static bool IsInvalidatorReceiver(IOperation op, ISymbol variable, StaticEcsSymbols symbols) {
            // Receiver may be wrapped in an implicit conversion (e.g. boxing); walk up to the invocation.
            var parent = op.Parent;
            while (parent is IConversionOperation { IsImplicit: true }) parent = parent.Parent;
            if (parent is not IInvocationOperation inv || inv.Instance is null) return false;
            if (!ReferenceEquals(OperationHelpers.UnwrapImplicitConversions(inv.Instance), op)) return false;
            if (!IsInvalidator(inv, symbols)) return false;
            return TryGetEntityVariable(inv.Instance, symbols, out var receiver)
                   && SymbolEqualityComparer.Default.Equals(receiver, variable);
        }

        private static bool IsReassignment(IOperation op, ISymbol variable) =>
            op is ISimpleAssignmentOperation assignment && IsRefTo(assignment.Target, variable);

        private static bool IsReassignmentLhs(IOperation op, ISymbol variable) =>
            op.Parent is ISimpleAssignmentOperation assignment
            && ReferenceEquals(assignment.Target, op)
            && IsRefTo(op, variable);

        private static bool IsOutWrite(IOperation op, ISymbol variable) {
            if (op is not IArgumentOperation arg) return false;
            if (arg.Parameter?.RefKind != RefKind.Out) return false;
            var value = arg.Value;
            if (value is IDeclarationExpressionOperation decl) value = decl.Expression;
            return IsRefTo(value, variable);
        }

        private static bool IsInsideOutArg(IOperation op) {
            var parent = op.Parent;
            if (parent is IDeclarationExpressionOperation) parent = parent.Parent;
            return parent is IArgumentOperation arg && arg.Parameter?.RefKind == RefKind.Out;
        }
    }
}

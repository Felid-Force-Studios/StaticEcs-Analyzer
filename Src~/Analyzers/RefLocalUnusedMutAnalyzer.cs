using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0013 — a writable ref obtained from a StaticEcs ref-returning member (Entity.Ref/Mut,
    /// Components.Ref/Mut, Resource/NamedResource.Value, Multi.First/Last/[int], MultiComponentsIterator.Current)
    /// is used only for reading. Two shapes are detected:
    ///
    /// <para>1) Ref-local binding: <c>ref var x = ref entity.Ref&lt;T&gt;()</c> where <c>x</c> is never
    /// mutated. CFG-based pass.</para>
    ///
    /// <para>2) Inline chain: <c>entity.Ref&lt;T&gt;().Field</c> consumed as a read (var initializer,
    /// in/by-value argument, condition, ...). Single-operation walk upward via
    /// <see cref="OperationHelpers.TryClassifyInlineRefRead"/>.</para>
    ///
    /// "Mutation" is defined conservatively (any of the below → not a false positive):
    ///   • Direct write: local = ... / local.Field = ... / local[idx] = ... (incl. compound assignment).
    ///   • Increment / decrement.
    ///   • Pass-by-ref/out: Method(ref local) / Method(out local.Field).
    ///   • Writable ref-alias creation: ref var alias = ref local / ref local.Field (RefKind.Ref).
    ///   • Instance method call on a non-readonly struct (compiler may use writability for 'this').
    /// 'in'-passing, by-value-passing, field reads, and 'ref readonly' alias creation are NOT mutations.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RefLocalUnusedMutAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.RefLocalNeverMutated);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.RefReturningTargets.IsEmpty) return;
                start.RegisterOperationBlockAction(ctx => AnalyzeBlocks(ctx, symbols));
                start.RegisterOperationAction(
                    ctx => AnalyzeInlineRefDrop(ctx, symbols),
                    OperationKind.Invocation,
                    OperationKind.PropertyReference);
            });
        }

        private static void AnalyzeInlineRefDrop(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var op = context.Operation;
            ISymbol target = op switch {
                IInvocationOperation invoc => invoc.TargetMethod?.OriginalDefinition,
                IPropertyReferenceOperation prop => prop.Property?.OriginalDefinition,
                _ => null,
            };
            if (target is null) return;
            // RefTargetReadSiblings already excludes members without a read sibling (e.g. Add) — the
            // TryGetValue inside TryClassifyInlineRefRead will fail for them and short-circuit.
            if (!symbols.RefTargetReadSiblings.ContainsKey(target)) return;

            var containingMethod = context.ContainingSymbol as IMethodSymbol;
            if (!OperationHelpers.TryClassifyInlineRefRead(op, symbols, containingMethod, out var sibling, out var location)) {
                return;
            }
            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.RefLocalNeverMutated,
                location,
                FormatMemberLabel(target),
                sibling));
        }

        private static string FormatMemberLabel(ISymbol member) {
            return member.ContainingType?.Name is { } ct ? ct + "." + member.Name : member.Name;
        }

        private static void AnalyzeBlocks(OperationBlockAnalysisContext context, StaticEcsSymbols symbols) {
            var owner = context.OwningSymbol as IMethodSymbol;
            foreach (var block in context.OperationBlocks) {
                OperationHelpers.WalkCfgRecursive(block, owner, (cfg, _) => AnalyzeCfg(cfg, symbols, context.ReportDiagnostic));
            }
        }

        private static void AnalyzeCfg(ControlFlowGraph cfg, StaticEcsSymbols symbols, Action<Diagnostic> report) {
            Dictionary<ILocalSymbol, TrackedBinding> tracked = null;
            foreach (var block in cfg.Blocks) {
                foreach (var op in block.Operations) CollectTracked(op, symbols, ref tracked);
                if (block.BranchValue != null) CollectTracked(block.BranchValue, symbols, ref tracked);
            }
            if (tracked is null || tracked.Count == 0) return;

            var mutated = new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
            foreach (var block in cfg.Blocks) {
                foreach (var op in block.Operations) ScanMutations(op, tracked, mutated);
                if (block.BranchValue != null) ScanMutations(block.BranchValue, tracked, mutated);
            }

            foreach (var entry in tracked) {
                if (mutated.Contains(entry.Key)) continue;
                report(Diagnostic.Create(
                    Diagnostics.RefLocalNeverMutated,
                    entry.Value.Location,
                    entry.Value.MemberLabel,
                    entry.Value.ReadSibling));
            }
        }

        private readonly struct TrackedBinding {
            public readonly Location Location;
            public readonly string MemberLabel;
            public readonly string ReadSibling;
            public readonly ISimpleAssignmentOperation BindingAssignment;

            public TrackedBinding(Location location, string memberLabel, string readSibling, ISimpleAssignmentOperation bindingAssignment) {
                Location = location;
                MemberLabel = memberLabel;
                ReadSibling = readSibling;
                BindingAssignment = bindingAssignment;
            }
        }

        private static void CollectTracked(IOperation root, StaticEcsSymbols symbols, ref Dictionary<ILocalSymbol, TrackedBinding> tracked) {
            foreach (var d in root.DescendantsAndSelf()) {
                if (d is not ISimpleAssignmentOperation assignment || !assignment.IsRef) continue;
                if (assignment.Target is not ILocalReferenceOperation localRef) continue;
                if (localRef.Local.RefKind != RefKind.Ref) continue;

                var initValue = OperationHelpers.UnwrapImplicitConversions(assignment.Value);
                ISymbol member = initValue switch {
                    IInvocationOperation invocation => invocation.TargetMethod?.OriginalDefinition,
                    IPropertyReferenceOperation propRef => propRef.Property?.OriginalDefinition,
                    _ => null,
                };
                if (member is null) continue;
                // Members without a read-only sibling (Entity.Add, Components.Add) are absent from the
                // map — they're legitimate writable-binding sites and never raise FFSECS0013.
                if (!symbols.RefTargetReadSiblings.TryGetValue(member, out var sibling)) continue;

                tracked ??= new Dictionary<ILocalSymbol, TrackedBinding>(SymbolEqualityComparer.Default);
                if (tracked.ContainsKey(localRef.Local)) continue;
                tracked[localRef.Local] = new TrackedBinding(
                    location: localRef.Syntax.GetLocation(),
                    memberLabel: member.ContainingType?.Name is { } ct ? ct + "." + member.Name : member.Name,
                    readSibling: sibling,
                    bindingAssignment: assignment);
            }
        }

        private static void ScanMutations(IOperation root, Dictionary<ILocalSymbol, TrackedBinding> tracked, HashSet<ILocalSymbol> mutated) {
            foreach (var d in root.DescendantsAndSelf()) {
                switch (d) {
                    case ISimpleAssignmentOperation assignment:
                        if (assignment.IsRef) {
                            // ref alias creation: `ref var x = ref <chain rooted at tracked>` — only
                            // counts as mutation if the alias itself is writable (RefKind.Ref).
                            // Exclude the binding assignment of the tracked local itself.
                            if (tracked.TryGetValue(LocalOfAssignmentTarget(assignment), out var binding)
                                && binding.BindingAssignment == assignment) {
                                continue;
                            }
                            if (assignment.Target is ILocalReferenceOperation aliasTarget
                                && aliasTarget.Local.RefKind == RefKind.Ref) {
                                MarkIfReachable(assignment.Value, tracked, mutated);
                            }
                        } else {
                            MarkIfReachable(assignment.Target, tracked, mutated);
                        }
                        break;
                    case ICompoundAssignmentOperation compound:
                        MarkIfReachable(compound.Target, tracked, mutated);
                        break;
                    case ICoalesceAssignmentOperation coalesce:
                        MarkIfReachable(coalesce.Target, tracked, mutated);
                        break;
                    case IIncrementOrDecrementOperation incdec:
                        MarkIfReachable(incdec.Target, tracked, mutated);
                        break;
                    case IArgumentOperation arg
                        when arg.Parameter?.RefKind is RefKind.Ref or RefKind.Out:
                        MarkIfReachable(arg.Value, tracked, mutated);
                        break;
                    case IInvocationOperation invocation when invocation.Instance is not null:
                        // Method call on the tracked local as the receiver: for a non-readonly struct
                        // calling a non-readonly method, the compiler treats 'this' as a writable ref —
                        // we can't prove the method doesn't write through it, so conservatively count
                        // it as a mutation. Readonly struct or readonly method → no mutation concern.
                        if (TryGetRootLocal(invocation.Instance, out var receiverLocal)
                            && tracked.ContainsKey(receiverLocal)) {
                            var receiverType = receiverLocal.Type;
                            var targetMethod = invocation.TargetMethod;
                            if (receiverType is { IsValueType: true, IsReadOnly: false }
                                && targetMethod is { IsReadOnly: false }) {
                                mutated.Add(receiverLocal);
                            }
                        }
                        break;
                }
            }
        }

        private static ILocalSymbol LocalOfAssignmentTarget(ISimpleAssignmentOperation assignment) {
            return assignment.Target is ILocalReferenceOperation localRef ? localRef.Local : null;
        }

        private static void MarkIfReachable(IOperation operation, Dictionary<ILocalSymbol, TrackedBinding> tracked, HashSet<ILocalSymbol> mutated) {
            if (!TryGetRootLocal(operation, out var local)) return;
            if (tracked.ContainsKey(local)) mutated.Add(local);
        }

        /// <summary>
        /// Walks an operation chain (field / property / array element / conversion) down to the
        /// underlying <see cref="ILocalReferenceOperation"/> and returns its local symbol. Returns
        /// false if the chain doesn't root at a local.
        /// </summary>
        private static bool TryGetRootLocal(IOperation operation, out ILocalSymbol local) {
            while (operation is not null) {
                switch (operation) {
                    case ILocalReferenceOperation localRef:
                        local = localRef.Local;
                        return true;
                    case IFieldReferenceOperation fieldRef:
                        operation = fieldRef.Instance;
                        break;
                    case IPropertyReferenceOperation propRef:
                        operation = propRef.Instance;
                        break;
                    case IArrayElementReferenceOperation arrayRef:
                        operation = arrayRef.ArrayReference;
                        break;
                    case IConversionOperation conv:
                        operation = conv.Operand;
                        break;
                    default:
                        local = null;
                        return false;
                }
            }
            local = null;
            return false;
        }
    }
}

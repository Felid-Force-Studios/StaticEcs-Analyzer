using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0012 — A 'ref' local bound to a StaticEcs ref-returning member (per FFSECS0010 allow-list)
    /// must continue to be passed with the 'ref' keyword. Passing it as a value argument silently copies
    /// the underlying component at the call boundary, defeating the ref binding.
    ///
    /// Universal across method/lambda/local-function bodies: the analyzer walks the top-level CFG and
    /// every nested CFG via <see cref="OperationHelpers.WalkCfgRecursive"/>, so a ref-local declared
    /// inside a lambda passed to <c>WorldQuery.For(...)</c> is analysed exactly like one in a regular
    /// method body.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RefLocalEscapeAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.RefLocalPassedByValue);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.RefReturningTargets.IsEmpty) return;
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
            HashSet<ILocalSymbol> tracked = null;
            foreach (var block in cfg.Blocks) {
                foreach (var op in block.Operations) CollectTracked(op, symbols, ref tracked);
                if (block.BranchValue != null) CollectTracked(block.BranchValue, symbols, ref tracked);
            }
            if (tracked is null) return;

            foreach (var block in cfg.Blocks) {
                foreach (var op in block.Operations) ReportEscapes(op, tracked, report);
                if (block.BranchValue != null) ReportEscapes(block.BranchValue, tracked, report);
            }
        }

        private static void CollectTracked(IOperation root, StaticEcsSymbols symbols, ref HashSet<ILocalSymbol> tracked) {
            foreach (var d in root.DescendantsAndSelf()) {
                if (d is not ISimpleAssignmentOperation a || !a.IsRef) continue;
                if (a.Target is not ILocalReferenceOperation localRef) continue;
                // 'ref readonly' locals can't mutate the storage through the ref — the user already
                // committed to a readonly snapshot. Passing them by value adds at most a copy of an
                // already-readonly view; no writable ref semantics to "lose".
                if (localRef.Local.RefKind == RefKind.RefReadOnly) continue;
                if (OperationHelpers.IsAtomicallyValuedType(localRef.Local.Type)) continue;
                if (!IsAllowListedRefReturn(a.Value, symbols)) continue;
                tracked ??= new HashSet<ILocalSymbol>(SymbolEqualityComparer.Default);
                tracked.Add(localRef.Local);
            }
        }

        private static void ReportEscapes(IOperation root, HashSet<ILocalSymbol> tracked, Action<Diagnostic> report) {
            foreach (var d in root.DescendantsAndSelf()) {
                if (d is not IArgumentOperation arg) continue;
                if (arg.Parameter?.RefKind != RefKind.None) continue;
                var local = ResolveLocalReference(arg.Value);
                if (local is null || !tracked.Contains(local)) continue;
                report(Diagnostic.Create(Diagnostics.RefLocalPassedByValue, arg.Value.Syntax.GetLocation(), local.Name));
            }
        }

        private static bool IsAllowListedRefReturn(IOperation value, StaticEcsSymbols symbols) {
            var match = OperationHelpers.TryResolveRefReturningChain(value, symbols, out _, out _);
            return match == RefChainMatch.Write || match == RefChainMatch.Read;
        }

        private static ILocalSymbol ResolveLocalReference(IOperation value) {
            value = OperationHelpers.UnwrapImplicitConversions(value);
            return value is ILocalReferenceOperation localRef ? localRef.Local : null;
        }
    }
}

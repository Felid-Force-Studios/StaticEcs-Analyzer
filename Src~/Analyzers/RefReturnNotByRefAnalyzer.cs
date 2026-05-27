using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0010 — ref T returning member result must be bound by 'ref'.
    /// FFSECS0011 — ref readonly T (Read&lt;T&gt;) result bound to copy (Hidden by default).
    ///
    /// Both rules share infrastructure: locate the StaticEcs ref-returning member by allow-list,
    /// then flag binding to a non-ref local or passing as a value argument. Explicit cast in
    /// source (e.g. <c>(Position)entity.Ref&lt;Position&gt;()</c>) is the documented escape hatch.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class RefReturnNotByRefAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.RefReturnDroppedToCopy, Diagnostics.RefReadonlyReadDroppedToCopy);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) {
                    return;
                }
                if (symbols.RefReturningTargets.IsEmpty && symbols.RefReadonlyReadTargets.IsEmpty) {
                    return;
                }

                start.RegisterOperationAction(ctx => AnalyzeVariableDeclarator(ctx, symbols), OperationKind.VariableDeclarator);
                start.RegisterOperationAction(ctx => AnalyzeArgument(ctx, symbols), OperationKind.Argument);
                start.RegisterOperationAction(ctx => AnalyzeAssignment(ctx, symbols), OperationKind.SimpleAssignment);
                start.RegisterOperationAction(ctx => AnalyzeReturn(ctx, symbols), OperationKind.Return);
            });
        }

        private static void AnalyzeVariableDeclarator(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var declarator = (IVariableDeclaratorOperation)context.Operation;
            var initializer = declarator.Initializer;
            if (initializer is null) return;
            // Already bound by ref — both the local and the initializer carry it.
            if (declarator.Symbol.IsRef) return;
            if (IsExplicitCastEscape(initializer.Value.Syntax)) return;

            var result = ClassifyMember(initializer.Value, symbols);
            if (result.descriptor is null) return;

            context.ReportDiagnostic(Diagnostic.Create(result.descriptor, result.location, result.label));
        }

        private static void AnalyzeArgument(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var argument = (IArgumentOperation)context.Operation;
            // Passing by ref/out/in — caller honours the ref-return, no copy.
            if (argument.Parameter is null || argument.Parameter.RefKind != RefKind.None) return;
            if (IsExplicitCastEscape(argument.Value.Syntax)) return;

            var result = ClassifyMember(argument.Value, symbols);
            if (result.descriptor is null) return;

            context.ReportDiagnostic(Diagnostic.Create(result.descriptor, result.location, result.label));
        }

        /// <summary>
        /// Catches assignment into an existing non-ref slot: 'pos = entity.Ref&lt;T&gt;()' where pos was
        /// declared earlier as 'Position pos' (not 'ref Position pos'). Same silent-copy bug as
        /// the VariableDeclarator case, just a different syntactic shape.
        /// </summary>
        private static void AnalyzeAssignment(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var assignment = (ISimpleAssignmentOperation)context.Operation;
            if (assignment.IsRef) return; // 'pos = ref entity.Ref<T>()' — explicit ref binding.
            if (assignment.Target is null) return;
            if (!IsNonRefValueTarget(assignment.Target)) return;
            if (IsExplicitCastEscape(assignment.Value.Syntax)) return;

            var result = ClassifyMember(assignment.Value, symbols);
            if (result.descriptor is null) return;

            context.ReportDiagnostic(Diagnostic.Create(result.descriptor, result.location, result.label));
        }

        /// <summary>
        /// Catches 'return entity.Ref&lt;T&gt;()' from a method that does not itself return by ref —
        /// silently copies the ref-return.
        /// </summary>
        private static void AnalyzeReturn(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var ret = (IReturnOperation)context.Operation;
            if (ret.ReturnedValue is null) return;
            // Enclosing ref-returning method/lambda: the ref-return is preserved through the boundary,
            // regardless of whether the syntax shape is `return ref x;` (statement body) or `=> x` (expression body).
            if (EnclosingMethodReturnsByRef(context, ret)) return;
            // Belt-and-braces for cases where ContainingSymbol resolution is unavailable: explicit `return ref ...`.
            if (ret.Syntax is ReturnStatementSyntax retSyntax && retSyntax.Expression is RefExpressionSyntax) return;
            if (IsExplicitCastEscape(ret.ReturnedValue.Syntax)) return;

            var result = ClassifyMember(ret.ReturnedValue, symbols);
            if (result.descriptor is null) return;

            context.ReportDiagnostic(Diagnostic.Create(result.descriptor, result.location, result.label));
        }

        /// <summary>True if <paramref name="target"/> is a non-ref local/parameter reference (the slot we'd copy into).
        /// Field/property targets are intentionally excluded: ref-fields are not expressible in ordinary structs/classes,
        /// and a property setter always copies — both are clearly intentional snapshot writes, not the "forgot ref var" bug.</summary>
        private static bool IsNonRefValueTarget(IOperation target) {
            switch (target) {
                case ILocalReferenceOperation localRef: return !localRef.Local.IsRef;
                case IParameterReferenceOperation paramRef: return paramRef.Parameter.RefKind == RefKind.None;
                default: return false;
            }
        }

        /// <summary>
        /// Delegates the chain walk to <see cref="OperationHelpers.TryResolveRefReturningChain"/>, then
        /// maps the result to the appropriate diagnostic descriptor. The helper handles every suppression
        /// case (reference-typed outer value, non-ref property breaking the chain, reference-typed payload
        /// of the ref-returning member itself) so that we report no diagnostic when a 'ref' binding would
        /// either fail to compile or change nothing.
        /// </summary>
        private static (DiagnosticDescriptor descriptor, string label, Location location) ClassifyMember(IOperation value, StaticEcsSymbols symbols) {
            var match = OperationHelpers.TryResolveRefReturningChain(value, symbols, out var target, out var matchedOperation);
            switch (match) {
                case RefChainMatch.Write:
                    return (Diagnostics.RefReturnDroppedToCopy, FormatLabel(target), matchedOperation.Syntax.GetLocation());
                case RefChainMatch.Read:
                    return (Diagnostics.RefReadonlyReadDroppedToCopy, FormatLabel(target), matchedOperation.Syntax.GetLocation());
                default:
                    return (null, null, null);
            }
        }

        /// <summary>
        /// True if the method/lambda enclosing this return itself returns by ref (or ref readonly).
        /// Walks the operation tree upward to find the nearest <see cref="IMethodBodyOperation"/>,
        /// <see cref="IBlockOperation"/> attached to a ref-returning member, or <see cref="IAnonymousFunctionOperation"/>.
        /// </summary>
        private static bool EnclosingMethodReturnsByRef(OperationAnalysisContext context, IReturnOperation ret) {
            // Walk up the operation tree: the first ILocalFunctionOperation / IAnonymousFunctionOperation we hit
            // is the immediate enclosing function; otherwise fall back to ContainingSymbol (top-level method).
            for (var parent = ret.Parent; parent is not null; parent = parent.Parent) {
                switch (parent) {
                    case IAnonymousFunctionOperation anon:
                        return anon.Symbol.RefKind != RefKind.None;
                    case ILocalFunctionOperation local:
                        return local.Symbol.RefKind != RefKind.None;
                }
            }
            return context.ContainingSymbol is IMethodSymbol method && method.RefKind != RefKind.None;
        }

        private static bool IsExplicitCastEscape(SyntaxNode syntax) {
            // The escape hatch is user-written '(T)expr'. Roslyn surfaces this as CastExpressionSyntax.
            // Conversions on the operation tree may differ, so trust the syntax form here.
            return syntax is CastExpressionSyntax;
        }

        private static string FormatLabel(ISymbol target) {
            if (target.ContainingType is null) return target.Name;
            return target.ContainingType.Name + "." + target.Name;
        }
    }
}

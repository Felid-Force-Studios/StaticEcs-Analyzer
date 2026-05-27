using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0032 — Suggests replacing <c>entity.IsMatch&lt;TFilter&gt;()</c> with a direct
    /// presence-check method on <c>Entity</c> (<c>Has</c>/<c>HasEnabled</c>/<c>HasDisabled</c> and
    /// their <c>*Any</c> variants, plus <c>Is</c>/<c>IsAny</c>/<c>IsNot</c> for entity-type filters)
    /// when the filter is a simple shape (`All`/`Any`/`None`/`AllOnlyDisabled`/... / `EntityIs*`)
    /// of arity ≤ 3. Pure style suggestion (Info severity).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class IsMatchToDirectMethodAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.IsMatchReplaceableWithDirectMethod);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.EntityIsMatch is null || symbols.IsMatchReplacements.IsEmpty) return;

                start.RegisterOperationAction(ctx => AnalyzeInvocation(ctx, symbols), OperationKind.Invocation);
            });
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var invocation = (IInvocationOperation)context.Operation;
            var target = invocation.TargetMethod;
            if (!SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, symbols.EntityIsMatch)) return;
            if (target.TypeArguments.Length != 1) return;

            if (target.TypeArguments[0] is not INamedTypeSymbol filterType) return;
            var filterDefinition = filterType.OriginalDefinition;
            if (!symbols.IsMatchReplacements.TryGetValue(filterDefinition, out var replacement)) return;

            // For HasEnabled/HasEnabledAny/HasDisabled/HasDisabledAny targets, every filter type argument
            // must implement BOTH IComponent AND IDisableable. The filter constraints alone are not enough:
            // All/Any/None accept IComponentOrTag (tags included), and a tag with IDisableable would still
            // fail HasEnabled's IComponent constraint at the call site. Without this check, the codefix
            // would produce uncompilable code.
            if (replacement.RequiresDisableable
                && (!AllImplementInterface(filterType, symbols.IDisableable)
                    || !AllImplementInterface(filterType, symbols.IComponent))) return;

            var directCall = (replacement.Negate ? "!entity." : "entity.") + replacement.MethodName + "<...>()";
            var properties = ImmutableDictionary<string, string>.Empty
                .Add("MethodName", replacement.MethodName)
                .Add("Negate", replacement.Negate ? "true" : "false");
            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.IsMatchReplaceableWithDirectMethod,
                invocation.Syntax.GetLocation(),
                properties,
                filterDefinition.Name,
                directCall));
        }

        /// <summary>True if every type argument of <paramref name="filterType"/> implements <paramref name="targetInterface"/>.</summary>
        private static bool AllImplementInterface(INamedTypeSymbol filterType, INamedTypeSymbol targetInterface) {
            if (targetInterface is null) return false;
            foreach (var arg in filterType.TypeArguments) {
                if (arg is not INamedTypeSymbol named && arg is not ITypeParameterSymbol) return false;
                var implements = false;
                foreach (var iface in arg.AllInterfaces) {
                    if (SymbolEqualityComparer.Default.Equals(iface, targetInterface)) {
                        implements = true;
                        break;
                    }
                }
                if (!implements) return false;
            }
            return true;
        }
    }
}

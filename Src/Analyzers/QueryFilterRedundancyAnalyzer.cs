using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0050 — Redundant component in query filter (same component twice in same-kind filters,
    /// or overlapping a lambda ref/in parameter, or overlapping an IQuery struct's component generic).
    /// FFSECS0051 — Contradictory All+None on the same component in one filter chain.
    ///
    /// Single Invocation hook, dispatched by symbolic match against three sets in <see cref="StaticEcsSymbols"/>:
    ///   • <see cref="StaticEcsSymbols.QueryEntryMethods"/> — duplicates inside the TFilter chain.
    ///   • <see cref="StaticEcsSymbols.QueryBuilderForMethods"/> — duplicates plus overlap with lambda
    ///     parameters and (for the generic For&lt;TFn&gt; overloads) the TFn IQuery struct's component generics.
    ///   • <see cref="StaticEcsSymbols.QueryBuilderTerminalMethods"/> — duplicates only.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class QueryFilterRedundancyAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.QueryFilterRedundantComponent, Diagnostics.QueryFilterContradiction);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.QueryFilterAll.IsEmpty && symbols.QueryFilterNone.IsEmpty && symbols.QueryFilterAny.IsEmpty) return;
                if (symbols.QueryEntryMethods.IsEmpty && symbols.QueryBuilderForMethods.IsEmpty && symbols.QueryBuilderTerminalMethods.IsEmpty) return;

                start.RegisterOperationAction(ctx => AnalyzeInvocation(ctx, symbols), OperationKind.Invocation);
            });
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var invocation = (IInvocationOperation)context.Operation;
            var target = invocation.TargetMethod.OriginalDefinition;

            if (symbols.QueryEntryMethods.Contains(target)) {
                AnalyzeQuery(context, invocation, symbols);
            } else if (symbols.QueryBuilderForMethods.Contains(target)) {
                AnalyzeFor(context, invocation, symbols);
            } else if (symbols.QueryBuilderTerminalMethods.Contains(target)) {
                AnalyzeTerminal(context, invocation, symbols);
            }
        }

        // ── World<TWorld>.Query<...>() ───────────────────────────────────────────
        private static void AnalyzeQuery(OperationAnalysisContext context, IInvocationOperation invocation, StaticEcsSymbols symbols) {
            var typeArgs = invocation.TargetMethod.TypeArguments;
            if (typeArgs.Length == 0) return; // Query() — no filter to check.

            // Skip if chained to a For/Entities/Write/Read/WriteBlock/ReadBlock call — that outer call
            // will report on the same filter set, so reporting twice is noise.
            if (IsChainedToAnalyzedCall(invocation, symbols)) return;

            ReportFilterIssues(context, BuildFilterSets(typeArgs, symbols), invocation.Syntax.GetLocation());
        }

        private static bool IsChainedToAnalyzedCall(IInvocationOperation invocation, StaticEcsSymbols symbols) {
            var current = invocation.Parent;
            while (current is not null) {
                switch (current) {
                    case IInvocationOperation outer:
                        var def = outer.TargetMethod.OriginalDefinition;
                        return symbols.QueryBuilderForMethods.Contains(def)
                               || symbols.QueryBuilderTerminalMethods.Contains(def)
                               || symbols.QueryEntryMethods.Contains(def);
                    case IConversionOperation conv when conv.IsImplicit:
                        current = conv.Parent;
                        continue;
                    default:
                        return false;
                }
            }
            return false;
        }

        // ── WorldQuery<TFilter>.For(...) and any fluent builder (WriteQuery, ReadQuery, BlockWriteQuery, ...).For(...) ───
        private static void AnalyzeFor(OperationAnalysisContext context, IInvocationOperation invocation, StaticEcsSymbols symbols) {
            // Accept any containing-type chain that exposes an IQueryFilter as its first generic arg
            // (covers WorldQuery<TFilter>, WriteQuery<TFilter, T0>, ReadQuery<TFilter, T0...>,
            // BlockWriteQuery<TFilter, ...>, BlockReadQuery<TFilter, ...>, and nested ReadQuery within them).
            var filter = ExtractTFilterFromContainingType(invocation.TargetMethod.ContainingType, symbols);
            if (filter is null) return;

            var filterSets = BuildFilterSets(ImmutableArray.Create<ITypeSymbol>(filter), symbols);
            ReportFilterIssues(context, filterSets, invocation.Syntax.GetLocation());

            // Lambda overlap.
            foreach (var argument in invocation.Arguments) {
                var lambda = OperationHelpers.ExtractLambda(argument.Value);
                if (lambda is null) continue;
                ReportFilterParamOverlap(context, filterSets, GetLambdaComponentParams(lambda.Symbol, symbols), lambda.Symbol);
            }

            // IQuery struct overlap (For<TFn>() — generic method).
            if (invocation.TargetMethod.TypeArguments.Length >= 1
                && invocation.TargetMethod.TypeArguments[0] is INamedTypeSymbol tfn
                && !symbols.QueryCallbackInterfaces.IsEmpty) {
                var structComponents = GetIQueryStructComponents(tfn, symbols);
                if (structComponents.Count > 0) {
                    ReportFilterStructOverlap(context, filterSets, structComponents, invocation.Syntax.GetLocation());
                }
            }
        }

        // ── *.Entities() / *.Write() / *.Read() / *.WriteBlock() / *.ReadBlock() ─
        private static void AnalyzeTerminal(OperationAnalysisContext context, IInvocationOperation invocation, StaticEcsSymbols symbols) {
            var filter = ExtractTFilterFromContainingType(invocation.TargetMethod.ContainingType, symbols);
            if (filter is null) return;

            ReportFilterIssues(context, BuildFilterSets(ImmutableArray.Create<ITypeSymbol>(filter), symbols), invocation.Syntax.GetLocation());
        }

        // ── Filter decomposition ─────────────────────────────────────────────────
        private readonly struct FilterSets {
            public readonly Dictionary<ITypeSymbol, int> All;
            public readonly Dictionary<ITypeSymbol, int> None;
            public readonly Dictionary<ITypeSymbol, int> Any;
            public FilterSets(Dictionary<ITypeSymbol, int> all, Dictionary<ITypeSymbol, int> none, Dictionary<ITypeSymbol, int> any) {
                All = all; None = none; Any = any;
            }
        }

        private static FilterSets BuildFilterSets(ImmutableArray<ITypeSymbol> typeArgs, StaticEcsSymbols symbols) {
            var all = new Dictionary<ITypeSymbol, int>(SymbolEqualityComparer.Default);
            var none = new Dictionary<ITypeSymbol, int>(SymbolEqualityComparer.Default);
            var any = new Dictionary<ITypeSymbol, int>(SymbolEqualityComparer.Default);
            foreach (var arg in typeArgs) {
                Decompose(arg, all, none, any, symbols);
            }
            return new FilterSets(all, none, any);
        }

        private static void Decompose(ITypeSymbol filter, Dictionary<ITypeSymbol, int> all, Dictionary<ITypeSymbol, int> none, Dictionary<ITypeSymbol, int> any, StaticEcsSymbols symbols) {
            if (filter is not INamedTypeSymbol named) return;
            var origDef = named.OriginalDefinition;

            if (symbols.QueryFilterAnd.Contains(origDef)) {
                foreach (var inner in named.TypeArguments) Decompose(inner, all, none, any, symbols);
                return;
            }
            if (symbols.QueryFilterAll.Contains(origDef)) {
                foreach (var component in named.TypeArguments) Increment(all, component);
                return;
            }
            if (symbols.QueryFilterNone.Contains(origDef)) {
                foreach (var component in named.TypeArguments) Increment(none, component);
                return;
            }
            if (symbols.QueryFilterAny.Contains(origDef)) {
                foreach (var component in named.TypeArguments) Increment(any, component);
                return;
            }
            // Other filter kinds (EntityIs, AllOnlyDisabled, Or<>, etc.) are silently ignored in V1.
        }

        private static void Increment(Dictionary<ITypeSymbol, int> bag, ITypeSymbol component) {
            var key = component.OriginalDefinition;
            bag[key] = bag.TryGetValue(key, out var count) ? count + 1 : 1;
        }

        // ── Reporting ────────────────────────────────────────────────────────────
        private static void ReportFilterIssues(OperationAnalysisContext context, FilterSets sets, Location location) {
            ReportDuplicates(context, sets.All, "All<...>", location);
            ReportDuplicates(context, sets.None, "None<...>", location);
            ReportDuplicates(context, sets.Any, "Any<...>", location);
            ReportContradictions(context, sets, location);
        }

        private static void ReportDuplicates(OperationAnalysisContext context, Dictionary<ITypeSymbol, int> bag, string kindLabel, Location location) {
            foreach (var pair in bag) {
                if (pair.Value <= 1) continue;
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.QueryFilterRedundantComponent,
                    location,
                    pair.Key.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                    "appears " + pair.Value + " times in " + kindLabel));
            }
        }

        private static void ReportContradictions(OperationAnalysisContext context, FilterSets sets, Location location) {
            foreach (var component in sets.All.Keys) {
                if (!sets.None.ContainsKey(component)) continue;
                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.QueryFilterContradiction,
                    location,
                    component.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }
        }

        private static void ReportFilterParamOverlap(OperationAnalysisContext context, FilterSets filter, List<ITypeSymbol> lambdaComponents, IMethodSymbol lambdaMethod) {
            var location = lambdaMethod.Locations.Length > 0 ? lambdaMethod.Locations[0] : Location.None;
            foreach (var component in lambdaComponents) {
                var key = component.OriginalDefinition;
                if (filter.All.ContainsKey(key)) {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.QueryFilterRedundantComponent,
                        location,
                        component.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        "already in All<...> filter and is also a ref/in lambda parameter (implicit All<...>)"));
                }
                // Lambda param implies All<T>; filter has None<T> → unsatisfiable.
                if (filter.None.ContainsKey(key)) {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.QueryFilterContradiction,
                        location,
                        component.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                }
            }
        }

        private static void ReportFilterStructOverlap(OperationAnalysisContext context, FilterSets filter, HashSet<ITypeSymbol> structComponents, Location location) {
            foreach (var component in structComponents) {
                var key = component.OriginalDefinition;
                if (filter.All.ContainsKey(key)) {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.QueryFilterRedundantComponent,
                        location,
                        component.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
                        "already in All<...> filter and is also a component generic of the IQuery struct (implicit All<...>)"));
                }
                // IQuery component implies All<T>; filter has None<T> → unsatisfiable.
                if (filter.None.ContainsKey(key)) {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.QueryFilterContradiction,
                        location,
                        component.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                }
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────
        private static INamedTypeSymbol ExtractTFilterFromContainingType(INamedTypeSymbol containingType, StaticEcsSymbols symbols) {
            // Walk up the containing-type chain. Accept any type whose first type-argument implements
            // IQueryFilter — that covers WorldQuery<TFilter>, WriteQuery<TFilter, T...>, ReadQuery<...>,
            // BlockWriteQuery<...>, BlockReadQuery<...>, plus nested ReadQuery types within them.
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
            if (symbols.IQueryFilter is null) return false;
            foreach (var iface in type.AllInterfaces) {
                if (SymbolEqualityComparer.Default.Equals(iface, symbols.IQueryFilter)) return true;
            }
            return false;
        }

        private static List<ITypeSymbol> GetLambdaComponentParams(IMethodSymbol lambda, StaticEcsSymbols symbols) {
            var list = new List<ITypeSymbol>();
            foreach (var parameter in lambda.Parameters) {
                if (parameter.RefKind == RefKind.None) continue;
                // Skip Entity-typed parameter; only components count.
                if (symbols.EntityType is not null
                    && SymbolEqualityComparer.Default.Equals(parameter.Type.OriginalDefinition, symbols.EntityType)) continue;
                list.Add(parameter.Type);
            }
            return list;
        }

        /// <summary>Component type-args of any IQuery.Write&lt;...&gt; / Read&lt;...&gt; / Write&lt;...&gt;.Read&lt;...&gt; that <paramref name="tfn"/> implements.</summary>
        private static HashSet<ITypeSymbol> GetIQueryStructComponents(INamedTypeSymbol tfn, StaticEcsSymbols symbols) {
            var components = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var iface in tfn.AllInterfaces) {
                if (!symbols.QueryCallbackInterfaces.Contains(iface.OriginalDefinition)) continue;
                foreach (var typeArg in iface.TypeArguments) {
                    components.Add(typeArg);
                }
            }
            return components;
        }
    }
}

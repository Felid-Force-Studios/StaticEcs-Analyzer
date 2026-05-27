using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0020 — class implements any StaticEcs marker interface (IComponent, ITag, IEvent,
    /// ILinkType, ILinksType, IEntityType, IWorldType, IMultiComponent). One report per class.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class EcsMarkerInterfaceMustBeStructAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.EcsMarkerInterfaceMustBeStruct);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.EcsMarkerInterfaces.IsEmpty) return;
                start.RegisterSymbolAction(ctx => AnalyzeNamedType(ctx, symbols), SymbolKind.NamedType);
            });
        }

        private static void AnalyzeNamedType(SymbolAnalysisContext context, StaticEcsSymbols symbols) {
            var type = (INamedTypeSymbol)context.Symbol;
            if (type.TypeKind != TypeKind.Class) return;
            if (type.IsAbstract) return; // abstract base classes may exist as scaffolding; only concrete classes break runtime.

            foreach (var iface in type.AllInterfaces) {
                if (!symbols.EcsMarkerInterfaces.Contains(iface.OriginalDefinition)) continue;
                var location = type.Locations.Length > 0 ? type.Locations[0] : Location.None;
                context.ReportDiagnostic(Diagnostic.Create(Diagnostics.EcsMarkerInterfaceMustBeStruct, location, type.Name, iface.Name));
                return; // one report per class is enough; user fixes by turning into struct.
            }
        }
    }
}

using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0021 — type implementing IMultiComponent that is not unmanaged must override both
    /// Write(ref BinaryPackWriter) and Read(ref BinaryPackReader). Without overrides, serialization
    /// silently produces empty data for managed payloads. The struct-vs-class requirement is enforced
    /// separately by FFSECS0020.
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class MultiComponentRequirementsAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.MultiComponentSerializationOverrideRequired);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) {
                    return;
                }
                if (symbols.IMultiComponent is null) {
                    return;
                }

                start.RegisterSymbolAction(ctx => AnalyzeNamedType(ctx, symbols), SymbolKind.NamedType);
            });
        }

        private static void AnalyzeNamedType(SymbolAnalysisContext context, StaticEcsSymbols symbols) {
            var type = (INamedTypeSymbol)context.Symbol;
            // Only concrete struct/class instances can host serialization. Interfaces extending
            // IMultiComponent and abstract base classes are scaffolding — no storage, no serialization.
            // Struct-vs-class enforcement itself lives in FFSECS0020.
            if (type.TypeKind is not (TypeKind.Struct or TypeKind.Class)) return;
            if (type.IsAbstract) return;

            var implementsMulti = false;
            foreach (var iface in type.AllInterfaces) {
                if (SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, symbols.IMultiComponent)) {
                    implementsMulti = true;
                    break;
                }
            }
            if (!implementsMulti) return;
            if (type.IsUnmanagedType) return; // unmanaged → bulk memory copy handles serialization.

            if (HasOwnSerializationMethod(type, "Write", symbols.BinaryPackWriter)
                && HasOwnSerializationMethod(type, "Read", symbols.BinaryPackReader)) {
                return;
            }

            var location = type.Locations.Length > 0 ? type.Locations[0] : Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                Diagnostics.MultiComponentSerializationOverrideRequired,
                location,
                type.Name));
        }

        /// <summary>
        /// True if the type explicitly declares a method '{name}(ref {parameterType})' — own method,
        /// not the default interface implementation inherited from IMultiComponent.
        /// </summary>
        private static bool HasOwnSerializationMethod(INamedTypeSymbol type, string name, INamedTypeSymbol parameterType) {
            if (parameterType is null) return true; // dependency type not resolvable → don't false-positive.
            foreach (var member in type.GetMembers(name)) {
                if (member is not IMethodSymbol method) continue;
                if (method.Parameters.Length != 1) continue;
                var param = method.Parameters[0];
                if (param.RefKind != RefKind.Ref) continue;
                if (SymbolEqualityComparer.Default.Equals(param.Type.OriginalDefinition, parameterType)) {
                    return true;
                }
            }
            return false;
        }
    }
}

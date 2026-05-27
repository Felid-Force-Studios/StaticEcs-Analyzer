using System.Linq;
using Microsoft.CodeAnalysis.Diagnostics;

namespace FFS.Libraries.StaticEcs.Analyzers {
    /// <summary>
    /// Shared scoping check: the analyzer must only fire on assemblies that actually depend on StaticEcs.
    /// Each analyzer calls <see cref="TryEnter"/> from its CompilationStartAction and only registers
    /// further actions if it returns true.
    ///
    /// Why a runtime check on top of packaging: NuGet already restricts the analyzer to consumers of
    /// FFS.StaticEcs, but in Unity an analyzer DLL can be picked up by any asmdef if a user misconfigures
    /// the meta file. The check below is a hard backstop that is cheap (one pass over ReferencedAssemblyNames).
    /// </summary>
    internal static class StaticEcsCompilationScope {
        public static bool TryEnter(CompilationStartAnalysisContext context, out StaticEcsSymbols symbols) {
            symbols = null;
            var compilation = context.Compilation;

            var isStaticEcsItself = compilation.AssemblyName is StaticEcsSymbols.StaticEcsAssembly
                                                              or StaticEcsSymbols.StaticEcsDebugAssembly;
            var referencesStaticEcs = compilation.ReferencedAssemblyNames.Any(static name =>
                name.Name == StaticEcsSymbols.StaticEcsAssembly
                || name.Name == StaticEcsSymbols.StaticEcsDebugAssembly);

            if (!isStaticEcsItself && !referencesStaticEcs) {
                return false;
            }

            symbols = StaticEcsSymbols.Create(compilation);
            return symbols is not null;
        }
    }
}

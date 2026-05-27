using Microsoft.CodeAnalysis;

namespace FFS.Libraries.StaticEcs.Analyzers {
    /// <summary>
    /// Conservative struct-size estimation shared between the analyzer and codefix assemblies.
    /// Compiled into both projects via the <c>Shared/</c> folder (default SDK glob).
    /// </summary>
    internal static class StructSizeEstimation {
        /// <summary>
        /// FFSECS0011: structs ≤ this many bytes are considered "register-sized" — a copy at the call
        /// boundary fits in a single register on x64/ARM64 ABIs, so binding to 'ref readonly var'
        /// brings no measurable win and the diagnostic / codefix tailor their behavior accordingly.
        /// </summary>
        public const int SmallStructPayloadByteThreshold = 8;

        private const int StructSizeRecursionDepthCap = 3;

        /// <summary>
        /// Conservative lower-bound estimate of an instance struct's size in bytes from its declared
        /// fields. Returns false when the size cannot be reliably estimated — open generic type
        /// parameters, explicit <see cref="System.Runtime.InteropServices.StructLayoutAttribute"/>,
        /// pointer / function-pointer fields, fixed-size buffers, or unresolvable nested field types.
        /// Callers should treat <c>false</c> as "don't apply size-based heuristics".
        ///
        /// <para>No padding/alignment is applied: an under-estimate is fine for the only consumer
        /// (FFSECS0011 small-struct gate) — at worst, the rule keeps firing on a slightly-larger-than-it-
        /// looks struct.</para>
        /// </summary>
        public static bool TryEstimateStructSize(ITypeSymbol type, out int sizeBytes) {
            return TryEstimateStructSize(type, depth: 0, out sizeBytes);
        }

        private static bool TryEstimateStructSize(ITypeSymbol type, int depth, out int sizeBytes) {
            sizeBytes = 0;
            if (type is null) return false;
            if (depth > StructSizeRecursionDepthCap) return false;

            if (type.IsReferenceType) {
                sizeBytes = 8;
                return true;
            }
            if (type is IPointerTypeSymbol || type is IFunctionPointerTypeSymbol) return false;
            if (type is ITypeParameterSymbol) return false;

            if (type.TypeKind == TypeKind.Enum) {
                if (type is INamedTypeSymbol enumType && enumType.EnumUnderlyingType is { } underlying) {
                    return TryEstimateStructSize(underlying, depth + 1, out sizeBytes);
                }
                return false;
            }

            switch (type.SpecialType) {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                    sizeBytes = 1; return true;
                case SpecialType.System_Char:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                    sizeBytes = 2; return true;
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Single:
                    sizeBytes = 4; return true;
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Double:
                    sizeBytes = 8; return true;
                case SpecialType.System_Decimal:
                    sizeBytes = 16; return true;
                case SpecialType.System_IntPtr:
                case SpecialType.System_UIntPtr:
                    sizeBytes = 8; return true;
            }

            if (HasExplicitLayout(type)) return false;

            int total = 0;
            foreach (var member in type.GetMembers()) {
                if (member is not IFieldSymbol field) continue;
                if (field.IsStatic || field.IsConst) continue;
                if (field.IsFixedSizeBuffer) return false;
                if (!TryEstimateStructSize(field.Type, depth + 1, out var fieldSize)) return false;
                total += fieldSize;
                if (total > SmallStructPayloadByteThreshold) {
                    sizeBytes = total;
                    return true;
                }
            }
            sizeBytes = total;
            return true;
        }

        private static bool HasExplicitLayout(ITypeSymbol type) {
            foreach (var attr in type.GetAttributes()) {
                var ac = attr.AttributeClass;
                if (ac is null) continue;
                if (ac.Name != "StructLayoutAttribute") continue;
                if (attr.ConstructorArguments.Length < 1) continue;
                var layoutKind = attr.ConstructorArguments[0];
                if (layoutKind.Value is int kind && kind == 2) return true; // LayoutKind.Explicit == 2
            }
            return false;
        }
    }
}

// ReSharper disable InconsistentNaming
// ReSharper disable IdentifierTypo
namespace FFS.Libraries.StaticEcs.Analyzers {
    /// <summary>
    /// Single source of truth for FFSECS rule IDs. Used by both the analyzer assembly
    /// (DiagnosticDescriptor.id) and the codefix assembly (FixableDiagnosticIds + runtime checks).
    /// Internal: not part of the public NuGet API surface.
    /// </summary>
    internal static class FFSECSIds {
        public const string FFSECS0010 = "FFSECS0010";
        public const string FFSECS0011 = "FFSECS0011";
        public const string FFSECS0012 = "FFSECS0012";
        public const string FFSECS0013 = "FFSECS0013";
        public const string FFSECS0020 = "FFSECS0020";
        public const string FFSECS0021 = "FFSECS0021";
        public const string FFSECS0030 = "FFSECS0030";
        public const string FFSECS0031 = "FFSECS0031";
        public const string FFSECS0032 = "FFSECS0032";
        public const string FFSECS0033 = "FFSECS0033";
        public const string FFSECS0040 = "FFSECS0040";
        public const string FFSECS0041 = "FFSECS0041";
        public const string FFSECS0042 = "FFSECS0042";
        public const string FFSECS0050 = "FFSECS0050";
        public const string FFSECS0051 = "FFSECS0051";
    }
}

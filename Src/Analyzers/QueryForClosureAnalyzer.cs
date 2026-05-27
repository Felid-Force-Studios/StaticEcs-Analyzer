using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers.Analyzers {
    /// <summary>
    /// FFSECS0031 — Lambda passed to WorldQuery.For (or any fluent builder's For) captures outer state,
    /// allocating a closure on every call. Direct violation of StaticEcs's zero-allocation iteration
    /// contract. Detection uses <see cref="SemanticModel.AnalyzeDataFlow(SyntaxNode)"/> on the lambda
    /// body to find captured variables — empty captures means the compiler caches the delegate once
    /// per process (safe).
    /// </summary>
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class QueryForClosureAnalyzer : DiagnosticAnalyzer {
        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
            ImmutableArray.Create(Diagnostics.QueryForClosureCapture);

        public override void Initialize(AnalysisContext context) {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();

            context.RegisterCompilationStartAction(static start => {
                if (!StaticEcsCompilationScope.TryEnter(start, out var symbols)) return;
                if (symbols.QueryBuilderForMethods.IsEmpty) return;

                start.RegisterOperationAction(ctx => AnalyzeInvocation(ctx, symbols), OperationKind.Invocation);
            });
        }

        private static void AnalyzeInvocation(OperationAnalysisContext context, StaticEcsSymbols symbols) {
            var invocation = (IInvocationOperation)context.Operation;
            if (!symbols.QueryBuilderForMethods.Contains(invocation.TargetMethod.OriginalDefinition)) return;

            foreach (var argument in invocation.Arguments) {
                var lambda = ExtractLambda(argument.Value);
                if (lambda is not null) {
                    // Static lambda (C# 9+) syntactically forbids captures — skip cheaply.
                    if (lambda.Symbol.IsStatic) continue;

                    var captures = FindCapturedSymbols(lambda);
                    if (captures.IsEmpty) continue;

                    var first = captures[0];
                    var label = captures.Length == 1
                        ? first
                        : first + "' and " + (captures.Length - 1) + " more — first capture: '" + first;

                    context.ReportDiagnostic(Diagnostic.Create(
                        Diagnostics.QueryForClosureCapture,
                        lambda.Syntax.GetLocation(),
                        label));
                    continue;
                }

                // Method-group reference to a non-static method (e.g. .For(InstanceMethod) or
                // .For(localVar.Method)) — captures the receiver as the delegate target, allocating a
                // Delegate per call. Receiver may be 'this' implicitly, a local, a parameter, or a field.
                var methodRef = ExtractMethodReference(argument.Value);
                if (methodRef is null) continue;
                if (methodRef.Method.IsStatic) continue;

                context.ReportDiagnostic(Diagnostic.Create(
                    Diagnostics.QueryForClosureCapture,
                    methodRef.Syntax.GetLocation(),
                    DescribeMethodReceiver(methodRef)));
            }
        }

        /// <summary>
        /// Builds a human-readable label for the captured delegate target of a method-group reference.
        /// <c>.For(InstanceMethod)</c> or <c>.For(this.M)</c> ⇒ "this"; <c>.For(local.M)</c> ⇒ local name.
        /// </summary>
        private static string DescribeMethodReceiver(IMethodReferenceOperation methodRef) {
            return methodRef.Instance switch {
                null or IInstanceReferenceOperation => "this",
                ILocalReferenceOperation localRef => localRef.Local.Name,
                IParameterReferenceOperation paramRef => paramRef.Parameter.Name,
                IFieldReferenceOperation fieldRef => fieldRef.Field.IsStatic ? fieldRef.Field.Name : "this",
                IPropertyReferenceOperation propRef => propRef.Property.IsStatic ? propRef.Property.Name : "this",
                _ => "this",
            };
        }

        private static IMethodReferenceOperation ExtractMethodReference(IOperation value) => OperationHelpers.ExtractMethodReference(value);

        /// <summary>
        /// Walks the lambda body and collects names of symbols captured from outside the lambda's
        /// own parameters/locals. Empty result ⇒ no closure ⇒ compiler will cache the delegate.
        /// </summary>
        private static ImmutableArray<string> FindCapturedSymbols(IAnonymousFunctionOperation lambda) {
            var lambdaSymbol = lambda.Symbol;
            var builder = ImmutableArray.CreateBuilder<string>();
            var seen = new System.Collections.Generic.HashSet<string>();

            foreach (var op in lambda.Body.DescendantsAndSelf()) {
                switch (op) {
                    case IParameterReferenceOperation paramRef:
                        if (!IsOwnedBy(paramRef.Parameter, lambdaSymbol) && seen.Add(paramRef.Parameter.Name)) {
                            builder.Add(paramRef.Parameter.Name);
                        }
                        break;
                    case ILocalReferenceOperation localRef:
                        if (!IsOwnedBy(localRef.Local, lambdaSymbol) && seen.Add(localRef.Local.Name)) {
                            builder.Add(localRef.Local.Name);
                        }
                        break;
                    case IInstanceReferenceOperation iref when iref.ReferenceKind == InstanceReferenceKind.ContainingTypeInstance:
                        // Explicit 'this' / 'base' reference. ImplicitReceiver (object initializer's
                        // `new T { Field = … }` receiver) is NOT a capture.
                        if (seen.Add("this")) builder.Add("this");
                        break;
                    case IFieldReferenceOperation fieldRef when !fieldRef.Field.IsStatic && IsThisReceiver(fieldRef.Instance):
                        // Implicit 'this' via instance-field access (e.g. _externalField inside class).
                        if (seen.Add("this")) builder.Add("this");
                        break;
                    case IPropertyReferenceOperation propRef when !propRef.Property.IsStatic && IsThisReceiver(propRef.Instance):
                        if (seen.Add("this")) builder.Add("this");
                        break;
                    case IMethodReferenceOperation methodRef when !methodRef.Method.IsStatic && IsThisReceiver(methodRef.Instance):
                        if (seen.Add("this")) builder.Add("this");
                        break;
                    case IInvocationOperation invocation when invocation.TargetMethod is { IsStatic: false }
                                                           && IsThisReceiver(invocation.Instance):
                        // Implicit this-method call (e.g. SomeMethod() inside class).
                        if (seen.Add("this")) builder.Add("this");
                        break;
                }
            }
            return builder.ToImmutable();
        }

        /// <summary>
        /// True if <paramref name="instance"/> is an implicit (null) or explicit <c>this</c>/<c>base</c> receiver.
        /// Returns false for <c>ImplicitReceiver</c> — that kind is produced by object/collection initializers
        /// (<c>new T { Field = ... }</c>) and does NOT involve any closure capture.
        /// </summary>
        private static bool IsThisReceiver(IOperation instance) {
            if (instance is null) return true;
            return instance is IInstanceReferenceOperation iref
                   && iref.ReferenceKind == InstanceReferenceKind.ContainingTypeInstance;
        }

        /// <summary>True if <paramref name="symbol"/>'s containing symbol chain reaches <paramref name="lambdaSymbol"/>.</summary>
        private static bool IsOwnedBy(ISymbol symbol, IMethodSymbol lambdaSymbol) {
            var current = symbol?.ContainingSymbol;
            while (current is not null) {
                if (SymbolEqualityComparer.Default.Equals(current, lambdaSymbol)) return true;
                current = current.ContainingSymbol;
            }
            return false;
        }

        private static IAnonymousFunctionOperation ExtractLambda(IOperation value) => OperationHelpers.ExtractLambda(value);
    }
}

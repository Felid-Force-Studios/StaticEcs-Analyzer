using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FlowAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace FFS.Libraries.StaticEcs.Analyzers {
    internal enum RefChainMatch {
        /// <summary>No ref-returning member in the chain, or the chain is irrelevant to the rule.</summary>
        None,
        /// <summary>Chain resolves to a member from <see cref="StaticEcsSymbols.RefReturningTargets"/>.</summary>
        Write,
        /// <summary>Chain resolves to a member from <see cref="StaticEcsSymbols.RefReadonlyReadTargets"/>.</summary>
        Read,
        /// <summary>
        /// Ref-returning member is present in the chain, but the rule should NOT fire:
        /// either the final value is a reference type (copy concern doesn't apply), or a non-ref
        /// property breaks the ref chain between the local and the ref-returning call (binding by
        /// ref would not compile, so there's nothing to suggest).
        /// </summary>
        SuppressedByChain,
    }

    /// <summary>
    /// Tiny pure helpers shared across analyzers — extracted to keep individual analyzer files focused.
    /// All methods are pure: they only inspect their inputs and never report diagnostics.
    /// </summary>
    internal static class OperationHelpers {
        /// <summary>
        /// Unwraps implicit (compiler-inserted) conversion operations to expose the underlying value.
        /// Explicit conversions (user-written casts) are deliberately left in place — they may carry
        /// intent (escape-hatch markers etc.).
        /// </summary>
        public static IOperation UnwrapImplicitConversions(IOperation value) {
            while (value is IConversionOperation conv && conv.IsImplicit) {
                value = conv.Operand;
            }
            return value;
        }

        /// <summary>
        /// Unwraps an argument value down to its <see cref="IAnonymousFunctionOperation"/> if it is one.
        /// Returns null for method-group references or non-lambda values.
        /// </summary>
        public static IAnonymousFunctionOperation ExtractLambda(IOperation value) {
            while (value is IDelegateCreationOperation delegateCreation) {
                value = delegateCreation.Target;
            }
            return value as IAnonymousFunctionOperation;
        }

        /// <summary>
        /// Unwraps an argument value down to its <see cref="IMethodReferenceOperation"/> if it is a
        /// method-group reference (e.g. <c>.For(SomeMethod)</c>). Returns null for lambdas or other values.
        /// </summary>
        public static IMethodReferenceOperation ExtractMethodReference(IOperation value) {
            while (value is IDelegateCreationOperation delegateCreation) {
                value = delegateCreation.Target;
            }
            return value as IMethodReferenceOperation;
        }

        /// <summary>
        /// Walks the receiver chain of <paramref name="value"/> from outer to inner through field and
        /// property accesses, looking for a StaticEcs ref-returning member (FFSECS0010/0012 allow-list)
        /// or ref-readonly Read&lt;T&gt; member (FFSECS0011). Returns the classification and, when matched,
        /// the resolved <paramref name="target"/> symbol plus the <paramref name="matchedOperation"/>
        /// (the invocation/property reference node itself, used by analyzers for diagnostic location).
        ///
        /// <para>Suppression rules — when ref-returning member IS present but the diagnostic should not fire:</para>
        /// <list type="bullet">
        ///   <item>The outermost <paramref name="value"/> resolves to a reference type — the local would
        ///         hold a heap reference; mutations through it reach the same object, no copy concern.</item>
        ///   <item>Any step on the way from the outer value to the ref-returning member is an
        ///         <see cref="IPropertyReferenceOperation"/> whose property does NOT return by ref —
        ///         the ref chain is already broken at that property, so 'ref' binding cannot compile,
        ///         and the user is explicitly working with a copy.</item>
        /// </list>
        /// <para>Also returns <see cref="RefChainMatch.SuppressedByChain"/> when the ref-returning member's
        /// T payload is itself a reference type (e.g. <c>Resource&lt;MyClass&gt;.Value</c>) — same
        /// reasoning as the outer reference-type check, but applies when the outer type cannot be
        /// resolved (e.g. <c>var</c> on a member whose type is unknown).</para>
        /// </summary>
        public static RefChainMatch TryResolveRefReturningChain(
            IOperation value,
            StaticEcsSymbols symbols,
            out ISymbol target,
            out IOperation matchedOperation) {
            target = null;
            matchedOperation = null;
            if (value is null || symbols is null) return RefChainMatch.None;

            var outerIsReferenceType = value.Type?.IsReferenceType == true;
            // Outer value is atomically copyable (primitive, enum, IntPtr/UIntPtr, or reference type):
            // copying it through the call/binding losslessly conveys the value, so ref binding would
            // not change observable behavior. Mirrors the FFSECS0012 filter so chains like
            // `Read<T>().PrimitiveField` don't produce a false positive.
            var outerIsAtomicValue = value.Type is not null && IsAtomicallyValuedType(value.Type);
            var current = UnwrapImplicitConversions(value);
            var sawNonRefProperty = false;

            while (current is not null) {
                ISymbol candidate = current switch {
                    IInvocationOperation invocation => invocation.TargetMethod?.OriginalDefinition,
                    IPropertyReferenceOperation propertyRef => propertyRef.Property?.OriginalDefinition,
                    _ => null,
                };
                if (candidate is not null) {
                    var matchesWrite = symbols.RefReturningTargets.Contains(candidate);
                    var matchesRead = symbols.RefReadonlyReadTargets.Contains(candidate);
                    if (matchesWrite || matchesRead) {
                        var payload = TryGetRefReturningPayloadType(current);
                        if (outerIsReferenceType || outerIsAtomicValue || sawNonRefProperty || payload is null || !payload.IsValueType) {
                            return RefChainMatch.SuppressedByChain;
                        }
                        // Read-only branch (FFSECS0011): for small structs (≤ 8 bytes) the boundary copy
                        // fits in a register, so binding to 'ref readonly var' brings no measurable win
                        // and the hint becomes pure noise. Applied to ANY struct, readonly or not — the
                        // size, not readonly-ness, is what makes the copy cheap. Write branch
                        // (FFSECS0010) does NOT apply this: a writable ref to even a 4-byte struct can
                        // still lose mutations through a copy, so the diagnostic stays correctness-bound.
                        if (matchesRead && StructSizeEstimation.TryEstimateStructSize(payload, out var sizeBytes) && sizeBytes <= StructSizeEstimation.SmallStructPayloadByteThreshold) {
                            return RefChainMatch.SuppressedByChain;
                        }
                        target = candidate;
                        matchedOperation = current;
                        return matchesWrite ? RefChainMatch.Write : RefChainMatch.Read;
                    }
                }

                switch (current) {
                    case IFieldReferenceOperation fieldRef when fieldRef.Instance is not null:
                        current = UnwrapImplicitConversions(fieldRef.Instance);
                        continue;
                    case IPropertyReferenceOperation propRef when propRef.Instance is not null:
                        // A property breaks the ref chain unless it returns by ref / ref readonly.
                        // Once broken, any deeper ref-returning call cannot be reached by 'ref' binding
                        // from the outer expression — the user already accepted a copy at this boundary.
                        if (!propRef.Property.ReturnsByRef && !propRef.Property.ReturnsByRefReadonly) {
                            sawNonRefProperty = true;
                        }
                        current = UnwrapImplicitConversions(propRef.Instance);
                        continue;
                    default:
                        return RefChainMatch.None;
                }
            }
            return RefChainMatch.None;
        }

        /// <summary>
        /// Classifies an inline ref-returning member call (e.g. <c>entity.Ref&lt;T&gt;()</c>) by walking
        /// upward to its consumer to decide whether the result is used only for reading. Returns true
        /// when the diagnostic should fire (FFSECS0013 inline path):
        ///
        /// <para>Walks past <see cref="IConversionOperation"/> (implicit only), and past
        /// <see cref="IFieldReferenceOperation"/>/<see cref="IPropertyReferenceOperation"/> where the
        /// tracked node is the <c>Instance</c> — these are the steps of an inline
        /// <c>refReturn().Field.SubField</c> chain. The outermost consumer of the chain then decides:</para>
        ///
        /// <list type="bullet">
        /// <item>Assignment target / compound assignment / increment-decrement / ref/out argument /
        ///   non-readonly instance method on a non-readonly value-type receiver → mutation → no report.</item>
        /// <item><c>ref</c>-assignment with the chain as <c>Value</c> → ref-local binding; the
        ///   CFG pass owns this case → no report.</item>
        /// <item>Return from an enclosing ref-returning method/lambda → ref-forward → no report.</item>
        /// <item>Anything else (field/property read upward, by-value / <c>in</c> argument, return from
        ///   a non-ref enclosing, var initializer, ...) → read-only consumption → report.</item>
        /// </list>
        ///
        /// <para><paramref name="containingMethod"/> is used to resolve "enclosing method returns by ref"
        /// when the return statement isn't nested inside a lambda/local function.</para>
        /// </summary>
        public static bool TryClassifyInlineRefRead(
            IOperation refOp,
            StaticEcsSymbols symbols,
            IMethodSymbol containingMethod,
            out string readSiblingName,
            out Location diagnosticLocation) {
            readSiblingName = null;
            diagnosticLocation = null;
            if (refOp is null || symbols is null) return false;

            ISymbol target = refOp switch {
                IInvocationOperation invoc => invoc.TargetMethod?.OriginalDefinition,
                IPropertyReferenceOperation prop => prop.Property?.OriginalDefinition,
                _ => null,
            };
            if (target is null) return false;
            if (!symbols.RefTargetReadSiblings.TryGetValue(target, out var sibling)) return false;

            var current = refOp;
            while (true) {
                var parent = current.Parent;
                if (parent is null) return false; // detached / top-level — no consumer to classify.

                switch (parent) {
                    case IConversionOperation conv when conv.IsImplicit:
                        current = parent; continue;
                    case IFieldReferenceOperation fieldRef when ReferenceEquals(fieldRef.Instance, current):
                        current = parent; continue;
                    case IPropertyReferenceOperation propRef when ReferenceEquals(propRef.Instance, current):
                        current = parent; continue;

                    // Mutation shapes.
                    case ISimpleAssignmentOperation assignRef when assignRef.IsRef && ReferenceEquals(assignRef.Value, current):
                        // Ref-local binding: handled by the CFG pass — don't double-report here.
                        return false;
                    // Variable initializer for a ref-local: `ref var x = ref <chain>`. In the raw
                    // Operation tree (as opposed to the CFG normalization) this is the shape that
                    // appears, not an ISimpleAssignmentOperation. The CFG pass owns these — both the
                    // detection (when the binding is unused) and the suppression (when 'x' is later
                    // passed by ref / assigned through). Reporting from the inline action would
                    // double-fire and, worse, the codefix's rename would produce non-compiling code
                    // (a 'ref readonly T' value can't initialize a writable 'ref T' local).
                    case IVariableInitializerOperation initOp
                        when initOp.Parent is IVariableDeclaratorOperation declarator
                          && declarator.Symbol is { IsRef: true }:
                        return false;
                    case ISimpleAssignmentOperation assign when ReferenceEquals(assign.Target, current):
                        return false;
                    case ICompoundAssignmentOperation compound when ReferenceEquals(compound.Target, current):
                        return false;
                    case IIncrementOrDecrementOperation incdec when ReferenceEquals(incdec.Target, current):
                        return false;
                    case IArgumentOperation arg when arg.Parameter?.RefKind is RefKind.Ref or RefKind.Out:
                        return false;

                    // Method call where our chain is the receiver. Mutation only if the method might
                    // write through 'this' — non-readonly method on a non-readonly value-type receiver.
                    case IInvocationOperation invoc when ReferenceEquals(invoc.Instance, current): {
                        var receiverType = current.Type;
                        var method = invoc.TargetMethod;
                        if (receiverType is { IsValueType: true, IsReadOnly: false }
                            && method is { IsReadOnly: false }) {
                            return false;
                        }
                        readSiblingName = sibling;
                        diagnosticLocation = refOp.Syntax.GetLocation();
                        return true;
                    }

                    // Return: ref-forwarding from a ref-returning enclosing is fine; let FFSECS0010 decide.
                    case IReturnOperation ret:
                        if (IsEnclosingRefReturning(ret, containingMethod)) return false;
                        readSiblingName = sibling;
                        diagnosticLocation = refOp.Syntax.GetLocation();
                        return true;

                    default:
                        // var initializer, by-value / 'in' argument, condition, binary op, etc. — all
                        // read-only consumers of the (already-read) chain value.
                        readSiblingName = sibling;
                        diagnosticLocation = refOp.Syntax.GetLocation();
                        return true;
                }
            }
        }

        private static bool IsEnclosingRefReturning(IOperation op, IMethodSymbol containingMethod) {
            for (var parent = op.Parent; parent is not null; parent = parent.Parent) {
                switch (parent) {
                    case IAnonymousFunctionOperation anon:
                        return anon.Symbol.RefKind != RefKind.None;
                    case ILocalFunctionOperation local:
                        return local.Symbol.RefKind != RefKind.None;
                }
            }
            return containingMethod is not null && containingMethod.RefKind != RefKind.None;
        }

        /// <summary>
        /// True for types whose copy is losslessly equivalent to the original — reference types
        /// (the local holds a pointer that survives copies), enums, primitives, and IntPtr/UIntPtr.
        /// For such outer values, ref binding gives no benefit over a plain copy, so analyzers that
        /// flag "dropped ref-return" should suppress. Multi-field structs are intentionally excluded:
        /// their structure can be mutated through ref and lost in a copy.
        /// </summary>
        public static bool IsAtomicallyValuedType(ITypeSymbol type) {
            if (type.IsReferenceType) return true;
            if (type.TypeKind == TypeKind.Enum) return true;
            switch (type.SpecialType) {
                case SpecialType.System_Boolean:
                case SpecialType.System_Byte:
                case SpecialType.System_SByte:
                case SpecialType.System_Char:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_Decimal:
                case SpecialType.System_IntPtr:
                case SpecialType.System_UIntPtr:
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Extracts the T of a ref-returning member (Ref&lt;T&gt;()/Read&lt;T&gt;()/Resource&lt;T&gt;.Value/...)
        /// from its operation node. Returns null when T cannot be resolved (caller should treat that
        /// as "unknown — preserve default behavior").
        /// </summary>
        private static ITypeSymbol TryGetRefReturningPayloadType(IOperation operation) {
            return operation switch {
                IInvocationOperation invocation when invocation.TargetMethod is { } method =>
                    method.TypeArguments.Length > 0
                        ? method.TypeArguments[0]
                        : (method.ContainingType?.TypeArguments.Length > 0 ? method.ContainingType.TypeArguments[0] : null),
                IPropertyReferenceOperation propRef when propRef.Property?.ContainingType?.TypeArguments.Length > 0 =>
                    propRef.Property.ContainingType.TypeArguments[0],
                _ => null,
            };
        }

        /// <summary>
        /// Tries to build a <see cref="ControlFlowGraph"/> for the given body operation.
        /// <c>ControlFlowGraph.Create</c> requires a root operation whose Parent is null; this helper
        /// walks up the tree to find such a root and dispatches to the correct Create overload.
        /// Returns null for unsupported shapes (e.g. lambdas — Roslyn cannot create a CFG for them
        /// directly) or if construction throws.
        /// </summary>
        public static ControlFlowGraph TryCreateCfg(IOperation body) {
            if (body is null) return null;
            var root = body;
            while (root.Parent is not null) {
                root = root.Parent;
            }
            try {
                switch (root) {
                    case IBlockOperation b: return ControlFlowGraph.Create(b);
                    case IMethodBodyOperation m: return ControlFlowGraph.Create(m);
                    case IConstructorBodyOperation c: return ControlFlowGraph.Create(c);
                    case IFieldInitializerOperation f: return ControlFlowGraph.Create(f);
                    case IPropertyInitializerOperation p: return ControlFlowGraph.Create(p);
                    case IParameterInitializerOperation pi: return ControlFlowGraph.Create(pi);
                    default: return null;
                }
            } catch {
                return null;
            }
        }

        /// <summary>
        /// Builds a <see cref="ControlFlowGraph"/> for a lambda body. Roslyn forbids
        /// <c>ControlFlowGraph.Create(IAnonymousFunctionOperation)</c> directly — instead we build the
        /// enclosing method/initializer CFG first, locate the corresponding
        /// <see cref="IFlowAnonymousFunctionOperation"/> by syntax, and ask the parent CFG for the
        /// lambda's nested CFG. Returns null if the enclosing CFG can't be built or the flow-anon
        /// can't be located (rare; falls back to syntax-order analysis on caller side).
        /// </summary>
        public static ControlFlowGraph TryGetAnonymousFunctionCfg(IAnonymousFunctionOperation lambda) {
            if (lambda is null) return null;
            var parentCfg = TryCreateCfg(lambda);
            if (parentCfg is null) return null;
            var flowAnon = FindFlowAnonymousFunction(parentCfg, lambda.Syntax);
            if (flowAnon is null) return null;
            try {
                return parentCfg.GetAnonymousFunctionControlFlowGraph(flowAnon);
            } catch {
                return null;
            }
        }

        private static IFlowAnonymousFunctionOperation FindFlowAnonymousFunction(ControlFlowGraph cfg, SyntaxNode lambdaSyntax) {
            foreach (var block in cfg.Blocks) {
                foreach (var op in block.Operations) {
                    foreach (var descendant in op.DescendantsAndSelf()) {
                        if (descendant is IFlowAnonymousFunctionOperation flow && ReferenceEquals(flow.Syntax, lambdaSyntax)) {
                            return flow;
                        }
                    }
                }
                if (block.BranchValue is not null) {
                    foreach (var descendant in block.BranchValue.DescendantsAndSelf()) {
                        if (descendant is IFlowAnonymousFunctionOperation flow && ReferenceEquals(flow.Syntax, lambdaSyntax)) {
                            return flow;
                        }
                    }
                }
            }
            // Lambdas can be nested in regions (e.g. inside a captured `try`); walk LocalFunctions/AnonymousFunctions CFGs too.
            foreach (var localFnRef in cfg.LocalFunctions) {
                try {
                    var nested = cfg.GetLocalFunctionControlFlowGraph(localFnRef);
                    var found = FindFlowAnonymousFunction(nested, lambdaSyntax);
                    if (found is not null) return found;
                } catch { }
            }
            return null;
        }

        /// <summary>
        /// Visits the CFG of <paramref name="body"/> and every nested CFG (anonymous functions and local
        /// functions) exactly once. Each CFG is delivered to <paramref name="visit"/> along with the
        /// <see cref="IMethodSymbol"/> of its enclosing callable. No-op if the root CFG cannot be built.
        /// </summary>
        public static void WalkCfgRecursive(IOperation body, IMethodSymbol owner, Action<ControlFlowGraph, IMethodSymbol> visit) {
            var cfg = TryCreateCfg(body);
            if (cfg is not null) WalkCfgRecursive(cfg, owner, visit);
        }

        /// <summary>
        /// Same as the body-based overload but accepts an already-built <see cref="ControlFlowGraph"/>.
        /// Useful when the caller has the top-level CFG and wants to avoid rebuilding it.
        /// </summary>
        public static void WalkCfgRecursive(ControlFlowGraph cfg, IMethodSymbol owner, Action<ControlFlowGraph, IMethodSymbol> visit) {
            visit(cfg, owner);
            foreach (var anon in EnumerateFlowAnonymousFunctions(cfg)) {
                ControlFlowGraph nested;
                try { nested = cfg.GetAnonymousFunctionControlFlowGraph(anon); } catch { continue; }
                if (nested is not null) WalkCfgRecursive(nested, anon.Symbol, visit);
            }
            foreach (var localFn in cfg.LocalFunctions) {
                ControlFlowGraph nested;
                try { nested = cfg.GetLocalFunctionControlFlowGraph(localFn); } catch { continue; }
                if (nested is not null) WalkCfgRecursive(nested, localFn, visit);
            }
        }

        private static IEnumerable<IFlowAnonymousFunctionOperation> EnumerateFlowAnonymousFunctions(ControlFlowGraph cfg) {
            foreach (var block in cfg.Blocks) {
                foreach (var op in block.Operations)
                    foreach (var d in op.DescendantsAndSelf())
                        if (d is IFlowAnonymousFunctionOperation anon) yield return anon;
                if (block.BranchValue is not null)
                    foreach (var d in block.BranchValue.DescendantsAndSelf())
                        if (d is IFlowAnonymousFunctionOperation anon) yield return anon;
            }
        }
    }
}

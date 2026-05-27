using Microsoft.CodeAnalysis;

namespace FFS.Libraries.StaticEcs.Analyzers {
    internal static class Categories {
        public const string Usage = "StaticEcs (Usage)";
        public const string Correctness = "StaticEcs (Correctness)";
        public const string Performance = "StaticEcs (Performance)";
    }

    internal static class Diagnostics {
        public static readonly DiagnosticDescriptor RefReturnDroppedToCopy = new(
            id: FFSECSIds.FFSECS0010,
            title: "Ref-returning StaticEcs member result must be bound by 'ref'",
            messageFormat: "'{0}' returns by reference; the result is consumed by value here (directly or via a field/property chain like '.Field') — bind with 'ref var', or call 'Read<T>()' for an explicit copy",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Methods like Entity.Ref<T>(), Entity.Mut<T>(), Entity.Add<T>(), Components<T>.Ref/Mut/Add, Resource<T>.Value, NamedResource<T>.Value and Multi<T> indexers/iterators return ref T. Binding the result to a non-ref local silently copies the value, so any mutation goes to the copy, not the original value.");

        public static readonly DiagnosticDescriptor RefReadonlyReadDroppedToCopy = new(
            id: FFSECSIds.FFSECS0011,
            title: "Read<T>() result is bound to a copy",
            messageFormat: "'{0}' returns ref readonly; the result is consumed by value here (directly or via a field/property chain) — bind with 'ref readonly var' to avoid the copy",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "Entity.Read<T>() and Components<T>.Read(Entity) return ref readonly T. Binding to a non-ref local copies the value, which may be undesirable for large components. Surfaced as an IDE hint; silence via 'dotnet_diagnostic.FFSECS0011.severity = none' in .editorconfig if not relevant.");

        public static readonly DiagnosticDescriptor RefLocalNeverMutated = new(
            id: FFSECSIds.FFSECS0013,
            title: "Writable ref obtained but used for reading only",
            messageFormat: "'{0}' returns a writable ref but the result is only read here — use '{1}' for explicit read-only access",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "Two shapes trigger this rule: (1) a 'ref var x = ref entity.Ref<T>()' (or .Mut<T>()) binding whose body never writes through 'x', never passes it by 'ref'/'out', never takes a writable ref-alias from it, and never invokes a non-readonly instance method; (2) an inline 'entity.Ref<T>().Field' / '.Property' expression whose result flows into a read-only consumer (field/property read, 'in'/by-value argument, return from a non-ref method, etc.) without any of the above mutations. In both cases the writable ref is unused and 'Mut<T>()' additionally marks the component as changed for nothing. Prefer the read-only sibling — 'Read<T>()' for Entity/Components, 'ValueRO' for Resource/NamedResource, 'GetFirst()'/'GetLast()'/'Get(int)' for Multi, 'CurrentRO' for MultiComponentsIterator. Surfaced as an IDE suggestion; silence via 'dotnet_diagnostic.FFSECS0013.severity = none' in .editorconfig if not relevant.");

        public static readonly DiagnosticDescriptor RefLocalPassedByValue = new(
            id: FFSECSIds.FFSECS0012,
            title: "Ref-local backed by StaticEcs storage passed by value",
            messageFormat: "Local '{0}' is a 'ref'/'ref readonly' binding to StaticEcs storage; passing it as a value argument copies the component. Pass it by reference (use 'ref' for 'ref' locals, 'in' for 'ref readonly' locals), or introduce an explicit copy before the call.",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "When a local is declared as 'ref T x = ref entity.Ref<T>()' (or any other ref-returning StaticEcs member), the local is a reference to the underlying storage. Passing it as a value argument silently copies the struct at the call boundary — the called method mutates the copy, not the storage. Surfaced as an IDE suggestion; silence globally via 'dotnet_diagnostic.FFSECS0012.severity = none' in .editorconfig. Atomically-valued types (primitives, enums, reference types) are excluded automatically — they don't lose state through a copy.");

        public static readonly DiagnosticDescriptor EcsMarkerInterfaceMustBeStruct = new(
            id: FFSECSIds.FFSECS0020,
            title: "StaticEcs marker interface must be implemented by a struct",
            messageFormat: "Type '{0}' implements StaticEcs marker interface '{1}' and must be declared as 'struct', not 'class'. All registration paths require a struct constraint and reflection-based RegisterAll would skip class implementations.",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "ECS marker interfaces (IComponent, ITag, IEvent, ILinkType, ILinksType, IEntityType, IWorldType, IMultiComponent) are used throughout StaticEcs with 'where T : struct, X' constraints; implementing them via 'class' breaks generic dispatch and reflection-based type registration. For IMultiComponent specifically a class implementation also breaks the contiguous-segment storage layout.");

        public static readonly DiagnosticDescriptor MultiComponentSerializationOverrideRequired = new(
            id: FFSECSIds.FFSECS0021,
            title: "Non-unmanaged IMultiComponent must override Write/Read",
            messageFormat: "Type '{0}' implements IMultiComponent and is not unmanaged. Both 'Write(ref BinaryPackWriter)' and 'Read(ref BinaryPackReader)' should be overridden.",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Unmanaged IMultiComponent types use bulk memory copy automatically. Managed types (with reference fields) rely on user-provided Write/Read hooks.");

        public static readonly DiagnosticDescriptor QueryFilterRedundantComponent = new(
            id: FFSECSIds.FFSECS0050,
            title: "Redundant component in query filter",
            messageFormat: "Component '{0}' is referenced more than once in this query — {1}",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "WorldQuery filters of the same kind (All/None/Any) listing the same component twice, or a filter component overlapping with a lambda ref/in parameter or an IQuery struct's component generic, indicate a copy-paste mistake. The runtime does not error but the duplicate has no effect and obscures the intended filter.");

        public static readonly DiagnosticDescriptor QueryFilterContradiction = new(
            id: FFSECSIds.FFSECS0051,
            title: "Contradictory All+None in query filter",
            messageFormat: "Component '{0}' appears in both All<...> and None<...> in the same query filter — the resulting query never matches any entity",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "An All<T> requires T to be present on an entity; a sibling None<T> requires it to be absent. The combination is unsatisfiable and the query iterates over an empty set, silently — usually a copy-paste or refactoring mistake.");

        public static readonly DiagnosticDescriptor QueryForClosureCapture = new(
            id: FFSECSIds.FFSECS0031,
            title: "Lambda in Query.For captures outer state — allocates a closure each call",
            messageFormat: "Lambda passed to '.For(...)' captures '{0}', allocating a closure on every query call. Use a static lambda with the userData overload — For<TData>(userData, static (TData d, ...) => ...) — or a struct implementing IQuery, or 'foreach (var entity in W.Query<...>().Entities())'.",
            category: Categories.Performance,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "WorldQuery.For(delegate) is called in the per-frame iteration hot path. A capturing lambda generates a compiler-synthesized closure class that is allocated each time .For is invoked. Lambdas with no captures are cached in a static field by the compiler and allocated once per process — those are safe.");

        public static readonly DiagnosticDescriptor EntityUseAfterInvalidation = new(
            id: FFSECSIds.FFSECS0040,
            title: "Entity alias used after invalidation",
            messageFormat: "'{0}' aliases entity storage; access here is reachable from a prior '{1}' call that invalidates the entity. Read the value into a local before the call, or restructure to access first.",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "Ref/in parameters of Query.For lambdas and IQuery.Invoke methods, as well as ref-locals obtained from entity.Ref<T>()/Read<T>()/Mut<T>()/Add<T>(), are aliases into the underlying component storage. Calls like Destroy/MoveTo/Unload (full invalidation) or Delete<T> (per-component) free or change that storage. Subsequent access through the alias reads garbage or the data of a reused slot. Runtime DEBUG asserts in strict-mode iteration catch some of these (see FFS_ECS_DEBUG), but compile-time detection covers all build configurations.");

        public static readonly DiagnosticDescriptor EntityHandleUseAfterInvalidation = new(
            id: FFSECSIds.FFSECS0041,
            title: "Entity handle used after invalidation",
            messageFormat: "Entity '{0}' is used here after a prior '{1}' call invalidated the handle. The slot may be reused by a new entity; reassign the variable to a freshly created/loaded entity before using it again.",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Error,
            isEnabledByDefault: true,
            description: "After Destroy/MoveTo/Unload the entity handle (uint id) no longer refers to a live entity — calling methods on it or reading its fields hits whichever entity now occupies the slot (often none). The only safe operation on the variable is reassigning it ('entity = W.NewEntity()' or similar).");

        public static readonly DiagnosticDescriptor RefWithoutPresenceGuard = new(
            id: FFSECSIds.FFSECS0042,
            title: "Ref/Mut/Read<T> called without a visible presence guarantee",
            messageFormat: "'{0}<{1}>()' is called on entity without a visible guarantee that '{1}' is present. Wrap in 'if (entity.Has<{1}>())', use a query with All<{1}> in its filter, or call Add/Set<{1}> first.",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "Entity.Ref<T>(), Mut<T>(), Read<T>() and the matching Components<T>.Ref/Mut/Read(entity) overloads require T to be present on the entity. The analyzer warns when, at the call site, there is no statically visible proof of that. Recognised proofs along the current control-flow path: (1) the true branch of a previous 'entity.Has<T>()', 'HasEnabled<T>()', 'HasDisabled<T>()' (any arity) or 'entity.IsMatch<F>()' where F decomposes (through And<>) to All<T>/AllOnlyDisabled<T>/AllWithDisabled<T>; (2) the body of a Query<TFilter>().For(...) lambda or an IQuery<...>.Invoke method whose TFilter likewise contains All*<T>, applied to the entity parameter (additionally, any ref/in component parameter of that lambda/Invoke is itself a guarantee); (3) a prior 'entity.Add<T>()', 'Set<T>()', 'Ref<T>()', 'Mut<T>()' or 'Read<T>()' on the same entity local/parameter without an intervening Delete<T>/Destroy/MoveTo/Unload. The diagnostic only fires when 'entity' is a tracked local, parameter, or foreach iteration variable — chained/property/inline entity expressions are also reported because no Has guard could attach to them. Silence per-call via '#pragma warning disable FFSECS0042' or globally via 'dotnet_diagnostic.FFSECS0042.severity = none' in .editorconfig.");

        public static readonly DiagnosticDescriptor IsMatchReplaceableWithDirectMethod = new(
            id: FFSECSIds.FFSECS0032,
            title: "IsMatch<TFilter>() can be replaced with a direct Entity method",
            messageFormat: "Filter '{0}' has a direct equivalent on Entity — use '{1}' instead of 'IsMatch<{0}<...>>'",
            category: Categories.Usage,
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "Entity has direct presence-check methods (Has/HasEnabled/HasDisabled and their *Any variants, plus Is/IsAny/IsNot for entity-type filters) that are shorter and clearer than the generic IsMatch<TFilter>() with a simple filter. This is a style suggestion; no functional difference.");

        public static readonly DiagnosticDescriptor ForeachEntitiesConvertibleToFor = new(
            id: FFSECSIds.FFSECS0033,
            title: "foreach over Entities() can be replaced with Query.For(...)",
            messageFormat: "This 'foreach' over WorldQuery.Entities() can be replaced with a call to '.For(...)' — components used via 'entity.Ref/Mut/Read<T>()' for T in All<...> become ref/in lambda parameters",
            category: Categories.Usage,
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "The 'foreach (var entity in W.Query<All<T1,...>>().Entities()) { ref var x = ref entity.Ref<T1>(); ... }' pattern has a more compact, intent-revealing form via 'W.Query<...>().For((ref T1 x, ...) => { ... })'. The codefix moves each component reached through 'entity.Ref<T>()/Mut<T>()/Read<T>()' for a T listed in All<...> into the lambda parameter list (ref for Ref/Mut, in for Read), removes the matching ref-local declarations, and trims the absorbed components from All<...>. Other filters (None/Any/EntityIs/etc.) are preserved verbatim. If the body captures one outer symbol, the codefix uses the 'For<TData>(ref data, static (ref TData data, ...) => ...)' overload so the lambda stays allocation-free. Surfaced as an IDE suggestion; silence via 'dotnet_diagnostic.FFSECS0033.severity = none' in .editorconfig.");

        public static readonly DiagnosticDescriptor QueryForRefParameterNotMutated = new(
            id: FFSECSIds.FFSECS0030,
            title: "Query.For lambda parameter declared 'ref' but never mutated",
            messageFormat: "Parameter '{0}' is declared as 'ref {1}' but never assigned inside the lambda. Use the 'in {1}' overload of WorldQuery.For(...) to signal read-only intent.",
            category: Categories.Correctness,
            defaultSeverity: DiagnosticSeverity.Info,
            isEnabledByDefault: true,
            description: "WorldQuery.For has overloads accepting 'in T' for read-only components. Keeping 'ref T' for a parameter that is only read misleads the reader. More importantly, when change-tracking is enabled for the component, every 'ref T' access marks the component as changed regardless of whether the body actually writes to it — accidental 'dirty' flags propagate to change-driven systems. Switching to 'in T' keeps the component read-only at the storage level and skips the change mark.");
    }
}

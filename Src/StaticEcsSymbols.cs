using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace FFS.Libraries.StaticEcs.Analyzers {
    /// <summary>
    /// Resolved StaticEcs API symbols cached once per compilation. Built in CompilationStartAction
    /// and passed to all registered actions via closure. All symbol lookups go through this object,
    /// never raw <see cref="Compilation.GetTypeByMetadataName"/> at action-time.
    /// </summary>
    internal sealed class StaticEcsSymbols {
        public const string RootNamespace = "FFS.Libraries.StaticEcs";
        public const string StaticEcsAssembly = "FFS.StaticEcs";
        public const string StaticEcsDebugAssembly = "FFS.StaticEcs.Debug";

        // ECS marker interfaces (top-level). Used by FFSECS0020/0021.
        public INamedTypeSymbol IComponent { get; private set; }
        public INamedTypeSymbol ITag { get; private set; }
        public INamedTypeSymbol IEvent { get; private set; }
        public INamedTypeSymbol IMultiComponent { get; private set; }
        public INamedTypeSymbol IWorldType { get; private set; }
        public INamedTypeSymbol IQueryFilter { get; private set; }
        public INamedTypeSymbol IResource { get; private set; }
        public INamedTypeSymbol ILinkType { get; private set; }
        public INamedTypeSymbol ILinksType { get; private set; }
        public INamedTypeSymbol IEntityType { get; private set; }
        public INamedTypeSymbol IDisableable { get; private set; }

        /// <summary>All ECS marker interfaces the rule FFSECS0020 covers, including IMultiComponent.</summary>
        public ImmutableHashSet<INamedTypeSymbol> EcsMarkerInterfaces { get; private set; }

        // Containing nested types (under World&lt;TWorld&gt;).
        public INamedTypeSymbol WorldQuery { get; private set; }
        public INamedTypeSymbol EntityType { get; private set; }

        /// <summary>FFSECS0040 — methods on Entity that free or fully invalidate the entity slot.
        /// Triggering any of these on an entity makes ALL ref/in aliases backed by that entity stale.</summary>
        public ImmutableHashSet<ISymbol> EntityFullInvalidators { get; private set; }

        /// <summary>FFSECS0040 — methods on Entity that invalidate a specific component on the entity.
        /// Triggering Delete&lt;T&gt;() invalidates only aliases of type T (not other components).</summary>
        public ImmutableHashSet<ISymbol> EntityComponentInvalidators { get; private set; }

        /// <summary>FFSECS0040 — IQuery callback interfaces (root + nested Write/Read variants). A struct
        /// implementing any of these is a candidate for use-after-invalidation analysis of its Invoke method.</summary>
        public ImmutableHashSet<INamedTypeSymbol> QueryCallbackInterfaces { get; private set; }

        /// <summary>FFSECS0042 — Entity.Has/HasEnabled/HasDisabled (any arity). A true branch of these
        /// adds each generic argument as a presence guarantee for the receiver entity.</summary>
        public ImmutableHashSet<ISymbol> EntityHasMethods { get; private set; }

        /// <summary>FFSECS0042 — Entity.Set&lt;T&gt;(...) overloads. Calling Set on an entity establishes
        /// presence of T for the receiver (no guard check required before subsequent Ref/Mut/Read&lt;T&gt;).</summary>
        public ImmutableHashSet<ISymbol> EntitySetMethods { get; private set; }

        /// <summary>FFSECS0042 — Entity.Ref&lt;T&gt; / Mut&lt;T&gt; / Read&lt;T&gt; overloads. These are the
        /// check points for the rule (each call is verified against the current guarantee set), and also
        /// establish the guarantee for subsequent calls.</summary>
        public ImmutableHashSet<ISymbol> EntityRefAccessMethods { get; private set; }

        /// <summary>FFSECS0042 — Entity.Add&lt;T&gt; overloads. Establish presence of T for subsequent calls
        /// but, unlike Ref/Mut/Read, are not themselves check points.</summary>
        public ImmutableHashSet<ISymbol> EntityAddMethods { get; private set; }

        // FFSECS0050/0051 — query filter components/composers.
        public INamedTypeSymbol WorldOpenGeneric { get; private set; }
        public ImmutableHashSet<INamedTypeSymbol> QueryFilterAll { get; private set; }
        public ImmutableHashSet<INamedTypeSymbol> QueryFilterNone { get; private set; }
        public ImmutableHashSet<INamedTypeSymbol> QueryFilterAny { get; private set; }
        public ImmutableHashSet<INamedTypeSymbol> QueryFilterAnd { get; private set; }

        /// <summary>FFSECS0010 allow-list — concrete ref-returning members on StaticEcs nested types.
        /// Stored as original-definition <see cref="ISymbol"/>s; comparison via
        /// <see cref="SymbolEqualityComparer.Default"/>.</summary>
        public ImmutableHashSet<ISymbol> RefReturningTargets { get; private set; }

        /// <summary>FFSECS0011 allow-list — Entity.Read and Components&lt;T&gt;.Read.</summary>
        public ImmutableHashSet<ISymbol> RefReadonlyReadTargets { get; private set; }

        /// <summary>FFSECS0013 — mapping from a writable ref-returning member (subset of
        /// <see cref="RefReturningTargets"/>) to the name of its read-only sibling member. Members
        /// without a read-only sibling (e.g. <c>Add</c>) are intentionally absent — the analyzer
        /// must suppress reports for them.</summary>
        public ImmutableDictionary<ISymbol, string> RefTargetReadSiblings { get; private set; }

        /// <summary>FFSECS0021 dependencies — BinaryPackWriter/Reader, to identify Write/Read overrides on IMultiComponent.</summary>
        public INamedTypeSymbol BinaryPackWriter { get; private set; }
        public INamedTypeSymbol BinaryPackReader { get; private set; }

        /// <summary>FFSECS0032 — <c>World&lt;TWorld&gt;.Entity.IsMatch&lt;Q&gt;</c> (single overload, generic). Triggers the suggestion.</summary>
        public ISymbol EntityIsMatch { get; private set; }

        /// <summary>FFSECS0032 — mapping from filter <see cref="INamedTypeSymbol"/> (OriginalDefinition,
        /// per arity) to the recommended direct Entity method name, whether to wrap in <c>!</c>, and
        /// whether each filter type argument must also implement <see cref="IDisableable"/>.</summary>
        public ImmutableDictionary<INamedTypeSymbol, IsMatchReplacement> IsMatchReplacements { get; private set; }

        /// <summary>FFSECS0050/0051 — <c>World&lt;TWorld&gt;.Query</c> static entry methods (all overloads, generic and non-generic).</summary>
        public ImmutableHashSet<ISymbol> QueryEntryMethods { get; private set; }

        /// <summary>FFSECS0030/0031/0050/0051 — <c>For</c> overloads on every query-builder type (WorldQuery, WriteQuery, ReadQuery, BlockWriteQuery, BlockReadQuery, plus nested ReadQuery within each).</summary>
        public ImmutableHashSet<ISymbol> QueryBuilderForMethods { get; private set; }

        /// <summary>FFSECS0050/0051 — terminal chain methods on query-builder types (<c>Entities</c>, <c>Write</c>, <c>Read</c>, <c>WriteBlock</c>, <c>ReadBlock</c>). Names are kept as a single array constant inside <see cref="Create"/>.</summary>
        public ImmutableHashSet<ISymbol> QueryBuilderTerminalMethods { get; private set; }

        public readonly struct IsMatchReplacement {
            public readonly string MethodName;
            public readonly bool Negate;
            public readonly bool RequiresDisableable;
            public IsMatchReplacement(string methodName, bool negate, bool requiresDisableable) {
                MethodName = methodName;
                Negate = negate;
                RequiresDisableable = requiresDisableable;
            }
        }

        public static StaticEcsSymbols Create(Compilation compilation) {
            var iComponent = compilation.GetTypeByMetadataName(RootNamespace + ".IComponent");
            if (iComponent is null) {
                return null;
            }

            var symbols = new StaticEcsSymbols {
                IComponent = iComponent,
                ITag = compilation.GetTypeByMetadataName(RootNamespace + ".ITag"),
                IEvent = compilation.GetTypeByMetadataName(RootNamespace + ".IEvent"),
                IMultiComponent = compilation.GetTypeByMetadataName(RootNamespace + ".IMultiComponent"),
                IWorldType = compilation.GetTypeByMetadataName(RootNamespace + ".IWorldType"),
                IQueryFilter = compilation.GetTypeByMetadataName(RootNamespace + ".IQueryFilter"),
                IResource = compilation.GetTypeByMetadataName(RootNamespace + ".IResource"),
                ILinkType = compilation.GetTypeByMetadataName(RootNamespace + ".ILinkType"),
                ILinksType = compilation.GetTypeByMetadataName(RootNamespace + ".ILinksType"),
                IEntityType = compilation.GetTypeByMetadataName(RootNamespace + ".IEntityType"),
                IDisableable = compilation.GetTypeByMetadataName(RootNamespace + ".IDisableable"),
                BinaryPackWriter = compilation.GetTypeByMetadataName("FFS.Libraries.StaticPack.BinaryPackWriter"),
                BinaryPackReader = compilation.GetTypeByMetadataName("FFS.Libraries.StaticPack.BinaryPackReader"),
            };

            symbols.EcsMarkerInterfaces = BuildMarkerSet(symbols);

            // Nested types under World<TWorld> use metadata name "World`1+<Name>" (or "+<Name>`N" for generic nested).
            var entityType = compilation.GetTypeByMetadataName(RootNamespace + ".World`1+Entity");
            var componentsType = compilation.GetTypeByMetadataName(RootNamespace + ".World`1+Components`1");
            var resourceType = compilation.GetTypeByMetadataName(RootNamespace + ".World`1+Resource`1");
            var namedResourceType = compilation.GetTypeByMetadataName(RootNamespace + ".World`1+NamedResource`1");
            var multiType = compilation.GetTypeByMetadataName(RootNamespace + ".World`1+Multi`1");
            var multiIteratorType = compilation.GetTypeByMetadataName(RootNamespace + ".World`1+MultiComponentsIterator`1");
            symbols.WorldQuery = compilation.GetTypeByMetadataName(RootNamespace + ".World`1+WorldQuery`1");

            symbols.EntityType = entityType;
            symbols.RefReturningTargets = BuildRefReturningTargets(
                entityType, componentsType, resourceType, namedResourceType, multiType, multiIteratorType);
            symbols.RefReadonlyReadTargets = BuildRefReadonlyReadTargets(entityType, componentsType);
            symbols.RefTargetReadSiblings = BuildRefTargetReadSiblings(
                entityType, componentsType, resourceType, namedResourceType, multiType, multiIteratorType);
            symbols.EntityFullInvalidators = BuildEntityFullInvalidators(entityType);
            symbols.EntityComponentInvalidators = BuildEntityComponentInvalidators(entityType);
            symbols.QueryCallbackInterfaces = BuildQueryCallbackInterfaces(compilation);

            // FFSECS0042 — Entity API surface needed for the presence-guard rule.
            symbols.EntityHasMethods = BuildEntityMethodSet(entityType, "Has", "HasEnabled", "HasDisabled");
            symbols.EntitySetMethods = BuildEntityMethodSet(entityType, "Set");
            symbols.EntityRefAccessMethods = BuildEntityMethodSet(entityType, "Ref", "Mut", "Read");
            symbols.EntityAddMethods = BuildEntityMethodSet(entityType, "Add");

            // World<TWorld> open-generic — used to identify Query<...>() calls.
            symbols.WorldOpenGeneric = compilation.GetTypeByMetadataName(RootNamespace + ".World`1");

            // Query filter components: All<T0..T7>, None<T0..T7>, Any<T0..T7>, And<F0..F5>.
            // Also include disabled-state variants: AllOnlyDisabled / AllWithDisabled / NoneWithDisabled /
            // AnyOnlyDisabled / AnyWithDisabled — they share All/None/Any semantics for component filtering.
            symbols.QueryFilterAll = BuildArityRange(compilation, "All", 1, 8)
                .Union(BuildArityRange(compilation, "AllOnlyDisabled", 1, 8))
                .Union(BuildArityRange(compilation, "AllWithDisabled", 1, 8));
            symbols.QueryFilterNone = BuildArityRange(compilation, "None", 1, 8)
                .Union(BuildArityRange(compilation, "NoneWithDisabled", 1, 8));
            symbols.QueryFilterAny = BuildArityRange(compilation, "Any", 2, 8)
                .Union(BuildArityRange(compilation, "AnyOnlyDisabled", 2, 8))
                .Union(BuildArityRange(compilation, "AnyWithDisabled", 2, 8));
            symbols.QueryFilterAnd = BuildArityRange(compilation, "And", 2, 6);

            symbols.EntityIsMatch = ResolveEntityIsMatch(entityType);
            symbols.IsMatchReplacements = BuildIsMatchReplacements(compilation);

            // Query entry and builder methods: resolved once so analyzers compare by ISymbol,
            // never by method name. Builder discovery walks every type nested under World<TWorld>
            // and keeps those classified by IsQueryBuilderType.
            symbols.QueryEntryMethods = BuildMethodSet(symbols.WorldOpenGeneric, "Query");
            var queryBuilderTypes = CollectQueryBuilderTypes(symbols);
            symbols.QueryBuilderForMethods = BuildMethodSetForTypes(queryBuilderTypes, "For");
            symbols.QueryBuilderTerminalMethods = BuildMethodSetForTypes(
                queryBuilderTypes,
                "Entities", "Write", "Read", "WriteBlock", "ReadBlock");

            return symbols;
        }

        private static List<INamedTypeSymbol> CollectQueryBuilderTypes(StaticEcsSymbols symbols) {
            var result = new List<INamedTypeSymbol>();
            if (symbols.WorldOpenGeneric is null) return result;
            Walk(symbols.WorldOpenGeneric);
            return result;

            void Walk(INamedTypeSymbol parent) {
                foreach (var nested in parent.GetTypeMembers()) {
                    if (symbols.IsQueryBuilderType(nested)) result.Add(nested);
                    Walk(nested);
                }
            }
        }

        private static ImmutableHashSet<ISymbol> BuildMethodSet(INamedTypeSymbol type, params string[] names) {
            var builder = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
            if (type is null) return builder.ToImmutable();
            foreach (var name in names)
                foreach (var member in type.GetMembers(name).OfType<IMethodSymbol>())
                    builder.Add(member.OriginalDefinition);
            return builder.ToImmutable();
        }

        private static ImmutableHashSet<ISymbol> BuildMethodSetForTypes(List<INamedTypeSymbol> types, params string[] names) {
            var builder = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
            foreach (var type in types)
                foreach (var name in names)
                    foreach (var member in type.GetMembers(name).OfType<IMethodSymbol>())
                        builder.Add(member.OriginalDefinition);
            return builder.ToImmutable();
        }

        private static ISymbol ResolveEntityIsMatch(INamedTypeSymbol entityType) {
            if (entityType is null) return null;
            foreach (var method in entityType.GetMembers("IsMatch").OfType<IMethodSymbol>()) {
                if (method.TypeParameters.Length == 1) return method.OriginalDefinition;
            }
            return null;
        }

        private static ImmutableDictionary<INamedTypeSymbol, IsMatchReplacement> BuildIsMatchReplacements(Compilation compilation) {
            var builder = ImmutableDictionary.CreateBuilder<INamedTypeSymbol, IsMatchReplacement>(SymbolEqualityComparer.Default);

            void AddRange(string filterBaseName, int minArity, int maxArity, string methodName, bool negate, bool requiresDisableable) {
                for (var arity = minArity; arity <= maxArity; arity++) {
                    var filter = compilation.GetTypeByMetadataName(RootNamespace + "." + filterBaseName + "`" + arity);
                    if (filter is not null) builder[filter] = new IsMatchReplacement(methodName, negate, requiresDisableable);
                }
            }

            // Component/tag-presence filters, arity 1-3 (direct methods cap at arity 3).
            AddRange("All",                1, 3, "HasEnabled",     negate: false, requiresDisableable: true);
            AddRange("AllWithDisabled",    1, 3, "Has",            negate: false, requiresDisableable: false);
            AddRange("AllOnlyDisabled",    1, 3, "HasDisabled",    negate: false, requiresDisableable: false); // filter already requires IDisableable
            AddRange("Any",                2, 3, "HasEnabledAny",  negate: false, requiresDisableable: true);
            AddRange("AnyWithDisabled",    2, 3, "HasAny",         negate: false, requiresDisableable: false);
            AddRange("AnyOnlyDisabled",    2, 3, "HasDisabledAny", negate: false, requiresDisableable: false);
            // None has no arity-1 'Any' direct equivalent — for arity 1 use HasEnabled, for 2-3 use HasEnabledAny.
            AddRange("None",               1, 1, "HasEnabled",     negate: true,  requiresDisableable: true);
            AddRange("None",               2, 3, "HasEnabledAny",  negate: true,  requiresDisableable: true);
            AddRange("NoneWithDisabled",   1, 1, "Has",            negate: true,  requiresDisableable: false);
            AddRange("NoneWithDisabled",   2, 3, "HasAny",         negate: true,  requiresDisableable: false);
            // Entity-type filters.
            AddRange("EntityIs",           1, 1, "Is",             negate: false, requiresDisableable: false);
            AddRange("EntityIsAny",        2, 3, "IsAny",          negate: false, requiresDisableable: false);
            AddRange("EntityIsNot",        1, 3, "IsNot",          negate: false, requiresDisableable: false);

            return builder.ToImmutable();
        }

        private static ImmutableHashSet<INamedTypeSymbol> BuildArityRange(Compilation compilation, string baseName, int minArity, int maxArity) {
            var builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            for (var arity = minArity; arity <= maxArity; arity++) {
                var name = RootNamespace + "." + baseName + "`" + arity;
                var symbol = compilation.GetTypeByMetadataName(name);
                if (symbol is not null) builder.Add(symbol);
            }
            return builder.ToImmutable();
        }

        private static ImmutableHashSet<ISymbol> BuildEntityMethodSet(INamedTypeSymbol entity, params string[] names) {
            var builder = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
            if (entity is null) return builder.ToImmutable();
            foreach (var name in names) {
                foreach (var member in entity.GetMembers(name).OfType<IMethodSymbol>()) {
                    builder.Add(member.OriginalDefinition);
                }
            }
            return builder.ToImmutable();
        }

        private static ImmutableHashSet<ISymbol> BuildEntityFullInvalidators(INamedTypeSymbol entity) {
            var builder = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
            AddAllOverloads(builder, entity, "Destroy");
            AddAllOverloads(builder, entity, "MoveTo");
            AddAllOverloads(builder, entity, "Unload");
            return builder.ToImmutable();
        }

        private static ImmutableHashSet<ISymbol> BuildEntityComponentInvalidators(INamedTypeSymbol entity) {
            var builder = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
            AddAllOverloads(builder, entity, "Delete");
            return builder.ToImmutable();
        }

        private static ImmutableHashSet<INamedTypeSymbol> BuildQueryCallbackInterfaces(Compilation compilation) {
            var builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var iQueryRoot = compilation.GetTypeByMetadataName(RootNamespace + ".World`1+IQuery");
            if (iQueryRoot is not null) CollectNestedInterfaces(iQueryRoot, builder);
            var iQueryBlockRoot = compilation.GetTypeByMetadataName(RootNamespace + ".World`1+IQueryBlock");
            if (iQueryBlockRoot is not null) CollectNestedInterfaces(iQueryBlockRoot, builder);
            return builder.ToImmutable();
        }

        /// <summary>
        /// True if <paramref name="type"/> is <c>WorldQuery&lt;TFilter&gt;</c> or any nested type within it
        /// (the various nested WriteQuery/ReadQuery state structs that host delegate-based For overloads).
        /// </summary>
        public bool IsWithinWorldQuery(INamedTypeSymbol type) {
            if (type is null || WorldQuery is null) return false;
            var current = type;
            while (current is not null) {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, WorldQuery)) return true;
                current = current.ContainingType;
            }
            return false;
        }

        /// <summary>
        /// True if <paramref name="type"/> (or any of its containing-type ancestors) has a first generic
        /// parameter that implements <see cref="IQueryFilter"/>. This covers WorldQuery&lt;TFilter&gt;
        /// and all fluent builder types (WriteQuery, ReadQuery, BlockWriteQuery, BlockReadQuery, plus
        /// nested ReadQuery within them).
        /// </summary>
        public bool IsQueryBuilderType(INamedTypeSymbol type) {
            if (type is null || IQueryFilter is null) return false;
            var current = type;
            while (current is not null) {
                if (current.TypeArguments.Length >= 1 && FirstArgImplementsIQueryFilter(current.TypeArguments[0])) {
                    return true;
                }
                current = current.ContainingType;
            }
            return false;
        }

        private bool FirstArgImplementsIQueryFilter(ITypeSymbol firstArg) {
            return FirstArgImplementsIQueryFilter(firstArg, visited: null);
        }

        private bool FirstArgImplementsIQueryFilter(ITypeSymbol firstArg, System.Collections.Generic.HashSet<ITypeParameterSymbol> visited) {
            switch (firstArg) {
                case INamedTypeSymbol named:
                    return ImplementsIQueryFilterInternal(named);
                case ITypeParameterSymbol typeParam:
                    // Generic-definition context (e.g. when analyzing the StaticEcs assembly itself):
                    // the first arg is still a type parameter — walk its constraint chain. Constraints may
                    // be other type parameters (`where TInner : TOuter`), so recurse with a cycle guard.
                    visited ??= new System.Collections.Generic.HashSet<ITypeParameterSymbol>(SymbolEqualityComparer.Default);
                    if (!visited.Add(typeParam)) return false;
                    foreach (var constraint in typeParam.ConstraintTypes) {
                        if (constraint is INamedTypeSymbol namedConstraint && ImplementsIQueryFilterInternal(namedConstraint)) return true;
                        if (SymbolEqualityComparer.Default.Equals(constraint, IQueryFilter)) return true;
                        if (constraint is ITypeParameterSymbol && FirstArgImplementsIQueryFilter(constraint, visited)) return true;
                    }
                    return false;
                default:
                    return false;
            }
        }

        private bool ImplementsIQueryFilterInternal(INamedTypeSymbol type) {
            if (SymbolEqualityComparer.Default.Equals(type, IQueryFilter)) return true;
            foreach (var iface in type.AllInterfaces) {
                if (SymbolEqualityComparer.Default.Equals(iface, IQueryFilter)) return true;
            }
            return false;
        }

        private static void CollectNestedInterfaces(INamedTypeSymbol type, ImmutableHashSet<INamedTypeSymbol>.Builder builder) {
            if (type.TypeKind == TypeKind.Interface) {
                builder.Add(type.OriginalDefinition);
            }
            foreach (var nested in type.GetTypeMembers()) {
                CollectNestedInterfaces(nested, builder);
            }
        }

        private static ImmutableHashSet<INamedTypeSymbol> BuildMarkerSet(StaticEcsSymbols s) {
            var builder = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            void AddIfPresent(INamedTypeSymbol symbol) {
                if (symbol is not null) builder.Add(symbol);
            }
            AddIfPresent(s.IComponent);
            AddIfPresent(s.ITag);
            AddIfPresent(s.IEvent);
            AddIfPresent(s.IMultiComponent);
            AddIfPresent(s.IWorldType);
            AddIfPresent(s.ILinkType);
            AddIfPresent(s.ILinksType);
            AddIfPresent(s.IEntityType);
            return builder.ToImmutable();
        }

        private static ImmutableHashSet<ISymbol> BuildRefReturningTargets(
            INamedTypeSymbol entity,
            INamedTypeSymbol components,
            INamedTypeSymbol resource,
            INamedTypeSymbol namedResource,
            INamedTypeSymbol multi,
            INamedTypeSymbol multiIterator) {

            var builder = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
            // Entity.Ref<T>(), Mut<T>(), Add<T>(), Add<T>(out bool)
            AddAllOverloads(builder, entity, "Ref");
            AddAllOverloads(builder, entity, "Mut");
            AddAllOverloads(builder, entity, "Add");
            // Components<T>.Ref(Entity), Mut(Entity), Add(Entity), Add(Entity, out bool)
            AddAllOverloads(builder, components, "Ref");
            AddAllOverloads(builder, components, "Mut");
            AddAllOverloads(builder, components, "Add");
            // Resource<T>.Value, NamedResource<T>.Value
            AddProperty(builder, resource, "Value");
            AddProperty(builder, namedResource, "Value");
            // Multi<T>.First(), Last(), this[int]
            AddAllOverloads(builder, multi, "First");
            AddAllOverloads(builder, multi, "Last");
            AddIndexer(builder, multi);
            // MultiComponentsIterator<T>.Current
            AddProperty(builder, multiIterator, "Current");
            return builder.ToImmutable();
        }

        private static ImmutableHashSet<ISymbol> BuildRefReadonlyReadTargets(
            INamedTypeSymbol entity,
            INamedTypeSymbol components) {

            var builder = ImmutableHashSet.CreateBuilder<ISymbol>(SymbolEqualityComparer.Default);
            AddAllOverloads(builder, entity, "Read");
            AddAllOverloads(builder, components, "Read");
            return builder.ToImmutable();
        }

        private static ImmutableDictionary<ISymbol, string> BuildRefTargetReadSiblings(
            INamedTypeSymbol entity,
            INamedTypeSymbol components,
            INamedTypeSymbol resource,
            INamedTypeSymbol namedResource,
            INamedTypeSymbol multi,
            INamedTypeSymbol multiIterator) {

            var builder = ImmutableDictionary.CreateBuilder<ISymbol, string>(SymbolEqualityComparer.Default);
            // Entity.Ref<T>(), Entity.Mut<T>() → Entity.Read<T>(). Add<T>() has no read sibling.
            MapMethods(builder, entity, "Ref", "Read");
            MapMethods(builder, entity, "Mut", "Read");
            // Components<T>.Ref(Entity), Mut(Entity) → Read(Entity). Add has no read sibling.
            MapMethods(builder, components, "Ref", "Read");
            MapMethods(builder, components, "Mut", "Read");
            // Resource<T>.Value, NamedResource<T>.Value → ValueRO.
            MapProperty(builder, resource, "Value", "ValueRO");
            MapProperty(builder, namedResource, "Value", "ValueRO");
            // Multi<T>.First() → GetFirst(), Last() → GetLast(), this[int] → Get(int).
            MapMethods(builder, multi, "First", "GetFirst");
            MapMethods(builder, multi, "Last", "GetLast");
            MapIndexer(builder, multi, "Get");
            // MultiComponentsIterator<T>.Current → CurrentRO.
            MapProperty(builder, multiIterator, "Current", "CurrentRO");
            return builder.ToImmutable();
        }

        private static void MapMethods(ImmutableDictionary<ISymbol, string>.Builder builder, INamedTypeSymbol type, string name, string siblingName) {
            if (type is null) return;
            foreach (var member in type.GetMembers(name).OfType<IMethodSymbol>()) {
                builder[member.OriginalDefinition] = siblingName;
            }
        }

        private static void MapProperty(ImmutableDictionary<ISymbol, string>.Builder builder, INamedTypeSymbol type, string name, string siblingName) {
            if (type is null) return;
            foreach (var member in type.GetMembers(name).OfType<IPropertySymbol>()) {
                builder[member.OriginalDefinition] = siblingName;
            }
        }

        private static void MapIndexer(ImmutableDictionary<ISymbol, string>.Builder builder, INamedTypeSymbol type, string siblingName) {
            if (type is null) return;
            foreach (var member in type.GetMembers().OfType<IPropertySymbol>()) {
                if (member.IsIndexer) builder[member.OriginalDefinition] = siblingName;
            }
        }

        private static void AddAllOverloads(ImmutableHashSet<ISymbol>.Builder builder, INamedTypeSymbol type, string name) {
            if (type is null) return;
            foreach (var member in type.GetMembers(name).OfType<IMethodSymbol>()) {
                // OriginalDefinition strips substituted generics; we always compare against this.
                builder.Add(member.OriginalDefinition);
            }
        }

        private static void AddProperty(ImmutableHashSet<ISymbol>.Builder builder, INamedTypeSymbol type, string name) {
            if (type is null) return;
            foreach (var member in type.GetMembers(name).OfType<IPropertySymbol>()) {
                builder.Add(member.OriginalDefinition);
            }
        }

        private static void AddIndexer(ImmutableHashSet<ISymbol>.Builder builder, INamedTypeSymbol type) {
            if (type is null) return;
            foreach (var member in type.GetMembers().OfType<IPropertySymbol>()) {
                if (member.IsIndexer) builder.Add(member.OriginalDefinition);
            }
        }
    }
}

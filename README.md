<p align="center">
  <a href="./README.md"><img src="https://img.shields.io/badge/EN-English-blue?style=flat-square" alt="English"></a>
  <a href="./README_RU.md"><img src="https://img.shields.io/badge/RU-Русский-blue?style=flat-square" alt="Русский"></a>
  <a href="./README_ZH.md"><img src="https://img.shields.io/badge/ZH-中文-blue?style=flat-square" alt="中文"></a>
  <br><br>
  <img src="https://img.shields.io/badge/version-1.0.1-blue?style=for-the-badge" alt="Version">
  <a href="https://www.nuget.org/packages/FFS.StaticEcs.Analyzers/"><img src="https://img.shields.io/badge/NuGet-FFS.StaticEcs.Analyzers-004880?style=for-the-badge&logo=nuget" alt="NuGet"></a>
  <a href="https://github.com/Felid-Force-Studios/StaticEcs"><img src="https://img.shields.io/badge/StaticEcs-framework-orange?style=for-the-badge" alt="StaticEcs"></a>
</p>

# Static ECS Analyzer — Roslyn diagnostics & code-fixes for FFS.StaticEcs

Roslyn analyzer + code-fix suite that catches common misuses of the [StaticEcs](https://github.com/Felid-Force-Studios/StaticEcs) framework at compile time.
The package is fully self-contained — `FFS.StaticEcs` itself does **not** bundle these analyzers; you add this package explicitly.

## Installation

### NuGet
```
dotnet add package FFS.StaticEcs.Analyzers
```

### Unity (UPM)
Via git URL in Unity PackageManager:
```
https://github.com/Felid-Force-Studios/StaticEcs-Analyzer.git
```
Or adding to the manifest `Packages/manifest.json`:
```json
"com.felid-force-studios.static-ecs-analyzer": "https://github.com/Felid-Force-Studios/StaticEcs-Analyzer.git"
```

The analyzer auto-disables itself when the compilation does not reference `FFS.StaticEcs`, so it is safe to keep in any solution.

Diagnostic categories:

- **`FFS.StaticEcs.Correctness`** — code that compiles but is semantically wrong (silent copies of ref-returns, use-after-free of entities, contradictory query filters, …).
- **`FFS.StaticEcs.Performance`** — patterns that allocate or block runtime optimizations (closure-capturing lambdas in `Query.For`).
- **`FFS.StaticEcs.Usage`** — style/clarity suggestions (more direct API available).

___

## Rule index

| ID | Category | Severity | Title | CodeFix |
|---|---|---|---|---|
| [FFSECS0010](#ffsecs0010) | Correctness | Error | Ref-returning member result must be bound by 'ref' | yes |
| [FFSECS0011](#ffsecs0011) | Correctness | Info | `Read<T>()` result is bound to a copy | yes |
| [FFSECS0012](#ffsecs0012) | Correctness | Info | Ref-local backed by StaticEcs storage passed by value (atomically-valued types skipped) | yes |
| [FFSECS0013](#ffsecs0013) | Correctness | Info | Writable ref obtained from a ref-returning member is used only for reading; suggest the read-only sibling | yes |
| [FFSECS0020](#ffsecs0020) | Correctness | Error | StaticEcs marker interface must be implemented by a struct | yes |
| [FFSECS0021](#ffsecs0021) | Correctness | Error | `IMultiComponent` must be implemented by a struct | yes |
| [FFSECS0022](#ffsecs0022) | Correctness | Warning | Non-unmanaged `IMultiComponent` must override `Write`/`Read` | — |
| [FFSECS0030](#ffsecs0030) | Correctness | Info | Query.For lambda parameter declared `ref` but never mutated | yes |
| [FFSECS0031](#ffsecs0031) | Performance | Error | Lambda in `Query.For` captures outer state | — |
| [FFSECS0032](#ffsecs0032) | Usage | Info | `IsMatch<TFilter>()` can be replaced with a direct Entity method | yes |
| [FFSECS0033](#ffsecs0033) | Usage | Info | `foreach` over `Entities()` convertible to `Query.For(...)` | yes |
| [FFSECS0040](#ffsecs0040) | Correctness | Error | `ref`/`in` reference to a component used after invalidation | — |
| [FFSECS0041](#ffsecs0041) | Correctness | Error | Entity used after invalidation | — |
| [FFSECS0042](#ffsecs0042) | Correctness | Warning | `Ref`/`Mut`/`Read<T>` called without a visible presence guarantee | yes |
| [FFSECS0050](#ffsecs0050) | Correctness | Error | Redundant component in query filter | — |
| [FFSECS0051](#ffsecs0051) | Correctness | Error | Contradictory `All` + `None` in query filter | — |

___

## Rules

### FFSECS0010
**Category:** Correctness · **Severity:** Error · **CodeFix:** yes

`Entity.Ref/Mut/Add`, `Components<T>.Ref/Mut/Add`, `Resource<T>.Value`, `NamedResource<T>.Value`, `Multi<T>.First/Last/[i]`, `MultiComponentsIterator<T>.Current` all return by reference. Binding the result to a non-ref local silently copies the component — any mutation goes to the copy, not the storage. Detected in variable declarations, value-arguments, simple assignments and non-ref return statements.

Reference-typed payloads (e.g. `Resource<MyClass>.Value`) are suppressed: copying a reference is cheap and idiomatic. The same applies when the **outer** value of the chain is atomically copyable — a primitive, enum, `IntPtr`/`UIntPtr`, or reference type — e.g. `entity.Ref<C>().PrimitiveField`. A copy of such a value is losslessly equivalent to a ref-binding, so the diagnostic is suppressed.

#### Pattern (will fire)
```csharp
var pos = entity.Ref<Position>();           // FFSECS0010 — silent copy
Consume(entity.Ref<Position>());            // FFSECS0010 — copied at call boundary
return entity.Ref<Position>();              // FFSECS0010 — copied at return
```

#### Fix (no diagnostic)
```csharp
ref var pos = ref entity.Ref<Position>();   // ok — ref-bound
entity.Ref<Position>().Value = 5;           // ok — direct write through ref
Consume(ref entity.Ref<Position>());        // ok — passed by ref
```

#### Explicit opt-in to a copy: `*RO` siblings
When you genuinely want a snapshot (a copy) from `Resource<T>` / `NamedResource<T>` / `Multi<T>` / `MultiComponentsIterator<T>`, use the dedicated `*RO` members instead of binding the mutable ref-return to a plain local. These return `ref readonly T` and are intentionally **not** in the analyzer's allow-list — the `RO` suffix communicates the intent in the source.

| Mutable (flagged) | Read-only sibling |
|---|---|
| `Resource<T>.Value` | `Resource<T>.ValueRO` |
| `NamedResource<T>.Value` | `NamedResource<T>.ValueRO` |
| `Multi<T>.First()` | `Multi<T>.GetFirst()` |
| `Multi<T>.Last()` | `Multi<T>.GetLast()` |
| `Multi<T>[idx]` | `Multi<T>.Get(idx)` |
| `MultiComponentsIterator<T>.Current` | `MultiComponentsIterator<T>.CurrentRO` |

```csharp
var snapshot = timer.ValueRO;               // ok — explicit RO opt-in, no diagnostic
ref readonly var refSnap = ref multi.GetFirst();
```

For `Entity` and `Components<T>` the snapshot path is `Read<T>()` / `Read(Entity)` — paired with FFSECS0011 (Info hint).

The codefix offers a one-click «Switch to '`*RO`' (intentional copy)» action.

For `Entity.Ref<T>` / `Components<T>.Ref/Mut` on a `var`-declaration the codefix also offers «Switch to `Read<T>()`». The exact set of variants depends on the payload size:

- **T ≤ 8 bytes** (per the FFSECS0011 size-based suppression): two actions are offered — first `var x = entity.Read<T>();` (plain copy snapshot, recommended), then `ref readonly var x = ref entity.Read<T>();` (bind-by-`ref readonly`, in case the user wants the binding explicitly).
- **T > 8 bytes or unknown**: only the bind-by-`ref readonly` variant is offered — a copy of a larger struct would be a pessimization.

___

### FFSECS0011
**Category:** Correctness · **Severity:** Info · **CodeFix:** yes

`Entity.Read<T>()` and `Components<T>.Read(Entity)` return `ref readonly T`. Binding to a non-ref-readonly local copies the value — undesirable for large components. Surfaced as an IDE hint; silence per-project with `dotnet_diagnostic.FFSECS0011.severity = none`.

Like FFSECS0010, the rule is suppressed when the outer value of the chain is atomically copyable — primitive, enum, `IntPtr`/`UIntPtr`, or reference type. For example `entity.Read<C>().PrimitiveField` does **not** fire: the call already produces a value that survives the copy losslessly.

The rule is **also** suppressed when the `Read<T>` payload is a value type with a conservatively-estimated size of ≤ 8 bytes — e.g. `struct Id { uint Value; }`, `readonly struct WorldEntityMask { uint Mask; }`, `struct Pair { uint A; uint B; }`. A ≤ 8-byte copy fits in a single register on x64/ARM64 ABIs, so binding to `ref readonly var` brings no measurable win and `readonly`-ness of the struct doesn't matter. Estimation is conservative: open generics, explicit struct layout, pointer/function-pointer/fixed-buffer fields → no suppression.

#### Pattern (will fire)
```csharp
var snapshot = entity.Read<Position>();     // FFSECS0011 — copied
```

#### Fix (no diagnostic)
```csharp
ref readonly var snap = ref entity.Read<Position>();
Consume(in entity.Read<Position>());        // ok — passed by 'in'
```

___

### FFSECS0012
**Category:** Correctness · **Severity:** Info · **CodeFix:** yes

A `ref` / `ref readonly` local bound to a StaticEcs storage source was passed by value. This copies the component at the call boundary — the callee mutates the copy. The hint is heuristic: the analyzer can't tell an accidental loss of ref semantics from an intentional pass-the-current-value, so it surfaces as Info; to silence globally use `dotnet_diagnostic.FFSECS0012.severity = none` in `.editorconfig`.

Atomically-valued types are excluded automatically — they have no internal state that could be lost via a copy: CLR primitives (`bool`/`int`/`float`/...), enums, and reference types (the local holds a pointer; copying the pointer still hits the same heap object).

`ref readonly` locals are also untracked: the user already opted into a readonly snapshot — there's no writable ref semantics to lose. This covers e.g. extension-method invocations on `ref readonly` locals (`entityType.Name()` inside string interpolation, etc.) which Roslyn lowers to a value-typed `this` argument.

#### Pattern (will fire)
```csharp
ref var hp = ref entity.Ref<Health>();      // Health — multi-field struct
Consume(hp);                                // FFSECS0012 — copied at the call
```

#### Fix
```csharp
Consume(ref hp);                            // ok
Consume(in hp);                             // ok — 'in' accepts a ref local
ref var id = ref entity.Add<PlayerId>().Value;  // .Value — ushort, atomic
SetBehaviour(id);                           // ok — primitive isn't tracked
ref var st = ref entity.Ref<C>().Status;    // Status — enum
M(st);                                      // ok — enum isn't tracked
```

___

### FFSECS0013
**Category:** Correctness · **Severity:** Info · **CodeFix:** yes

A writable ref obtained from a StaticEcs ref-returning member is used only for reading. Two shapes are detected:

1. **Ref-local binding** — `ref var x = ref entity.Ref<T>()` (or `.Mut<T>()` / a Multi/Resource/Iterator ref-member) whose body never writes through `x`, never passes it by `ref`/`out`, never takes a writable ref-alias from it, and never invokes a non-readonly instance method on it.
2. **Inline read** — `entity.Ref<T>().Field` / `world.Resource<T>().Value.X` / `multi.First().Field` / `multi[i].Y` etc., where the result flows into a read-only consumer (var initializer, field/property access, `in`/by-value argument, return from a non-ref method).

In both cases the writable ref is unused, and `Mut<T>` additionally marks the component as **changed** for nothing. The hint suggests the matching read-only sibling. Severity Info: silence via `dotnet_diagnostic.FFSECS0013.severity = none` in `.editorconfig`.

**Read-only siblings:**

| Writable member | Read-only sibling |
|---|---|
| `Entity.Ref<T>()` / `Entity.Mut<T>()` | `Entity.Read<T>()` |
| `Components<T>.Ref(Entity)` / `Mut(Entity)` | `Components<T>.Read(Entity)` |
| `Resource<T>.Value`, `NamedResource<T>.Value` | `ValueRO` |
| `Multi<T>.First()` | `GetFirst()` |
| `Multi<T>.Last()` | `GetLast()` |
| `Multi<T>[int]` | `Multi<T>.Get(int)` |
| `MultiComponentsIterator<T>.Current` | `CurrentRO` |

`Entity.Add<T>()` / `Components<T>.Add(...)` have no read-only sibling and never trigger the rule.

"Mutation" is defined conservatively: direct writes, compound assignments, `++`/`--`, pass-by-`ref`/`out`, taking a writable `ref` alias (`ref var alias = ref local.Field`), and instance-method calls on a non-readonly struct all count. By-value reads, `in`-passing, and `ref readonly` alias creation are not mutations.

#### Pattern (will fire)
```csharp
// Ref-local binding form.
ref var d = ref entity.Ref<Attack>();                            // FFSECS0013 — only reads below
Console.WriteLine(d.Delay);
Process(d.AttackerId);                                            // by-value pass — not a mutation

// Inline form — outer is a reference type.
var transform = entity.Ref<GameObjectRef>().Val.transform;        // FFSECS0013 → Read<GameObjectRef>()

// Inline form — outer is a primitive.
var x = entity.Ref<Position>().X;                                 // FFSECS0013

// Inline form — chain through a non-ref property.
var y = entity.Ref<Big>().SomeProperty.Field;                     // FFSECS0013

// Multi / Resource inline forms.
var first = multi.First().Field;                                  // FFSECS0013 → GetFirst()
var cfgN  = world.Resource<Cfg>().Value.SomeInt;                  // FFSECS0013 → ValueRO
```

#### Fix (no diagnostic)
```csharp
// Ref-local: bind by 'ref readonly' (default).
ref readonly var d = ref entity.Read<Attack>();

// For payload ≤ 8 bytes — also offered as a copy snapshot.
var d = entity.Read<Attack>();

// Inline.
var transform = entity.Read<GameObjectRef>().Val.transform;
var first     = multi.GetFirst().Field;
var cfgN      = world.Resource<Cfg>().ValueRO.SomeInt;
```

#### Will NOT fire
```csharp
// Direct write through the ref-local.
ref var d = ref entity.Ref<Attack>();
d.Delay = 0;

// Direct write through inline ref.
entity.Ref<Position>().X = 5;

// Writable ref-alias on a field.
ref var t = ref entity.Ref<Transform>();
ref var x = ref t.Position.X;
x = 1f;

// Non-readonly method call on a non-readonly struct — conservatively counted as mutation.
ref var s = ref entity.Ref<StateMachine>();
s.Advance();
entity.Ref<StateMachine>().Advance();   // also silent — inline form, same rule

// Ref/out argument.
Method(ref entity.Ref<Position>().X);

// Add has no read-only sibling and is never flagged.
entity.Add<Tag>().Value = 1;
```

___

### FFSECS0020
**Category:** Correctness · **Severity:** Error · **CodeFix:** yes

A class implementing any StaticEcs marker interface (`IComponent`, `ITag`, `IEvent`, `ILinkType`, `ILinksType`, `IEntityType`, `IWorldType`) breaks generic dispatch — every public API in StaticEcs has a `where T : struct` constraint, and reflection-based `RegisterAll` would skip class types.

#### Pattern (will fire)
```csharp
public class Health : IComponent { public int Value; }   // FFSECS0020
```

#### Fix
```csharp
public struct Health : IComponent { public int Value; }  // ok
```

___

### FFSECS0021
**Category:** Correctness · **Severity:** Error · **CodeFix:** yes

Same as FFSECS0020 but specifically for `IMultiComponent`.

___

### FFSECS0022
**Category:** Correctness · **Severity:** Warning · **CodeFix:** —

A struct implementing `IMultiComponent` that is **not** `unmanaged` (contains managed fields like `string`, arrays, delegates, …) must override both `Write(ref BinaryPackWriter)` and `Read(ref BinaryPackReader)`. The interface's default implementations are no-ops — without overrides, snapshots silently produce empty data for the managed payload.

#### Pattern (will fire)
```csharp
public struct Inventory : IMultiComponent { public string Owner; }  // FFSECS0022 — no overrides
```

#### Fix
```csharp
public struct Inventory : IMultiComponent {
    public string Owner;
    public void Write(ref BinaryPackWriter w) { w.WriteString(Owner); }
    public void Read(ref BinaryPackReader r) { Owner = r.ReadString(); }
}
```

Unmanaged structs (`int`/`float`/`Nullable<int>` etc.) are bulk-copied by the storage and don't need overrides.

___

### FFSECS0030
**Category:** Correctness · **Severity:** Info · **CodeFix:** yes

A `ref T` parameter of a `Query.For` lambda that is never written marks the component as **changed** at runtime whenever change-tracking is enabled — even though the body only reads it. Use the `in T` overload to signal read-only intent and skip the change mark.

#### Pattern (will fire)
```csharp
W.Query().For((ref Health h) => { Console.WriteLine(h.Value); }); // FFSECS0030
```

#### Fix
```csharp
W.Query().For((in Health h) => { Console.WriteLine(h.Value); });
```

___

### FFSECS0031
**Category:** Performance · **Severity:** Error · **CodeFix:** —

A lambda passed to `Query.For` (or any fluent builder's `.For(...)`) that captures outer state (`this`, a method-local variable, an instance field/property/method) allocates a closure every time the query runs. Use one of the alternatives:

- `static` lambda + the `userData` overload (`For<TData>(userData, static (ref TData d, …) => …)`).
- A `struct` implementing `W.IQuery.Write<…>` / `W.IQuery.Read<…>`.
- `foreach (var entity in W.Query<…>().Entities())` + `ref var` locals.

#### Pattern (will fire)
```csharp
var multiplier = 2;
W.Query().For((ref Health h) => { h.Value *= multiplier; });    // FFSECS0031
```

#### Fix
```csharp
var multiplier = 2;
W.Query().For(multiplier, static (ref int m, ref Health h) => { h.Value *= m; });
```

Method-group references to non-static instance methods are also flagged (they capture `this`).

___

### FFSECS0032
**Category:** Usage · **Severity:** Info · **CodeFix:** yes

`Entity.IsMatch<TFilter>()` works for any `IQueryFilter`, but for simple shapes (`All<…>`, `Any<…>`, `None<…>`, their `*WithDisabled`/`*OnlyDisabled` siblings, `EntityIs<…>`, `EntityIsAny<…>`, `EntityIsNot<…>`) Entity has a shorter, intent-revealing direct method:

| Filter | Equivalent |
|---|---|
| `All<T..>` (arity 1-3) | `HasEnabled<T..>()` |
| `AllWithDisabled<T..>` | `Has<T..>()` |
| `AllOnlyDisabled<T..>` | `HasDisabled<T..>()` |
| `Any<T..>` (arity 2-3) | `HasEnabledAny<T..>()` |
| `AnyWithDisabled<T..>` | `HasAny<T..>()` |
| `AnyOnlyDisabled<T..>` | `HasDisabledAny<T..>()` |
| `None<T..>` | `!HasEnabled<…>` / `!HasEnabledAny<…>` |
| `NoneWithDisabled<T..>` | `!Has<…>` / `!HasAny<…>` |
| `EntityIs<T>` | `Is<T>()` |
| `EntityIsAny<T..>` | `IsAny<T..>()` |
| `EntityIsNot<T..>` | `IsNot<T..>()` |

#### Pattern (will fire)
```csharp
if (entity.IsMatch<All<Health, Mana>>())  { … }   // FFSECS0032
if (entity.IsMatch<None<Stunned>>())      { … }   // FFSECS0032
```

#### Fix
```csharp
if (entity.HasEnabled<Health, Mana>()) { … }
if (!entity.HasEnabled<Stunned>())     { … }
```

Constraint check: `HasEnabled`/`HasEnabledAny`/`HasDisabled`/`HasDisabledAny` all require `T : struct, IComponent, IDisableable`. For `All<…>`, `Any<…>`, `None<…>` (which accept `IComponentOrTag` — tags allowed) the diagnostic only fires when every type argument is both `IComponent` and `IDisableable`; otherwise the naive replacement would not compile and is silently skipped. `*OnlyDisabled` filters already carry the same constraints, so the check is automatic for them.

Composite filters (`And<…>`, `Or<…>`, `Nothing`) and arity > 3 are not suggested — `IsMatch` is the only practical entry point there.

___

### FFSECS0033
**Category:** Usage · **Severity:** Info · **CodeFix:** yes

The pattern `foreach (var entity in W.Query<…>().Entities()) { ref var x = ref entity.Ref<T>(); … }` has a more compact, intent-revealing form via `W.Query<…>().For((ref T x, …) => { … })` — the components touched through `entity.Ref<T>()`/`Mut<T>()`/`Read<T>()` move into the lambda parameter list and are simultaneously removed from any `All<…>` they were listed in, since `For` adds them back to the filter implicitly.

How the codefix rewrites the body:

- Components reached via `entity.Ref<T>()`/`Mut<T>()` for a `T` listed in some `All<…>` become `ref T` lambda parameters; `entity.Read<T>()` becomes `in T`. The matching `ref var X = ref entity.Ref<T>();` declarations are deleted.
- Each absorbed `T` is removed from its `All<…>`. Empty `All<…>` nodes collapse out of any enclosing `And<…>`; if `And<…>` ends up with a single argument it is unwrapped; the top-level filter disappears entirely when nothing remains.
- All other filters (`None<…>`, `Any<…>`, `EntityIs<…>`, …) and `All<…>` components that the body does not touch are preserved verbatim.
- If the body uses `entity` for anything other than absorbing components from `All<…>` (e.g. `entity.Has<Tag>()`, `entity.Destroy()`, or `entity.Ref<U>()` where `U` is not in `All<…>`), the lambda gets an `Entity entity` parameter and those calls stay in place.
- If the body references one outer local/parameter, the codefix uses the `For<TData>(ref data, static (ref TData data, …) => …)` overload and the lambda becomes `static` — no closure allocation.

Skipped (the diagnostic does NOT fire) when any of the following hold:

- The body contains `break`, `continue`, `return`, `yield`, `goto`, `throw`, `await`, a nested anonymous function, or a nested local function — these cannot be preserved one-to-one inside a lambda body.
- The body captures `this`, an instance field, or two or more distinct outer locals/parameters — the rewrite would need a multi-field UserData struct, which the codefix does not synthesize.
- The total absorbed component count exceeds 6 — `For` overloads only go up to `T0..T5`.
- No `entity.Ref/Mut/Read<T>()` call inside the body touches a `T` listed in `All<…>` — there is nothing to absorb and the rewrite would be pure churn.
- The filter shape uses constructs the codefix cannot safely modify (e.g. `Or<…>` at the top level) — only `All<…>`/`And<…>`/`None<…>`/`Any<…>` compositions are supported in V1.

#### Pattern (will fire)
```csharp
foreach (var entity in W.Query<All<NeedsData>>().Entities()) {
    ref var needs = ref entity.Ref<NeedsData>();
    needs.Hunger++;
    needs.Thirst++;
    needs.Tired++;
}
```

#### Fix
```csharp
W.Query().For((ref NeedsData needs) => {
    needs.Hunger++;
    needs.Thirst++;
    needs.Tired++;
});
```

___

### FFSECS0040
**Category:** Correctness · **Severity:** Error · **CodeFix:** —

`ref`/`in` references to a component become stale after the underlying entity is invalidated. Three patterns are tracked:

- **Lambda in `WorldQuery.For`** — the references are the lambda's `ref`/`in` component parameters.
- **`struct` implementing `IQuery.*`** — the references are the `ref`/`in` parameters of the `Invoke` method.
- **`ref`-locals from `entity.Ref/Mut/Read/Add(...)`**.

Invalidators: `Destroy`, `MoveTo`, `Unload` (full kill), `Delete<T>` (only references to a component of type `T`).

#### Pattern (will fire)
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    e.Destroy();
    hp.Value = 0;                       // FFSECS0040 — hp points into freed storage
});
```

#### Fix
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    var snap = hp.Value;                // copy first
    e.Destroy();
    Use(snap);                          // ok
});
```

___

### FFSECS0041
**Category:** Correctness · **Severity:** Error · **CodeFix:** —

Counterpart to FFSECS0040 but tracks the **entity variable itself**, not the `ref`/`in` references to its components. After `Destroy`/`MoveTo`/`Unload` on a local or parameter, any further operation on that variable (`Has`, `Add`, `IsActual`, …) is flagged. The only allowed operations on the tainted variable are:

- Direct reassignment (`entity = W.NewEntity<…>();`).
- Out-parameter rebind (`Method(out entity);` or `Method(out var entity)` inside a loop).

#### Pattern (will fire)
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
_ = e.Has<Health>();                    // FFSECS0041
```

#### Fix
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
e = W.NewEntity<Default>();             // reassignment kills the taint
_ = e.Has<Health>();                    // ok
```

The merge across conditional branches is conservative — if any predecessor path leaves the variable invalidated, the merge point is tainted.

___

### FFSECS0042
**Category:** Correctness · **Severity:** Warning · **CodeFix:** yes

`Entity.Ref<T>()`, `Entity.Mut<T>()`, `Entity.Read<T>()` require `T` to be present on the entity — otherwise debug builds assert and release builds return data of an unrelated component. The analyzer runs a forward dataflow over the method/lambda CFG and emits a warning at every call site where the receiver entity is **not statically proven** to carry `T` on every incoming path.

A guarantee for `T` on `entity` is established by:

- The true branch of a previous `entity.Has<T...>()`, `HasEnabled<T...>()`, `HasDisabled<T...>()` (any arity — each generic argument is added).
- The true branch of a previous `entity.IsMatch<F>()` where `F` reduces (through nested `And<…>`) to `All<T>` / `AllOnlyDisabled<T>` / `AllWithDisabled<T>`. `None`/`Any`/`EntityIs*` filters add nothing.
- The body of a `Query<TFilter>().For(...)` lambda — every component listed in `TFilter`'s `All*` is guaranteed for the lambda's `Entity` parameter, plus every `ref T` / `in T` component parameter declared in the lambda signature.
- The body of an `IQuery<...>.Invoke` method — every `ref T` / `in T` component parameter is guaranteed for the `Entity` parameter. `TFilter` is not visible at this layer, so only signature-derived components count.
- A previous `entity.Add<T>(...)`, `Set<T>(...)`, `Ref<T>()`, `Mut<T>()`, `Read<T>()` on the same local/parameter, without an intervening invalidator.

Invalidators clear guarantees:

- `entity.Delete<T>()` removes only `T`.
- `entity.Destroy()` / `MoveTo(…)` / `Unload(…)` remove every guarantee on that entity.
- Re-assigning the entity variable (`entity = …;`) or passing it as a `ref`/`out` argument clears every guarantee on the local/parameter.

The analyser only tracks entities that resolve to a single `ILocalSymbol` or `IParameterSymbol`. Chained, property/field, or `default(Entity)` receivers cannot have a `Has` guard attach to them, and are reported unconditionally.

#### Pattern (will fire)
```csharp
ref var pos = ref entity.Ref<Position>();                                              // FFSECS0042 — no proof Position is present
W.Query<None<Stunned>>().For((W.Entity e) => { e.Ref<Position>(); });                  // FFSECS0042 — filter does not contain All<Position>
if (entity.Has<Velocity>()) { entity.Ref<Position>(); }                                // FFSECS0042 — guard is for Velocity, not Position
entity.Delete<Position>();
ref var lost = ref entity.Ref<Position>();                                             // FFSECS0042 — guarantee was cleared by Delete<Position>
```

#### Fix
```csharp
if (entity.Has<Position>()) {
    ref var pos = ref entity.Ref<Position>();                                          // ok — true-branch guarantees Position
}

if (!entity.Has<Position>()) return;
ref var pos2 = ref entity.Ref<Position>();                                             // ok — guarded by early-return

entity.Add<Position>();
ref var pos3 = ref entity.Ref<Position>();                                             // ok — Add establishes the guarantee

W.Query<All<Position, Velocity>>()
    .For((W.Entity e, ref Position p) => { ref var velocity = ref e.Ref<Velocity>(); });  // ok — both via All<…>
```

#### Per-call escape hatch: postfix `!`
```csharp
ref var pos = ref entity.Ref<Position>()!;                                             // ok — `!` suppresses FFSECS0042 here
entity.Mut<Position>()!.X = 5;                                                         // ok — works for Mut/Read too
```
The postfix null-forgiving operator (`!`) after a `Ref`/`Mut`/`Read` invocation silences the diagnostic for that single call. C# preserves the value/ref category through `!`, so the suppressed call still returns by reference and can be bound with `ref var`. The `!` token is accepted regardless of project-level nullable settings. After the suppressed call the dataflow records the guarantee for `T`, so subsequent uses on the same entity for the same component don't need the marker repeated. Suppression on the receiver (`entity!.Ref<T>()`) is **not** recognised — the marker must annotate the specific component access. The bundled CodeFix offers a one-click "Suppress FFSECS0042 with '!' after the call".

#### Limitations
- Boolean guards through intermediate locals (`var ok = entity.Has<Position>(); if (ok) …`) are not propagated.
- Components reached via `Components<T>.Ref/Mut/Read(entity)` overloads are not the rule's check points in V1; the `entity.X<T>()` instance form is.
- Cross-method analysis is out of scope: a helper `void Use(W.Entity e) => e.Ref<T>();` will warn unless guarded inside `Use`.

___

### FFSECS0050
**Category:** Correctness · **Severity:** Error · **CodeFix:** —

A component is referenced more than once inside the same query — either as a duplicate inside same-kind filters (`All`+`All`, `None`+`None`, `Any`+`Any`, including their `*WithDisabled`/`*OnlyDisabled` variants), or as an overlap between the filter chain and a lambda `ref`/`in` parameter, or with an `IQuery` struct's component generic.

#### Pattern (will fire)
```csharp
foreach (var _ in W.Query<All<Health>, All<Health>>().Entities()) { }                       // FFSECS0050
W.Query<All<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0050 — filter ↔ lambda
W.Query<All<Health>>().Write<Health>().For<MyWriteFn>();                                    // FFSECS0050 — filter ↔ IQuery generic
foreach (var _ in W.Query<All<Health>, AllOnlyDisabled<Health>>().Entities()) { }           // FFSECS0050 — base + disabled variant
```

___

### FFSECS0051
**Category:** Correctness · **Severity:** Error · **CodeFix:** —

The query has the same component in both an `All<…>` and a `None<…>` — the resulting set is always empty. The implicit `All` contribution of lambda parameters and `IQuery` struct generics also counts.

#### Pattern (will fire)
```csharp
foreach (var _ in W.Query<All<Health>, None<Health>>().Entities()) { }                       // FFSECS0051
W.Query<None<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0051 — lambda implies All
```

___

## Suppressing diagnostics

Per-line / per-block:
```csharp
#pragma warning disable FFSECS0011
var snap = entity.Read<Health>();
#pragma warning restore FFSECS0011
```

Per-project (`.editorconfig`):
```ini
[*.cs]
dotnet_diagnostic.FFSECS0011.severity = none
```

Per-build (`csproj`):
```xml
<NoWarn>FFSECS0011</NoWarn>
```

___

## Source code

All analyzers live under `StaticEcs/Analyzers~/Src/Analyzers/*.cs`; code fixes under `StaticEcs/Analyzers~/CodeFixes/`. Rule IDs are centralised in `StaticEcs/Analyzers~/Shared/FFSECSIds.cs`.

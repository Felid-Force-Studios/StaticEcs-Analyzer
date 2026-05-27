<p align="center">
  <a href="./README.md"><img src="https://img.shields.io/badge/EN-English-blue?style=flat-square" alt="English"></a>
  <a href="./README_RU.md"><img src="https://img.shields.io/badge/RU-Русский-blue?style=flat-square" alt="Русский"></a>
  <a href="./README_ZH.md"><img src="https://img.shields.io/badge/ZH-中文-blue?style=flat-square" alt="中文"></a>
  <br><br>
  <img src="https://img.shields.io/badge/version-1.0.0-blue?style=for-the-badge" alt="Version">
  <a href="https://www.nuget.org/packages/FFS.StaticEcs.Analyzers/"><img src="https://img.shields.io/badge/NuGet-FFS.StaticEcs.Analyzers-004880?style=for-the-badge&logo=nuget" alt="NuGet"></a>
  <a href="https://github.com/Felid-Force-Studios/StaticEcs"><img src="https://img.shields.io/badge/StaticEcs-framework-orange?style=for-the-badge" alt="StaticEcs"></a>
</p>

# Static ECS Analyzer —— FFS.StaticEcs 的 Roslyn 诊断与代码修复

针对 [StaticEcs](https://github.com/Felid-Force-Studios/StaticEcs) 框架常见误用的 Roslyn 分析器 + 代码修复套件，在编译期发现问题。
本包独立发布 —— `FFS.StaticEcs` 主包 **不再** 内置这些分析器，需要单独引入。

## 安装

### NuGet
```
dotnet add package FFS.StaticEcs.Analyzers
```

### Unity (UPM)
通过 Unity PackageManager 的 git URL：
```
https://github.com/Felid-Force-Studios/StaticEcs-Analyzer.git
```
或加入 `Packages/manifest.json`：
```json
"com.felid-force-studios.static-ecs-analyzer": "https://github.com/Felid-Force-Studios/StaticEcs-Analyzer.git"
```

如果当前编译没有引用 `FFS.StaticEcs`，分析器会自行禁用，因此在任何 solution 中保留它都是安全的。

诊断类别：

- **`FFS.StaticEcs.Correctness`** —— 代码可以编译但语义错误（ref 返回的隐式拷贝、对实体的 use-after-free、查询过滤器矛盾等）。
- **`FFS.StaticEcs.Performance`** —— 触发分配或阻碍运行时优化的模式（`Query.For` 中带闭包的 lambda）。
- **`FFS.StaticEcs.Usage`** —— 风格 / 可读性建议（存在更直接的 API）。

___

## 规则索引

| ID | 类别 | 严重程度 | 标题 | CodeFix |
|---|---|---|---|---|
| [FFSECS0010](#ffsecs0010) | Correctness | Error | ref 返回结果必须以 'ref' 绑定 | 有 |
| [FFSECS0011](#ffsecs0011) | Correctness | Info | `Read<T>()` 结果被绑定为副本 | 有 |
| [FFSECS0012](#ffsecs0012) | Correctness | Info | 来自 StaticEcs 存储的 ref-local 按值传递（原子值类型自动跳过） | 有 |
| [FFSECS0013](#ffsecs0013) | Correctness | Info | 来自 ref-返回成员的可写引用仅被用于读取；建议改用对应的只读 sibling | 有 |
| [FFSECS0020](#ffsecs0020) | Correctness | Error | StaticEcs 标记接口必须由 struct 实现 | 有 |
| [FFSECS0021](#ffsecs0021) | Correctness | Error | `IMultiComponent` 必须由 struct 实现 | 有 |
| [FFSECS0022](#ffsecs0022) | Correctness | Warning | 非 unmanaged 的 `IMultiComponent` 必须重写 `Write`/`Read` | — |
| [FFSECS0030](#ffsecs0030) | Correctness | Info | `Query.For` lambda 的 `ref` 参数从未被写入 | 有 |
| [FFSECS0031](#ffsecs0031) | Performance | Error | `Query.For` 的 lambda 捕获外部状态 | — |
| [FFSECS0032](#ffsecs0032) | Usage | Info | `IsMatch<TFilter>()` 可替换为 Entity 上的直接方法 | 有 |
| [FFSECS0033](#ffsecs0033) | Usage | Info | 遍历 `Entities()` 的 `foreach` 可转换为 `Query.For(...)` | 有 |
| [FFSECS0040](#ffsecs0040) | Correctness | Error | 失效后仍使用对组件的 `ref`/`in` 引用 | — |
| [FFSECS0041](#ffsecs0041) | Correctness | Error | 失效后仍使用 Entity | — |
| [FFSECS0042](#ffsecs0042) | Correctness | Warning | 调用 `Ref`/`Mut`/`Read<T>` 时缺少 `T` 存在性的静态证明 | 是 |
| [FFSECS0050](#ffsecs0050) | Correctness | Error | 查询过滤器中存在冗余组件 | — |
| [FFSECS0051](#ffsecs0051) | Correctness | Error | 查询过滤器中 `All` 与 `None` 相互矛盾 | — |

___

## 规则详解

### FFSECS0010
**类别：** Correctness · **严重程度：** Error · **CodeFix：** 有

`Entity.Ref/Mut/Add`、`Components<T>.Ref/Mut/Add`、`Resource<T>.Value`、`NamedResource<T>.Value`、`Multi<T>.First/Last/[i]`、`MultiComponentsIterator<T>.Current` 全部按引用返回。把结果绑定到普通 local 会悄无声息地复制组件 —— 后续的修改写入副本而非存储。会在变量声明、值参数、简单赋值、非 ref 返回中触发。

引用类型负载（如 `Resource<MyClass>.Value`）会被静默放行：复制一个引用本身没问题。当链路的**最外层**值是原子可复制类型（基本类型、enum、`IntPtr`/`UIntPtr` 或引用类型），例如 `entity.Ref<C>().PrimitiveField`，同样会被放行：这种值的拷贝与 ref 绑定语义上完全等价。

#### 触发
```csharp
var pos = entity.Ref<Position>();           // FFSECS0010 —— 隐式拷贝
Consume(entity.Ref<Position>());            // FFSECS0010 —— 在调用边界处拷贝
return entity.Ref<Position>();              // FFSECS0010 —— 返回时拷贝
```

#### 修复
```csharp
ref var pos = ref entity.Ref<Position>();   // ok —— ref 绑定
entity.Ref<Position>().Value = 5;           // ok —— 直接通过 ref 写入
Consume(ref entity.Ref<Position>());        // ok —— 按 ref 传递
```

#### 显式选择拷贝：`*RO` 对等成员
当你确实希望从 `Resource<T>` / `NamedResource<T>` / `Multi<T>` / `MultiComponentsIterator<T>` 得到一个快照（拷贝）时，请使用专门的 `*RO` 成员，而不是把可变 ref 返回绑定到普通局部变量。它们返回 `ref readonly T`，并被**故意**排除在分析器的允许列表之外 —— `RO` 后缀在源代码中传达了意图。

| 可变（会被诊断） | 只读对等成员 |
|---|---|
| `Resource<T>.Value` | `Resource<T>.ValueRO` |
| `NamedResource<T>.Value` | `NamedResource<T>.ValueRO` |
| `Multi<T>.First()` | `Multi<T>.GetFirst()` |
| `Multi<T>.Last()` | `Multi<T>.GetLast()` |
| `Multi<T>[idx]` | `Multi<T>.Get(idx)` |
| `MultiComponentsIterator<T>.Current` | `MultiComponentsIterator<T>.CurrentRO` |

```csharp
var snapshot = timer.ValueRO;               // ok —— 显式 RO 选择，无诊断
ref readonly var refSnap = ref multi.GetFirst();
```

对于 `Entity` 与 `Components<T>`，快照路径是 `Read<T>()` / `Read(Entity)`（FFSECS0011 Info 已经提示这条路径）。

CodeFix 提供一键操作「Switch to '`*RO`' (intentional copy)」。

对于 `var` 声明上的 `Entity.Ref<T>` / `Components<T>.Ref/Mut`，CodeFix 还会提供「Switch to `Read<T>()`」。具体可选项依赖 payload 大小：

- **T ≤ 8 字节**（与 FFSECS0011 的大小放行阈值一致）：提供两个动作，依次为 `var x = entity.Read<T>();`（普通拷贝快照，推荐）和 `ref readonly var x = ref entity.Read<T>();`（按 `ref readonly` 绑定，供用户显式选择）。
- **T > 8 字节或大小未知**：只提供按 `ref readonly` 绑定的版本。对较大的 struct 而言拷贝是性能反模式，因此不提供拷贝选项。

___

### FFSECS0011
**类别：** Correctness · **严重程度：** Info · **CodeFix：** 有

`Entity.Read<T>()` 与 `Components<T>.Read(Entity)` 返回 `ref readonly T`。绑定到非 ref-readonly local 会复制 —— 对大型组件不可取。Severity 为 Info，仅是 IDE 提示，不会破坏构建。可通过 `.editorconfig` 中的 `dotnet_diagnostic.FFSECS0011.severity = none` 关闭。

与 FFSECS0010 一致：当链路最外层是原子可复制类型（基本类型、enum、`IntPtr`/`UIntPtr` 或引用类型）时，规则会被放行。例如 `entity.Read<C>().PrimitiveField` **不会**触发：返回值本身就是可无损复制的标量。

当 `Read<T>` 的 payload 是值类型且保守估算的大小 ≤ 8 字节时，规则**同样**被放行 —— 例如 `struct Id { uint Value; }`、`readonly struct WorldEntityMask { uint Mask; }`、`struct Pair { uint A; uint B; }`。≤ 8 字节的拷贝在 x64/ARM64 ABI 下放入单个寄存器，绑定到 `ref readonly var` 没有可测的收益，是否 `readonly struct` 并不影响判断。估算是保守的：开放泛型、`StructLayout(Explicit)`、指针/函数指针/fixed-buffer 字段 → 不放行。

#### 触发
```csharp
var snapshot = entity.Read<Position>();     // FFSECS0011 —— 复制
```

#### 修复
```csharp
ref readonly var snap = ref entity.Read<Position>();
Consume(in entity.Read<Position>());        // ok —— 按 'in' 传递
```

___

### FFSECS0012
**类别：** Correctness · **严重程度：** Info · **CodeFix：** 有

绑定到 StaticEcs 存储源的 `ref` / `ref readonly` local 以值方式传递。这会在调用边界处拷贝组件 —— 被调用者修改的是副本。该提示属于启发式：分析器无法区分意外丢失 ref 语义与有意传递当前值，因此以 Info 呈现；如需全局静默，在 `.editorconfig` 中设置 `dotnet_diagnostic.FFSECS0012.severity = none`。

原子值类型会自动排除 —— 它们没有可通过拷贝丢失的内部状态：CLR 原生类型 (`bool`/`int`/`float`/...)、`enum`、以及引用类型 (local 保存指针；拷贝指针仍命中同一个堆对象)。

`ref readonly` 局部变量同样不会被跟踪：用户已显式选择 readonly 快照，无法通过该 ref 修改 storage —— 不存在「可写 ref 语义」可丢失。这覆盖了在 `ref readonly` 局部上调用扩展方法的场景（例如字符串插值中的 `entityType.Name()`），Roslyn 会把 `this` 接收方降级为按值参数。

#### 触发
```csharp
ref var hp = ref entity.Ref<Health>();      // Health — 多字段 struct
Consume(hp);                                // FFSECS0012 —— 拷贝
```

#### 修复
```csharp
Consume(ref hp);                            // ok
Consume(in hp);                             // ok —— 'in' 接受 ref-local
ref var id = ref entity.Add<PlayerId>().Value;  // .Value 是 ushort，原子值
SetBehaviour(id);                           // ok —— 原生类型不会被跟踪
ref var st = ref entity.Ref<C>().Status;    // Status 是 enum
M(st);                                      // ok —— enum 不会被跟踪
```

___

### FFSECS0013
**类别：** Correctness · **严重程度：** Info · **CodeFix：** 有

来自 StaticEcs ref-返回成员的可写引用仅被用于读取。识别两种形态：

1. **Ref-local 绑定** —— `ref var x = ref entity.Ref<T>()`（或 `.Mut<T>()` / Multi/Resource/迭代器的 ref 成员），且 `x` 没有任何写入、没有按 `ref`/`out` 传递、没有创建可写 ref 别名、没有在其上调用非 readonly 实例方法。
2. **Inline 读取** —— `entity.Ref<T>().Field` / `world.Resource<T>().Value.X` / `multi.First().Field` / `multi[i].Y` 等等，结果流入只读消费者（`var` 初始化、字段/属性读取、`in`/按值参数、非 ref 方法的 return）。

两种情况下可写引用都是多余的；对 `Mut<T>` 来说还会**无谓**地把组件标记为已变更。提示给出对应的只读 sibling。严重程度 Info：可通过 `.editorconfig` 中的 `dotnet_diagnostic.FFSECS0013.severity = none` 关闭。

**只读 sibling 表：**

| 可写成员 | 只读 sibling |
|---|---|
| `Entity.Ref<T>()` / `Entity.Mut<T>()` | `Entity.Read<T>()` |
| `Components<T>.Ref(Entity)` / `Mut(Entity)` | `Components<T>.Read(Entity)` |
| `Resource<T>.Value`, `NamedResource<T>.Value` | `ValueRO` |
| `Multi<T>.First()` | `GetFirst()` |
| `Multi<T>.Last()` | `GetLast()` |
| `Multi<T>[int]` | `Multi<T>.Get(int)` |
| `MultiComponentsIterator<T>.Current` | `CurrentRO` |

`Entity.Add<T>()` / `Components<T>.Add(...)` 没有只读 sibling，规则不会对它们触发。

「修改」的判定保守：直接写入、复合赋值、`++`/`--`、按 `ref`/`out` 传递、获取可写 ref 别名 (`ref var alias = ref local.Field`)、在非 readonly struct 上调用实例方法，均视为修改。按值读取、`in` 传递、创建 `ref readonly` 别名不算修改。

#### 触发
```csharp
// Ref-local 形态。
ref var d = ref entity.Ref<Attack>();                              // FFSECS0013 —— 下面只有读取
Console.WriteLine(d.Delay);
Process(d.AttackerId);                                              // 按值传递 —— 不是修改

// Inline —— outer 为引用类型。
var transform = entity.Ref<GameObjectRef>().Val.transform;          // FFSECS0013 → Read<GameObjectRef>()

// Inline —— outer 为基元。
var x = entity.Ref<Position>().X;                                   // FFSECS0013

// Inline —— 链路经过非 ref property。
var y = entity.Ref<Big>().SomeProperty.Field;                       // FFSECS0013

// Multi / Resource inline。
var first = multi.First().Field;                                    // FFSECS0013 → GetFirst()
var cfgN  = world.Resource<Cfg>().Value.SomeInt;                    // FFSECS0013 → ValueRO
```

#### 修复（无诊断）
```csharp
// Ref-local：绑定为 'ref readonly'（默认）。
ref readonly var d = ref entity.Read<Attack>();

// payload ≤ 8 字节时还会额外提供 copy snapshot。
var d = entity.Read<Attack>();

// Inline。
var transform = entity.Read<GameObjectRef>().Val.transform;
var first     = multi.GetFirst().Field;
var cfgN      = world.Resource<Cfg>().ValueRO.SomeInt;
```

#### 不会触发
```csharp
// 通过 ref-local 直接写入
ref var d = ref entity.Ref<Attack>();
d.Delay = 0;

// 通过 inline ref 直接写入
entity.Ref<Position>().X = 5;

// 字段上的可写 ref 别名
ref var t = ref entity.Ref<Transform>();
ref var x = ref t.Position.X;
x = 1f;

// 在非 readonly struct 上调用非 readonly 方法 —— 保守视为修改
ref var s = ref entity.Ref<StateMachine>();
s.Advance();
entity.Ref<StateMachine>().Advance();   // 也不会触发 —— 同一规则的 inline 形态

// ref/out 参数
Method(ref entity.Ref<Position>().X);

// Add 没有只读 sibling，规则不触发
entity.Add<Tag>().Value = 1;
```

___

### FFSECS0020
**类别：** Correctness · **严重程度：** Error · **CodeFix：** 有

`class` 实现了任意 StaticEcs 标记接口（`IComponent`、`ITag`、`IEvent`、`ILinkType`、`ILinksType`、`IEntityType`、`IWorldType`）会破坏泛型派发：StaticEcs 的公共 API 都带 `where T : struct` 约束，并且基于反射的 `RegisterAll` 会跳过 class 实现。

#### 触发
```csharp
public class Health : IComponent { public int Value; }   // FFSECS0020
```

#### 修复
```csharp
public struct Health : IComponent { public int Value; }  // ok
```

___

### FFSECS0021
**类别：** Correctness · **严重程度：** Error · **CodeFix：** 有

与 FFSECS0020 相同，但专门针对 `IMultiComponent`。

___

### FFSECS0022
**类别：** Correctness · **严重程度：** Warning · **CodeFix：** —

实现 `IMultiComponent` 的 struct 如果**不是** `unmanaged`（包含 `string`、数组、委托等托管字段），必须重写 `Write(ref BinaryPackWriter)` 与 `Read(ref BinaryPackReader)`。接口默认实现是空的 —— 不重写则快照会对托管载荷静默写入空数据。

#### 触发
```csharp
public struct Inventory : IMultiComponent { public string Owner; }  // FFSECS0022
```

#### 修复
```csharp
public struct Inventory : IMultiComponent {
    public string Owner;
    public void Write(ref BinaryPackWriter w) { w.WriteString(Owner); }
    public void Read(ref BinaryPackReader r) { Owner = r.ReadString(); }
}
```

Unmanaged struct（`int`/`float`/`Nullable<int>` 等）由存储以 bulk-copy 处理，无需重写。

___

### FFSECS0030
**类别：** Correctness · **严重程度：** Info · **CodeFix：** 有

`Query.For` lambda 的 `ref T` 参数从未被写入。在开启变更跟踪的运行时，对 `ref` 参数的任何访问都会把组件标记为已修改 —— 即便函数体只读不写。换用 `in T` 重载以明确只读意图并跳过变更标记。

#### 触发
```csharp
W.Query().For((ref Health h) => { Console.WriteLine(h.Value); }); // FFSECS0030
```

#### 修复
```csharp
W.Query().For((in Health h) => { Console.WriteLine(h.Value); });
```

___

### FFSECS0031
**类别：** Performance · **严重程度：** Error · **CodeFix：** —

传给 `Query.For`（或任何 fluent `.For(...)`）的 lambda 如果捕获了外部状态（`this`、方法局部变量、实例字段/属性/方法），每次执行查询都会分配闭包。替代方案：

- `static` lambda + `userData` 重载（`For<TData>(userData, static (ref TData d, …) => …)`）。
- 实现 `W.IQuery.Write<…>` / `W.IQuery.Read<…>` 的 `struct`。
- `foreach (var entity in W.Query<…>().Entities())` + `ref var` 本地变量。

#### 触发
```csharp
var multiplier = 2;
W.Query().For((ref Health h) => { h.Value *= multiplier; });    // FFSECS0031
```

#### 修复
```csharp
var multiplier = 2;
W.Query().For(multiplier, static (ref int m, ref Health h) => { h.Value *= m; });
```

method-group 指向非静态实例方法的引用也会被检测到（它们会捕获 `this`）。

___

### FFSECS0032
**类别：** Usage · **严重程度：** Info · **CodeFix：** 有

`Entity.IsMatch<TFilter>()` 对任意 `IQueryFilter` 都能用，但对于简单形态（`All<…>`、`Any<…>`、`None<…>` 以及它们的 `*WithDisabled`/`*OnlyDisabled` 变体、`EntityIs<…>`、`EntityIsAny<…>`、`EntityIsNot<…>`），Entity 上有更简短、更能表达意图的直接方法：

| 过滤器 | 等价方法 |
|---|---|
| `All<T..>`（arity 1-3） | `HasEnabled<T..>()` |
| `AllWithDisabled<T..>` | `Has<T..>()` |
| `AllOnlyDisabled<T..>` | `HasDisabled<T..>()` |
| `Any<T..>`（arity 2-3） | `HasEnabledAny<T..>()` |
| `AnyWithDisabled<T..>` | `HasAny<T..>()` |
| `AnyOnlyDisabled<T..>` | `HasDisabledAny<T..>()` |
| `None<T..>` | `!HasEnabled<…>` / `!HasEnabledAny<…>` |
| `NoneWithDisabled<T..>` | `!Has<…>` / `!HasAny<…>` |
| `EntityIs<T>` | `Is<T>()` |
| `EntityIsAny<T..>` | `IsAny<T..>()` |
| `EntityIsNot<T..>` | `IsNot<T..>()` |

#### 触发
```csharp
if (entity.IsMatch<All<Health, Mana>>())  { … }   // FFSECS0032
if (entity.IsMatch<None<Stunned>>())      { … }   // FFSECS0032
```

#### 修复
```csharp
if (entity.HasEnabled<Health, Mana>()) { … }
if (!entity.HasEnabled<Stunned>())     { … }
```

约束检查：`HasEnabled`/`HasEnabledAny`/`HasDisabled`/`HasDisabledAny` 都要求 `T : struct, IComponent, IDisableable`。对于 `All<…>`、`Any<…>`、`None<…>`（它们接受 `IComponentOrTag` —— 允许 tag）只有当每个类型参数同时实现 `IComponent` 和 `IDisableable` 时诊断才触发；否则朴素替换无法编译，规则会静默跳过。`*OnlyDisabled` 过滤器本身已带相同的约束，自动满足检查。

复合过滤器（`And<…>`、`Or<…>`、`Nothing`）以及 arity > 3 不会被建议替换 —— 那里 `IsMatch` 仍是唯一实用入口。

___

### FFSECS0033
**类别：** Usage · **严重程度：** Info · **CodeFix：** 有

`foreach (var entity in W.Query<…>().Entities()) { ref var x = ref entity.Ref<T>(); … }` 这种模式有一个更紧凑、意图更清晰的形式 —— `W.Query<…>().For((ref T x, …) => { … })`。通过 `entity.Ref<T>()`/`Mut<T>()`/`Read<T>()` 访问的组件直接成为 lambda 的参数，并从相应的 `All<…>` 中被移除（因为 `For` 会按签名隐式补回过滤器）。

CodeFix 的重写规则：

- 体内通过 `entity.Ref<T>()`/`Mut<T>()` 访问、且 `T` 出现在某个 `All<…>` 中的组件，变成 `ref T` 参数；`entity.Read<T>()` 变成 `in T`。对应的 `ref var X = ref entity.Ref<T>();` 声明会被删除。
- 每个被吸收的 `T` 从其 `All<…>` 中移除。空的 `All<…>` 会从外层 `And<…>` 中折叠掉；若 `And<…>` 退化到单个参数则被解包；若顶层过滤器整体变空，`Query<…>()` 退化为 `Query()`。
- 其它过滤器（`None<…>`、`Any<…>`、`EntityIs<…>` 等）以及体内未触及的 `All<…>` 组件原样保留。
- 如果 `entity` 还有其它用途（例如 `entity.Has<Tag>()`、`entity.Destroy()`，或 `entity.Ref<U>()` 但 `U` 不在 `All<…>` 中），lambda 会得到 `Entity entity` 形参，相关调用保留在体内。
- 如果体内捕获了一个外部局部变量/形参,CodeFix 会改用 `For<TData>(ref data, static (ref TData data, …) => …)` 重载并把 lambda 标记为 `static` —— 不分配闭包。

不触发（不产出诊断）的情况：

- 体内出现 `break`、`continue`、`return`、`yield`、`goto`、`throw`、`await`、嵌套匿名函数或嵌套 local function —— 它们在 lambda 体中无法一一保留。
- 体内捕获了 `this`、实例字段，或两个及以上不同的外部局部变量/形参 —— V1 不会合成多字段 UserData 结构。
- 被吸收组件总数超过 6 —— `For` 只到 `T0..T5`。
- 体内没有任何 `entity.Ref/Mut/Read<T>()` 调用所触及的 `T` 出现在 `All<…>` 中 —— 没有可吸收对象，重写没有收益。
- 过滤器形态包含 CodeFix 无法安全修改的结构（例如顶层的 `Or<…>`）—— V1 只支持 `All<…>`/`And<…>`/`None<…>`/`Any<…>` 的组合。

#### 触发
```csharp
foreach (var entity in W.Query<All<NeedsData>>().Entities()) {
    ref var needs = ref entity.Ref<NeedsData>();
    needs.Hunger++;
    needs.Thirst++;
    needs.Tired++;
}
```

#### 修复
```csharp
W.Query().For((ref NeedsData needs) => {
    needs.Hunger++;
    needs.Thirst++;
    needs.Tired++;
});
```

___

### FFSECS0040
**类别：** Correctness · **严重程度：** Error · **CodeFix：** —

对组件的 `ref`/`in` 引用在底层实体失效后变成悬挂引用。跟踪三种模式：

- **`WorldQuery.For` 的 lambda** —— 引用即 lambda 的 `ref`/`in` 组件参数。
- **实现 `IQuery.*` 的 `struct`** —— 引用即 `Invoke` 的 `ref`/`in` 参数。
- **来自 `entity.Ref/Mut/Read/Add(...)` 的 `ref`-locals**。

失效操作：`Destroy`、`MoveTo`、`Unload`（整体 kill）、`Delete<T>`（仅指向 `T` 类型组件的引用）。

#### 触发
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    e.Destroy();
    hp.Value = 0;                       // FFSECS0040 —— hp 指向已释放的存储
});
```

#### 修复
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    var snap = hp.Value;                // 先快照
    e.Destroy();
    Use(snap);                          // ok
});
```

___

### FFSECS0041
**类别：** Correctness · **严重程度：** Error · **CodeFix：** —

与 FFSECS0040 对称，但追踪的是**实体变量本身**，而非对组件的 `ref`/`in` 引用。对某个 local 或参数执行 `Destroy`/`MoveTo`/`Unload` 后，对该变量的任何后续操作（`Has`、`Add`、`IsActual`、…）都会被标出。只允许：

- 直接重新赋值（`entity = W.NewEntity<…>();`）。
- out 参数重绑（`Method(out entity);` 或循环中的 `Method(out var entity)`）。

#### 触发
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
_ = e.Has<Health>();                    // FFSECS0041
```

#### 修复
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
e = W.NewEntity<Default>();             // 重新赋值清除 taint
_ = e.Has<Health>();                    // ok
```

跨条件分支的合并是保守的 —— 只要任一前驱路径让变量失效，汇合点就视为有 taint。

___

### FFSECS0042
**类别：** Correctness · **严重程度：** Warning · **CodeFix：** 是

`Entity.Ref<T>()`、`Entity.Mut<T>()`、`Entity.Read<T>()` 要求 `T` 已经存在于实体上 —— 否则 DEBUG 构建会触发断言，Release 构建则会返回某个无关插槽的数据。分析器对方法/lambda 的 CFG 进行前向数据流分析，在所有传入路径上**不能静态证明**接收者 entity 持有 `T` 的调用点上发出警告。

`entity` 上对 `T` 的保证由以下方式建立：

- 先前 `entity.Has<T...>()`、`HasEnabled<T...>()`、`HasDisabled<T...>()`（任意元数 —— 每个泛型参数都加入）的 true 分支。
- 先前 `entity.IsMatch<F>()` 的 true 分支，其中 `F`（经嵌套 `And<…>` 展开后）化简为 `All<T>` / `AllOnlyDisabled<T>` / `AllWithDisabled<T>`。`None`/`Any`/`EntityIs*` 过滤器不贡献保证。
- 在 `Query<TFilter>().For(...)` 的 lambda 体内 —— `TFilter` 的所有 `All*` 组件对 lambda 的 `Entity` 参数都已保证；此外，lambda 签名中每个 `ref T` / `in T` 组件参数也已保证。
- 在 `IQuery<...>.Invoke` 的方法体内 —— 仅签名中每个 `ref T` / `in T` 组件参数对 `Entity` 参数已保证（`TFilter` 在该层不可见，由调用点决定）。
- 同一 local/parameter 上先前的 `entity.Add<T>(...)`、`Set<T>(...)`、`Ref<T>()`、`Mut<T>()`、`Read<T>()`，且其间没有失效操作。

失效操作会清除保证：

- `entity.Delete<T>()` 仅清除 `T`。
- `entity.Destroy()` / `MoveTo(…)` / `Unload(…)` 清除该 entity 的所有保证。
- 对 entity 变量重新赋值（`entity = …;`）或以 `ref`/`out` 传递，都会清除该 local/parameter 的所有保证。

分析器只追踪可解析为单一 `ILocalSymbol` 或 `IParameterSymbol` 的 entity。链式调用、属性/字段访问、`default(Entity)` 等接收者无法被 `Has` 守卫绑定，会无条件报告。

#### 触发
```csharp
ref var pos = ref entity.Ref<Position>();                                              // FFSECS0042 — 无 Position 存在性证明
W.Query<None<Stunned>>().For((W.Entity e) => { e.Ref<Position>(); });                  // FFSECS0042 — 过滤器不含 All<Position>
if (entity.Has<Velocity>()) { entity.Ref<Position>(); }                                // FFSECS0042 — 守卫的是 Velocity 而非 Position
entity.Delete<Position>();
ref var lost = ref entity.Ref<Position>();                                             // FFSECS0042 — 保证被 Delete<Position> 清除
```

#### 修复
```csharp
if (entity.Has<Position>()) {
    ref var pos = ref entity.Ref<Position>();                                          // ok — true 分支保证 Position
}

if (!entity.Has<Position>()) return;
ref var pos2 = ref entity.Ref<Position>();                                             // ok — early-return 守卫

entity.Add<Position>();
ref var pos3 = ref entity.Ref<Position>();                                             // ok — Add 建立保证

W.Query<All<Position, Velocity>>()
    .For((W.Entity e, ref Position p) => { ref var velocity = ref e.Ref<Velocity>(); });  // ok — 两者均来自 All<…>
```

#### 单点抑制：后缀 `!`
```csharp
ref var pos = ref entity.Ref<Position>()!;                                             // ok — `!` 抑制本次调用上的 FFSECS0042
entity.Mut<Position>()!.X = 5;                                                         // ok — Mut/Read 同样适用
```
对 `Ref`/`Mut`/`Read` 调用后追加 null-forgiving 后缀 `!` 仅抑制该次调用的诊断。C# 在 `!` 之后仍保留表达式的值/ref 类别，因此被抑制的调用仍按引用返回，可与 `ref var` 绑定。`!` 不受项目级 nullable 设置影响。被抑制的调用之后，dataflow 仍会记录 `T` 的保证，所以在同一 entity 上对同一组件的后续访问无需再写 `!`。接收者形式 `entity!.Ref<T>()` **不会**被识别 —— 标记必须置于具体的组件访问点。随附 CodeFix「Suppress FFSECS0042 with '!' after the call」可一键应用。

#### 局限
- 通过中间 local 传递的布尔守卫（`var ok = entity.Has<Position>(); if (ok) …`）不会被传播。
- V1 中 `Components<T>.Ref/Mut/Read(entity)` 重载不是该规则的检查点；只检查 `entity.X<T>()` 实例形式。
- 不做跨方法分析：辅助函数 `void Use(W.Entity e) => e.Ref<T>();` 内部若无守卫，仍会报警。

___

### FFSECS0050
**类别：** Correctness · **严重程度：** Error · **CodeFix：** —

某个组件在查询中被引用多次 —— 可能是同种过滤器中的重复（`All`+`All`、`None`+`None`、`Any`+`Any`，含其 `*WithDisabled`/`*OnlyDisabled` 变体），或是过滤器链与 lambda 的 `ref`/`in` 参数之间的重叠，或与 `IQuery` 结构的组件泛型之间的重叠。

#### 触发
```csharp
foreach (var _ in W.Query<All<Health>, All<Health>>().Entities()) { }                       // FFSECS0050
W.Query<All<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0050 —— 过滤器 ↔ lambda
W.Query<All<Health>>().Write<Health>().For<MyWriteFn>();                                    // FFSECS0050 —— 过滤器 ↔ IQuery generic
foreach (var _ in W.Query<All<Health>, AllOnlyDisabled<Health>>().Entities()) { }           // FFSECS0050 —— 基础 + disabled 变体
```

___

### FFSECS0051
**类别：** Correctness · **严重程度：** Error · **CodeFix：** —

同一组件同时出现在 `All<…>` 和 `None<…>` 中 —— 查询结果永远为空。lambda 参数与 `IQuery` 结构泛型隐含的 `All` 贡献也会计入。

#### 触发
```csharp
foreach (var _ in W.Query<All<Health>, None<Health>>().Entities()) { }                       // FFSECS0051
W.Query<None<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0051 —— lambda 隐含 All
```

___

## 关闭诊断

针对单行 / 单块：
```csharp
#pragma warning disable FFSECS0011
var snap = entity.Read<Health>();
#pragma warning restore FFSECS0011
```

针对整个项目（`.editorconfig`）：
```ini
[*.cs]
dotnet_diagnostic.FFSECS0011.severity = none
```

针对构建（`csproj`）：
```xml
<NoWarn>FFSECS0011</NoWarn>
```

___

## 源代码

所有分析器位于 `StaticEcs/Analyzers~/Src/Analyzers/*.cs`；code-fix 位于 `StaticEcs/Analyzers~/CodeFixes/`。规则 ID 集中在 `StaticEcs/Analyzers~/Shared/FFSECSIds.cs`。

<p align="center">
  <a href="./README.md"><img src="https://img.shields.io/badge/EN-English-blue?style=flat-square" alt="English"></a>
  <a href="./README_RU.md"><img src="https://img.shields.io/badge/RU-Русский-blue?style=flat-square" alt="Русский"></a>
  <a href="./README_ZH.md"><img src="https://img.shields.io/badge/ZH-中文-blue?style=flat-square" alt="中文"></a>
  <br><br>
  <img src="https://img.shields.io/badge/version-1.0.1-blue?style=for-the-badge" alt="Version">
  <a href="https://www.nuget.org/packages/FFS.StaticEcs.Analyzers/"><img src="https://img.shields.io/badge/NuGet-FFS.StaticEcs.Analyzers-004880?style=for-the-badge&logo=nuget" alt="NuGet"></a>
  <a href="https://github.com/Felid-Force-Studios/StaticEcs"><img src="https://img.shields.io/badge/StaticEcs-framework-orange?style=for-the-badge" alt="StaticEcs"></a>
</p>

# Static ECS Analyzer — Roslyn-диагностики и code-fix'ы для FFS.StaticEcs

Набор Roslyn-анализаторов и автоматических исправлений, который ловит типовые ошибки использования фреймворка [StaticEcs](https://github.com/Felid-Force-Studios/StaticEcs) во время компиляции.
Пакет самостоятельный — основной пакет `FFS.StaticEcs` **не** содержит этих анализаторов, подключать нужно отдельно.

## Установка

### NuGet
```
dotnet add package FFS.StaticEcs.Analyzers
```

### Unity (UPM)
Через git-URL в Unity PackageManager:
```
https://github.com/Felid-Force-Studios/StaticEcs-Analyzer.git
```
Или добавив в `Packages/manifest.json`:
```json
"com.felid-force-studios.static-ecs-analyzer": "https://github.com/Felid-Force-Studios/StaticEcs-Analyzer.git"
```

Анализатор сам отключается, если проект не ссылается на `FFS.StaticEcs`, поэтому его безопасно держать в любом solution.

Категории диагностик:

- **`FFS.StaticEcs.Correctness`** — код компилируется, но семантически неверен (молчаливые копии ref-возвратов, обращения к удалённым сущностям, противоречивые фильтры запросов и т. п.).
- **`FFS.StaticEcs.Performance`** — паттерны, которые аллоцируют память или блокируют рантайм-оптимизации (захват замыканием в `Query.For`).
- **`FFS.StaticEcs.Usage`** — стилистические предложения (есть более прямой API).

___

## Список правил

| ID | Категория | Severity | Заголовок | CodeFix |
|---|---|---|---|---|
| [FFSECS0010](#ffsecs0010) | Correctness | Error | Результат ref-возврата должен биндиться через `ref` | да |
| [FFSECS0011](#ffsecs0011) | Correctness | Info | Результат `Read<T>()` биндится в копию | да |
| [FFSECS0012](#ffsecs0012) | Correctness | Info | ref-local над storage передан по значению (атомарно-значимые типы пропускаются) | да |
| [FFSECS0013](#ffsecs0013) | Correctness | Info | Writable ref от ref-возвращающего члена используется только для чтения; предлагается read-only сиблинг | да |
| [FFSECS0020](#ffsecs0020) | Correctness | Error | Marker-интерфейс StaticEcs должен реализовываться `struct` | да |
| [FFSECS0021](#ffsecs0021) | Correctness | Error | `IMultiComponent` должен реализовываться `struct` | да |
| [FFSECS0022](#ffsecs0022) | Correctness | Warning | Non-unmanaged `IMultiComponent` должен переопределить `Write`/`Read` | — |
| [FFSECS0030](#ffsecs0030) | Correctness | Info | Параметр `ref` лямбды `Query.For` ни разу не записывается | да |
| [FFSECS0031](#ffsecs0031) | Performance | Error | Лямбда в `Query.For` захватывает внешнее состояние | — |
| [FFSECS0032](#ffsecs0032) | Usage | Info | `IsMatch<TFilter>()` можно заменить прямым методом Entity | да |
| [FFSECS0033](#ffsecs0033) | Usage | Info | `foreach` по `Entities()` конвертируется в `Query.For(...)` | да |
| [FFSECS0040](#ffsecs0040) | Correctness | Error | Используется `ref`/`in` ссылка на компонент после инвалидации | — |
| [FFSECS0041](#ffsecs0041) | Correctness | Error | Используется сущность после инвалидации | — |
| [FFSECS0042](#ffsecs0042) | Correctness | Warning | `Ref`/`Mut`/`Read<T>` вызывается без видимой гарантии присутствия `T` | да |
| [FFSECS0050](#ffsecs0050) | Correctness | Error | Дубликат компонента в фильтре запроса | — |
| [FFSECS0051](#ffsecs0051) | Correctness | Error | Противоречие `All` + `None` в фильтре | — |

___

## Правила

### FFSECS0010
**Категория:** Correctness · **Severity:** Error · **CodeFix:** да

`Entity.Ref/Mut/Add`, `Components<T>.Ref/Mut/Add`, `Resource<T>.Value`, `NamedResource<T>.Value`, `Multi<T>.First/Last/[i]`, `MultiComponentsIterator<T>.Current` возвращают по ссылке. Биндинг результата в обычный local молча копирует компонент — последующая мутация пишется в копию, а не в storage. Ловится в объявлении переменной, аргументе по значению, простом присваивании и обычном `return`.

Reference-типы (например `Resource<MyClass>.Value`) исключены: копирование ссылки дёшево и идиоматично. То же подавление применяется, когда **внешнее** значение цепочки — атомарно копируемый тип: примитив, enum, `IntPtr`/`UIntPtr` или ссылочный тип (например `entity.Ref<C>().PrimitiveField`). Копия такого значения losslessly эквивалентна ref-привязке, поэтому диагностика подавляется.

#### Срабатывает
```csharp
var pos = entity.Ref<Position>();           // FFSECS0010 — молчаливая копия
Consume(entity.Ref<Position>());            // FFSECS0010 — копия на границе вызова
return entity.Ref<Position>();              // FFSECS0010 — копия на возврате
```

#### Без диагностики
```csharp
ref var pos = ref entity.Ref<Position>();   // ok — ref-binding
entity.Ref<Position>().Value = 5;           // ok — прямая запись
Consume(ref entity.Ref<Position>());        // ok — передача по ref
```

#### Явный opt-in на копию: `*RO`-сиблинги
Когда копия (snapshot) — это намеренный выбор для `Resource<T>` / `NamedResource<T>` / `Multi<T>` / `MultiComponentsIterator<T>`, используйте специальные `*RO`-члены вместо биндинга mutable ref-возврата в обычный local. Они возвращают `ref readonly T` и намеренно **не** включены в allow-list анализатора — суффикс `RO` выражает намерение в самом коде.

| Mutable (флагается) | Read-only сиблинг |
|---|---|
| `Resource<T>.Value` | `Resource<T>.ValueRO` |
| `NamedResource<T>.Value` | `NamedResource<T>.ValueRO` |
| `Multi<T>.First()` | `Multi<T>.GetFirst()` |
| `Multi<T>.Last()` | `Multi<T>.GetLast()` |
| `Multi<T>[idx]` | `Multi<T>.Get(idx)` |
| `MultiComponentsIterator<T>.Current` | `MultiComponentsIterator<T>.CurrentRO` |

```csharp
var snapshot = timer.ValueRO;               // ok — explicit RO opt-in, без диагностики
ref readonly var refSnap = ref multi.GetFirst();
```

Для `Entity` и `Components<T>` snapshot-путь — это `Read<T>()` / `Read(Entity)`

Codefix предлагает действие «Switch to '`*RO`' (intentional copy)» в один клик.

Для `Entity.Ref<T>` / `Components<T>.Ref/Mut` на `var`-объявлении codefix дополнительно предлагает «Switch to `Read<T>()`». Набор вариантов зависит от размера payload'а:

- **T ≤ 8 байт** (по тому же порогу, что и size-based подавление FFSECS0011): два действия — сначала `var x = entity.Read<T>();` (простой copy-snapshot, рекомендуемый), затем `ref readonly var x = ref entity.Read<T>();` (bind через `ref readonly`, если пользователь хочет именно его).
- **T > 8 байт или неизвестно**: только bind-вариант. Копия большой структуры — пессимизация, поэтому её не предлагаем.

___

### FFSECS0011
**Категория:** Correctness · **Severity:** Info · **CodeFix:** да

`Entity.Read<T>()` и `Components<T>.Read(Entity)` возвращают `ref readonly T`. Биндинг в обычный local — копия, нежелательная для больших компонентов. Severity Info: подсказка в IDE, не ломает сборку. Подавить можно через `dotnet_diagnostic.FFSECS0011.severity = none` в `.editorconfig`.

Как и FFSECS0010, правило подавляется, когда внешнее значение цепочки — атомарно копируемый тип: примитив, enum, `IntPtr`/`UIntPtr` или ссылочный тип. Например `entity.Read<C>().PrimitiveField` диагностику **не выдаёт**: на выходе уже значение, которое копируется без потерь.

Правило **также** подавляется, если payload `Read<T>` — value-тип с консервативно оценённым размером ≤ 8 байт: `struct Id { uint Value; }`, `readonly struct WorldEntityMask { uint Mask; }`, `struct Pair { uint A; uint B; }`. Копия ≤ 8 байт в современных ABI (x64/ARM64) укладывается в один регистр — биндинг через `ref readonly var` не даёт измеримого выигрыша, и `readonly`-ность struct'а тут не важна. Оценка консервативная: open generic, explicit struct layout, поля-указатели или fixed-buffer → подавления нет.

#### Срабатывает
```csharp
var snapshot = entity.Read<Position>();     // FFSECS0011 — копия
```

#### Без диагностики
```csharp
ref readonly var snap = ref entity.Read<Position>();
Consume(in entity.Read<Position>());        // ok — передача по in
```

___

### FFSECS0012
**Категория:** Correctness · **Severity:** Info · **CodeFix:** да

`ref` / `ref readonly` local, привязанный к источнику ref-возврата StaticEcs, передан по значению. Это копирует компонент на границе вызова — callee изменит копию. Подсказка эвристическая: правило не отличает «случайную» потерю ref-семантики от «явной» передачи текущего значения, поэтому surfaces как Info; чтобы скрыть глобально — `dotnet_diagnostic.FFSECS0012.severity = none` в `.editorconfig`.

Атомарно-значимые типы автоматически исключаются — у них нет внутреннего state-а, который мог бы быть «потерян» при копии: примитивы (`bool`/`int`/`float`/...), `enum`, ссылочные типы (локал хранит ссылку — копия указателя бьёт по тому же heap-объекту).

`ref readonly` локали тоже не отслеживаются: пользователь уже осознанно выбрал readonly-снимок, мутировать storage через такой ref нельзя — терять writable-семантику ref нечего. Это покрывает в т.ч. вызовы extension-методов на `ref readonly` локалях (`entityType.Name()` в интерполяции и т.п.).

#### Срабатывает
```csharp
ref var hp = ref entity.Ref<Health>();      // Health — multi-field struct
Consume(hp);                                // FFSECS0012 — копия
```

#### Без диагностики
```csharp
Consume(ref hp);                            // ok
Consume(in hp);                             // ok — 'in' принимает ref-local
ref var id = ref entity.Add<PlayerId>().Value;  // .Value — ushort, атомарно
SetBehaviour(id);                           // ok — primitive не трекается
ref var st = ref entity.Ref<C>().Status;    // Status — enum
M(st);                                      // ok — enum не трекается
```

___

### FFSECS0013
**Категория:** Correctness · **Severity:** Info · **CodeFix:** да

Writable ref, полученный из ref-возвращающего члена StaticEcs, используется только для чтения. Ловятся две формы:

1. **Ref-local биндинг** — `ref var x = ref entity.Ref<T>()` (или `.Mut<T>()` / ref-член Multi/Resource/итератора), через который никогда не пишется, не передаётся по `ref`/`out`, не берётся writable ref-alias и не вызывается non-readonly instance-метод.
2. **Inline чтение** — `entity.Ref<T>().Field` / `world.Resource<T>().Value.X` / `multi.First().Field` / `multi[i].Y` и т. п., где результат уходит в read-only потребитель (инициализация `var`, чтение поля/проперти, аргумент `in`/by-value, return из non-ref метода).

В обоих случаях writable ref не нужен, а `Mut<T>` ещё и зря маркирует компонент как **changed**. Подсказка предлагает соответствующий read-only сиблинг. Severity Info: подавить через `dotnet_diagnostic.FFSECS0013.severity = none` в `.editorconfig`.

**Read-only сиблинги:**

| Writable-член | Read-only сиблинг |
|---|---|
| `Entity.Ref<T>()` / `Entity.Mut<T>()` | `Entity.Read<T>()` |
| `Components<T>.Ref(Entity)` / `Mut(Entity)` | `Components<T>.Read(Entity)` |
| `Resource<T>.Value`, `NamedResource<T>.Value` | `ValueRO` |
| `Multi<T>.First()` | `GetFirst()` |
| `Multi<T>.Last()` | `GetLast()` |
| `Multi<T>[int]` | `Multi<T>.Get(int)` |
| `MultiComponentsIterator<T>.Current` | `CurrentRO` |

`Entity.Add<T>()` / `Components<T>.Add(...)` не имеют read-only сиблинга и правило на них не срабатывает.

«Мутация» определяется консервативно: прямая запись, compound-присваивания, `++`/`--`, передача по `ref`/`out`, взятие writable-ref alias'а (`ref var alias = ref local.Field`), вызов instance-метода на non-readonly struct. Чтение, `in`-passing и создание `ref readonly` alias'а — не мутация.

#### Срабатывает
```csharp
// Ref-local форма.
ref var d = ref entity.Ref<Attack>();                              // FFSECS0013 — только чтения ниже
Console.WriteLine(d.Delay);
Process(d.AttackerId);                                              // pass by value — не мутация

// Inline — outer ссылочный тип.
var transform = entity.Ref<GameObjectRef>().Val.transform;          // FFSECS0013 → Read<GameObjectRef>()

// Inline — outer примитив.
var x = entity.Ref<Position>().X;                                   // FFSECS0013

// Inline — цепочка через non-ref property.
var y = entity.Ref<Big>().SomeProperty.Field;                       // FFSECS0013

// Multi / Resource inline.
var first = multi.First().Field;                                    // FFSECS0013 → GetFirst()
var cfgN  = world.Resource<Cfg>().Value.SomeInt;                    // FFSECS0013 → ValueRO
```

#### Фикс (диагностики нет)
```csharp
// Ref-local: биндинг через 'ref readonly' (по умолчанию).
ref readonly var d = ref entity.Read<Attack>();

// Для payload ≤ 8 байт дополнительно предлагается copy snapshot.
var d = entity.Read<Attack>();

// Inline.
var transform = entity.Read<GameObjectRef>().Val.transform;
var first     = multi.GetFirst().Field;
var cfgN      = world.Resource<Cfg>().ValueRO.SomeInt;
```

#### НЕ срабатывает
```csharp
// прямая запись через ref-local
ref var d = ref entity.Ref<Attack>();
d.Delay = 0;

// прямая запись через inline ref
entity.Ref<Position>().X = 5;

// writable ref-alias на поле
ref var t = ref entity.Ref<Transform>();
ref var x = ref t.Position.X;
x = 1f;

// non-readonly method call на non-readonly struct — консервативно считается мутацией
ref var s = ref entity.Ref<StateMachine>();
s.Advance();
entity.Ref<StateMachine>().Advance();   // тоже молчит — inline-форма того же правила

// ref/out-аргумент
Method(ref entity.Ref<Position>().X);

// Add не имеет read-only сиблинга и не диагностируется
entity.Add<Tag>().Value = 1;
```

___

### FFSECS0020
**Категория:** Correctness · **Severity:** Error · **CodeFix:** да

`class`, реализующий любой marker-интерфейс StaticEcs (`IComponent`, `ITag`, `IEvent`, `ILinkType`, `ILinksType`, `IEntityType`, `IWorldType`), ломает generic-диспетчеризацию: весь публичный API StaticEcs объявлен с `where T : struct`, а reflection-based `RegisterAll` пропустит class.

#### Срабатывает
```csharp
public class Health : IComponent { public int Value; }   // FFSECS0020
```

#### Без диагностики
```csharp
public struct Health : IComponent { public int Value; }  // ok
```

___

### FFSECS0021
**Категория:** Correctness · **Severity:** Error · **CodeFix:** да

То же, что FFSECS0020, но конкретно для `IMultiComponent`.

___

### FFSECS0022
**Категория:** Correctness · **Severity:** Warning · **CodeFix:** —

`struct`-реализация `IMultiComponent`, которая **не** является `unmanaged` (содержит managed-поля: `string`, массивы, делегаты, …), обязана переопределить оба метода `Write(ref BinaryPackWriter)` и `Read(ref BinaryPackReader)`. Дефолтные реализации интерфейса пустые — без переопределения снапшоты молча сохранят пустые данные для managed-полей.

#### Срабатывает
```csharp
public struct Inventory : IMultiComponent { public string Owner; }  // FFSECS0022
```

#### Без диагностики
```csharp
public struct Inventory : IMultiComponent {
    public string Owner;
    public void Write(ref BinaryPackWriter w) { w.WriteString(Owner); }
    public void Read(ref BinaryPackReader r) { Owner = r.ReadString(); }
}
```

Unmanaged-структуры (`int`/`float`/`Nullable<int>` …) сериализуются bulk-copy и в переопределениях не нуждаются.

___

### FFSECS0030
**Категория:** Correctness · **Severity:** Info · **CodeFix:** да

`ref T`-параметр лямбды `Query.For`, который ни разу не записывается, при включённом change-tracking всё равно помечает компонент изменённым, потому что обращение идёт через `ref`. Замените на `in T` — это явно сигнализирует read-only намерение и пропускает отметку изменения.

#### Срабатывает
```csharp
W.Query().For((ref Health h) => { Console.WriteLine(h.Value); }); // FFSECS0030
```

#### Без диагностики
```csharp
W.Query().For((in Health h) => { Console.WriteLine(h.Value); });
```

___

### FFSECS0031
**Категория:** Performance · **Severity:** Error · **CodeFix:** —

Лямбда, переданная в `Query.For` (или в любой fluent-`.For(...)`) и захватывающая внешнее состояние (`this`, локальную переменную, поле/свойство/метод инстанса), аллоцирует замыкание при каждом вызове запроса. Альтернативы:

- `static`-лямбда + перегрузка с `userData` (`For<TData>(userData, static (ref TData d, …) => …)`).
- `struct`, реализующий `W.IQuery.Write<…>` / `W.IQuery.Read<…>`.
- `foreach (var entity in W.Query<…>().Entities())` + `ref var`-локалы.

#### Срабатывает
```csharp
var multiplier = 2;
W.Query().For((ref Health h) => { h.Value *= multiplier; });    // FFSECS0031
```

#### Без диагностики
```csharp
var multiplier = 2;
W.Query().For(multiplier, static (ref int m, ref Health h) => { h.Value *= m; });
```

Method-group ссылки на нестатический instance-метод тоже ловятся (они захватывают `this`).

___

### FFSECS0032
**Категория:** Usage · **Severity:** Info · **CodeFix:** да

`Entity.IsMatch<TFilter>()` работает для любого `IQueryFilter`, но для простых фильтров (`All<…>`, `Any<…>`, `None<…>`, их `*WithDisabled`/`*OnlyDisabled`-варианты, `EntityIs<…>`, `EntityIsAny<…>`, `EntityIsNot<…>`) у `Entity` есть короткий и понятный прямой метод:

| Фильтр | Эквивалент |
|---|---|
| `All<T..>` (арность 1-3) | `HasEnabled<T..>()` |
| `AllWithDisabled<T..>` | `Has<T..>()` |
| `AllOnlyDisabled<T..>` | `HasDisabled<T..>()` |
| `Any<T..>` (арность 2-3) | `HasEnabledAny<T..>()` |
| `AnyWithDisabled<T..>` | `HasAny<T..>()` |
| `AnyOnlyDisabled<T..>` | `HasDisabledAny<T..>()` |
| `None<T..>` | `!HasEnabled<…>` / `!HasEnabledAny<…>` |
| `NoneWithDisabled<T..>` | `!Has<…>` / `!HasAny<…>` |
| `EntityIs<T>` | `Is<T>()` |
| `EntityIsAny<T..>` | `IsAny<T..>()` |
| `EntityIsNot<T..>` | `IsNot<T..>()` |

#### Срабатывает
```csharp
if (entity.IsMatch<All<Health, Mana>>())  { … }   // FFSECS0032
if (entity.IsMatch<None<Stunned>>())      { … }   // FFSECS0032
```

#### Без диагностики
```csharp
if (entity.HasEnabled<Health, Mana>()) { … }
if (!entity.HasEnabled<Stunned>())     { … }
```

Проверка констрейнтов: `HasEnabled`/`HasEnabledAny`/`HasDisabled`/`HasDisabledAny` требуют `T : struct, IComponent, IDisableable`. Для `All<…>`, `Any<…>`, `None<…>` (которые принимают `IComponentOrTag` — теги разрешены) диагностика срабатывает только когда каждый аргумент типа одновременно `IComponent` и `IDisableable`; иначе наивная замена не скомпилируется, поэтому правило молчит. У `*OnlyDisabled` фильтров такие же констрейнты уже зашиты — проверка для них автоматическая.

Композитные фильтры (`And<…>`, `Or<…>`, `Nothing`) и арность > 3 не предлагаются — там `IsMatch` остаётся единственным практичным API.

___

### FFSECS0033
**Категория:** Usage · **Severity:** Info · **CodeFix:** да

Паттерн `foreach (var entity in W.Query<…>().Entities()) { ref var x = ref entity.Ref<T>(); … }` имеет более компактный и выразительный аналог `W.Query<…>().For((ref T x, …) => { … })` — компоненты, к которым обращаются через `entity.Ref<T>()`/`Mut<T>()`/`Read<T>()`, переезжают в параметры лямбды и одновременно удаляются из соответствующего `All<…>` (поскольку `For` сам добавляет их в фильтр через сигнатуру делегата).

Как codefix переписывает тело:

- Компоненты, к которым обращаются через `entity.Ref<T>()`/`Mut<T>()` и которые перечислены в каком-либо `All<…>`, становятся параметрами `ref T` лямбды; `entity.Read<T>()` — параметром `in T`. Соответствующие объявления `ref var X = ref entity.Ref<T>();` удаляются.
- Каждый абсорбированный `T` удаляется из своего `All<…>`. Опустевшие узлы `All<…>` сворачиваются из охватывающих их `And<…>`; если `And<…>` сводится к одному аргументу — он распаковывается; если фильтр полностью опустел — `Query<…>()` превращается в `Query()`.
- Остальные фильтры (`None<…>`, `Any<…>`, `EntityIs<…>`, …) и компоненты `All<…>`, к которым тело не обращается, остаются без изменений.
- Если `entity` используется в теле как-то ещё (например, `entity.Has<Tag>()`, `entity.Destroy()`, или `entity.Ref<U>()` где `U` отсутствует в `All<…>`), то лямбда получает первый параметр `Entity entity`, а соответствующие вызовы остаются в теле.
- Если тело захватывает одну внешнюю локальную переменную/параметр, codefix использует перегрузку `For<TData>(ref data, static (ref TData data, …) => …)` и помечает лямбду `static` — без аллокации замыкания.

Не срабатывает (диагностика не выдаётся) в следующих случаях:

- В теле есть `break`, `continue`, `return`, `yield`, `goto`, `throw`, `await`, вложенная анонимная функция или вложенная local function — такие конструкции невозможно перенести в тело лямбды без изменения семантики.
- Тело захватывает `this`, инстанс-поле или две и более различных внешних локалов/параметров — перепаковка в UserData потребовала бы синтез отдельной структуры, codefix этого не делает.
- Суммарное число абсорбируемых компонентов превышает 6 — перегрузки `For` существуют только до `T0..T5`.
- Ни один `entity.Ref/Mut/Read<T>()` в теле не указывает на `T` из `All<…>` — поглощать нечего, перезапись была бы пустым шумом.
- Форма фильтра содержит конструкции, которые codefix не умеет безопасно модифицировать (например `Or<…>` на верхнем уровне) — V1 поддерживает только композиции `All<…>`/`And<…>`/`None<…>`/`Any<…>`.

#### Срабатывает
```csharp
foreach (var entity in W.Query<All<NeedsData>>().Entities()) {
    ref var needs = ref entity.Ref<NeedsData>();
    needs.Hunger++;
    needs.Thirst++;
    needs.Tired++;
}
```

#### После фикса
```csharp
W.Query().For((ref NeedsData needs) => {
    needs.Hunger++;
    needs.Thirst++;
    needs.Tired++;
});
```

___

### FFSECS0040
**Категория:** Correctness · **Severity:** Error · **CodeFix:** —

`ref`/`in` ссылки на компонент становятся невалидными после инвалидации соответствующей сущности. Отслеживаются три паттерна:

- **Лямбда в `WorldQuery.For`** — ссылки это `ref`/`in`-параметры лямбды.
- **`struct`, реализующий `IQuery.*`** — ссылки это `ref`/`in`-параметры метода `Invoke`.
- **`ref`-локалы из `entity.Ref/Mut/Read/Add(...)`**.

Инвалидаторы: `Destroy`, `MoveTo`, `Unload` (полный kill), `Delete<T>` (только ссылки на компонент типа `T`).

#### Срабатывает
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    e.Destroy();
    hp.Value = 0;                       // FFSECS0040 — hp указывает в освобождённое место
});
```

#### Без диагностики
```csharp
W.Query().For((W.Entity e, ref Health hp) => {
    var snap = hp.Value;                // снимок до Destroy
    e.Destroy();
    Use(snap);                          // ok
});
```

___

### FFSECS0041
**Категория:** Correctness · **Severity:** Error · **CodeFix:** —

Двойник FFSECS0040, но отслеживает не ссылки на компоненты, а **саму переменную-сущность**. После `Destroy`/`MoveTo`/`Unload` по локалу или параметру любая дальнейшая операция на этой переменной (`Has`, `Add`, `IsActual`, …) флагается. Разрешены только:

- Прямое переприсваивание (`entity = W.NewEntity<…>();`).
- Out-параметр (`Method(out entity);` или `Method(out var entity)` внутри цикла).

#### Срабатывает
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
_ = e.Has<Health>();                    // FFSECS0041
```

#### Без диагностики
```csharp
var e = W.NewEntity<Default>();
e.Destroy();
e = W.NewEntity<Default>();             // reassignment снимает taint
_ = e.Has<Health>();                    // ok
```

Объединение в условных ветках консервативное: если хоть один путь оставляет переменную невалидной, в точке слияния taint остаётся.

___

### FFSECS0042
**Категория:** Correctness · **Severity:** Warning · **CodeFix:** да

`Entity.Ref<T>()`, `Entity.Mut<T>()`, `Entity.Read<T>()` требуют, чтобы `T` уже был на сущности — иначе в DEBUG срабатывает ассерт, а в release возвращаются данные чужого слота. Анализатор делает forward-dataflow по CFG метода/лямбды и предупреждает на каждом вызове, где для приёмника-сущности нет **статически видимой гарантии** присутствия `T` по всем входящим путям.

Гарантия для `T` на `entity` устанавливается:

- В true-ветке предшествующей проверки `entity.Has<T...>()`, `HasEnabled<T...>()`, `HasDisabled<T...>()` (любая арность — каждый generic-аргумент добавляется).
- В true-ветке предшествующей `entity.IsMatch<F>()`, если `F` (с учётом вложенных `And<…>`) сводится к `All<T>` / `AllOnlyDisabled<T>` / `AllWithDisabled<T>`. `None`/`Any`/`EntityIs*`-фильтры ничего не добавляют.
- Внутри лямбды `Query<TFilter>().For(...)` — для entity-параметра гарантированы все компоненты из `All*`-фильтров `TFilter`, плюс каждый `ref T` / `in T` компонентный параметр в сигнатуре лямбды.
- Внутри метода `IQuery<...>.Invoke` — гарантированы только компоненты из `ref T`/`in T`-параметров сигнатуры (`TFilter` не виден на этом уровне, выбирается caller-сайтом).
- Предшествующим `entity.Add<T>(...)`, `Set<T>(...)`, `Ref<T>()`, `Mut<T>()`, `Read<T>()` по тому же локалу/параметру, если между вызовами не было инвалидатора.

Инвалидаторы снимают гарантии:

- `entity.Delete<T>()` снимает только `T`.
- `entity.Destroy()` / `MoveTo(…)` / `Unload(…)` снимают все гарантии для этой сущности.
- Переприсвоение entity-переменной (`entity = …;`) и передача её как `ref`/`out`-аргумента очищают все гарантии для локала/параметра.

Анализатор отслеживает только сущности, разрешающиеся в один `ILocalSymbol` или `IParameterSymbol`. Цепочки, доступ через свойство/поле или `default(Entity)` — к ним никакая `Has`-проверка привязаться не может, поэтому такие случаи предупреждаются безусловно.

#### Срабатывает
```csharp
ref var pos = ref entity.Ref<Position>();                                              // FFSECS0042 — Position не гарантирован
W.Query<None<Stunned>>().For((W.Entity e) => { e.Ref<Position>(); });                  // FFSECS0042 — в фильтре нет All<Position>
if (entity.Has<Velocity>()) { entity.Ref<Position>(); }                                // FFSECS0042 — проверка про Velocity, не Position
entity.Delete<Position>();
ref var lost = ref entity.Ref<Position>();                                             // FFSECS0042 — Delete<Position> снял гарантию
```

#### Корректно
```csharp
if (entity.Has<Position>()) {
    ref var pos = ref entity.Ref<Position>();                                          // ok — true-ветка даёт гарантию Position
}

if (!entity.Has<Position>()) return;
ref var pos2 = ref entity.Ref<Position>();                                             // ok — early-return-guard

entity.Add<Position>();
ref var pos3 = ref entity.Ref<Position>();                                             // ok — Add устанавливает гарантию

W.Query<All<Position, Velocity>>()
    .For((W.Entity e, ref Position p) => { ref var velocity = ref e.Ref<Velocity>(); });  // ok — оба через All<…>
```

#### Точечное подавление: постфикс `!`
```csharp
ref var pos = ref entity.Ref<Position>()!;                                             // ok — `!` подавляет FFSECS0042 на этом вызове
entity.Mut<Position>()!.X = 5;                                                         // ok — работает и для Mut/Read
```
Постфиксный null-forgiving (`!`) после вызова `Ref`/`Mut`/`Read` подавляет диагностику только для этого конкретного вызова. C# сохраняет value/ref-категорию выражения сквозь `!`, поэтому подавленный вызов всё равно возвращает по ссылке и валиден в `ref var` биндинге. Токен `!` принимается независимо от настроек nullable в проекте. После подавленного вызова dataflow записывает гарантию для `T`, так что последующие обращения к тому же компоненту на той же сущности уже не требуют повторного `!`. Подавление на приёмнике (`entity!.Ref<T>()`) **не распознаётся** — маркер должен относиться к конкретному доступу к компоненту. Поставляется CodeFix «Suppress FFSECS0042 with '!' after the call».

#### Ограничения
- Логические значения через промежуточный локал (`var ok = entity.Has<Position>(); if (ok) …`) не пробрасываются.
- Перегрузки `Components<T>.Ref/Mut/Read(entity)` в V1 не являются точкой проверки правила; проверяется только `entity.X<T>()`.
- Межпроцедурный анализ не выполняется: вспомогательный `void Use(W.Entity e) => e.Ref<T>();` будет предупреждать, пока внутри `Use` нет проверки.

___

### FFSECS0050
**Категория:** Correctness · **Severity:** Error · **CodeFix:** —

Компонент упомянут в запросе более одного раза — либо дубликат внутри фильтров одного типа (`All`+`All`, `None`+`None`, `Any`+`Any`, в т.ч. `*WithDisabled`/`*OnlyDisabled`-варианты), либо пересечение цепочки фильтров с `ref`/`in`-параметром лямбды или с component-generic-параметром `IQuery`-структуры.

#### Срабатывает
```csharp
foreach (var _ in W.Query<All<Health>, All<Health>>().Entities()) { }                       // FFSECS0050
W.Query<All<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0050 — фильтр ↔ лямбда
W.Query<All<Health>>().Write<Health>().For<MyWriteFn>();                                    // FFSECS0050 — фильтр ↔ IQuery generic
foreach (var _ in W.Query<All<Health>, AllOnlyDisabled<Health>>().Entities()) { }           // FFSECS0050 — базовый + disabled-вариант
```

___

### FFSECS0051
**Категория:** Correctness · **Severity:** Error · **CodeFix:** —

В запросе один и тот же компонент находится одновременно в `All<…>` и `None<…>` — результат запроса всегда пуст. Неявный вклад `All` от параметров лямбды и component-generic `IQuery`-структуры тоже учитывается.

#### Срабатывает
```csharp
foreach (var _ in W.Query<All<Health>, None<Health>>().Entities()) { }                       // FFSECS0051
W.Query<None<Health>>().For((W.Entity e, in Health hp) => { });                              // FFSECS0051 — лямбда подразумевает All
```

___

## Подавление диагностик

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

## Исходники

Все анализаторы лежат в `StaticEcs/Analyzers~/Src/Analyzers/*.cs`; code-fix'ы в `StaticEcs/Analyzers~/CodeFixes/`. Идентификаторы правил централизованы в `StaticEcs/Analyzers~/Shared/FFSECSIds.cs`.

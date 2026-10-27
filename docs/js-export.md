# `[JsExport]`: .NET types as script classes

Mark a type with `[JsExport]` and a Roslyn generator (the `RecursiveAI.OneEcma.Generators` package, analyzer-only) writes its binding at build
time. There is no reflection at run time, so it works under NativeAOT and trimming. Only exported members exist in script.

```csharp
[JsExport] public enum OrderStatus { Pending, Paid, Shipped }
[JsExport] public record LineItem(string Sku, int Quantity, decimal Price);
[JsExport] public record Order(int Id, List<LineItem> LineItems, OrderStatus Status = OrderStatus.Pending, string? Note = null);

[JsExport]
public class OrderApi
{
    public Order GetOrder(int id) { … }
    public Task<Order> FetchOrderAsync(int id) { … }
    public void ForEach(Action<Order> visit) { … }
}

// host
JsExports.InstallAll(engine.MainRealm);                                 // generated, in the project's root namespace
engine.MainRealm.DefineData(engine.Global, "api", engine.MainRealm.Wrap(new OrderApi()));
```

```js
const order = await api.fetchOrderAsync(2);       // a Promise
api.forEach(o => print(o.id));                    // a callback
api.addOrder({ id: 3, lineItems: [{ sku: "pen", quantity: 1, price: 1.5 }] });
```

A runnable version is `samples/OrderHost`. Reference the generator as an analyzer (`PackageReference` with `PrivateAssets="all"` for the package);
it reads the project's `RootNamespace` through `CompilerVisibleProperty`, which the package's `build/*.props` sets.

## What gets exported

| Declaration | In script |
|---|---|
| `class` (default) | A class: `new C(...)` for each public constructor, instance members on its prototype, statics on the constructor. Instances are **shared by reference**: script holds a handle to the .NET object. Derived exported classes inherit through the prototype chain. |
| `interface` | A prototype for values returned through it; no constructor. `Wrap(obj)` finds the prototype of the object's class first. |
| `static class` | A plain global object holding the static members. |
| `record`, `struct` | **Copied by value**: a script object becomes a new .NET value; a .NET value becomes a new plain object. Only data members (public readable properties and fields) take part. Override with `Kind = JsExportKind.Reference`/`Value`. |
| `enum` | A global object of member names; values cross as the member **name** (case-insensitive on the way in) or its number. |

Members of an exported class: public instance and static methods, properties (get/set accessors on the prototype; a read-only property has no
setter, so assignment is ignored in sloppy code and a TypeError in strict code), fields (as accessors), `const` fields (read-only values). Members of
`object` (`Equals`, `GetHashCode`, `GetType`, an unannotated `ToString`) are never exported. `[JsIgnore]` leaves a member out; `[JsExport(Name = "x")]` renames it.

**Names.** Members become camelCase (`GetOrder` → `getOrder`, `URL` → `url`, `ID` → `id`; a leading run of capitals lowers all but the last when a lowercase
follows: `HTMLPage` → `htmlPage`). Types keep their name. Enum member names are kept as written.

**Overloads.** Overloads share one script name. A call picks the most specific overload whose parameter kinds accept the arguments: integer, number,
string, boolean, bigint, array, plain object, function, host object of a given class, enum (string or number), `JsValue` (anything).
`int` is chosen over `double` for integral numbers. Overloads that accept the same kinds for some argument count are a build error (OE1004); a call that matches none
is a TypeError listing the overloads.

**Parameters.** Required, optional (`= default`, or a trailing nullable parameter), and `params T[]`. A missing or `undefined` optional argument takes the default.

**Supported types.** `bool`, all integer types (`long`/`ulong` cross as a number when exact, else a BigInt; a BigInt is accepted when it fits), `float`,
`double`, `decimal` (finite numbers), `System.Numerics.BigInteger` (BigInt), `string`, `char` (one-character string), enums, `T?` and nullable reference
types (`null` and `undefined` give null), `T[]`, `List<T>` and the usual interfaces (`IList`, `IReadOnlyList`, `IEnumerable`, `ICollection`) as arrays,
`Dictionary<string, T>` and its interfaces as plain objects, `JsValue` and `JsObject` (passed through), `Func<…>`/`Action<…>` up to six parameters
(script functions; a callback that returns a `Task`/`Task<T>` awaits the promise the script function returns), and other exported types.

**Returns.** `void` (undefined), values as above, `Task`, `Task<T>`, `ValueTask`, `ValueTask<T>` (a Promise), `IEnumerable<T>` (an iterator: items are
produced as script asks, `return()` disposes), `IAsyncEnumerable<T>` (an async iterator whose `next()` returns promises). An exception thrown synchronously by a task-returning method
rejects the promise instead of throwing.

**Conversions are strict.** A number is not accepted where a string is expected, and a string is not accepted where a number is: the failure says so and
suggests the conversion (`Number("7.25")`). By-value types read their properties by exported name; a property missing from the script object is an error
when the .NET member is required (a constructor parameter without default, a `required` member, a non-nullable property) and leaves the default otherwise.
`RejectUnknownProperties = true` makes an extra property an error that suggests the closest name.

## Boundary errors

Every error has the form `<Member>: <what> …` as a `TypeError` whose `Diagnostic` has the call as the primary span, a label stating the path and the finding,
`help:` lines, `= note: host function: <signature>` and a stack whose first frame is the .NET member:

```
error[TypeError]: Shop.OrderApi.AddOrder: argument 'order.lineItems[1].price' must be a finite number (System.Decimal), got string "7.25"
 --> case.js:6:1
  |
6 | api.addOrder({ id: 3, lineItems: lines });
  | ^^^^^^^^^^^^ `order.lineItems[1].price` is string "7.25", expected a finite number (System.Decimal)
  |
  = help: convert the string first: Number("7.25")
  = note: host function: Shop.OrderApi.AddOrder(Order order)
  = stack: Shop.OrderApi.AddOrder [.NET] ← <anonymous> (case.js:6)
```

| Situation | Headline after `<Member>: ` |
|---|---|
| wrong type | `argument '<path>' must be <expected>, got <actual>` |
| missing argument or property | `argument '<path>' is missing; expected <expected>` |
| unknown property (when rejected) | `argument '<path>' has no property '<name>'` + `help: did you mean '<name>'?` + the accepted names |
| enum name | `argument '<path>' must be one of 'A', 'B' (Enum), got string "…"` + did-you-mean |
| callback result | `the value returned by callback '<name>' must be <expected>, got <actual>` |
| no overload | `no overload accepts (<kinds>)` + one note per overload |
| wrong `this` | `called on an incompatible receiver (this is <actual>); expected a <Class> object` |

The path is the parameter name followed by `.property` and `[index]` steps. `<actual>` is `undefined`, `null`, `number 1.5`, `string "…"` (cut at 40 characters),
`boolean true`, `bigint 5n`, `a function`, `an array of 3 items`, `an object with keys a, b`, `an empty object` or `a symbol`. These messages are ours (V8 has no counterpart); the
name of the error and its place in `err.stack` follow V8.

Nesting of by-value types is limited to 64 levels in both directions, so a script object that contains itself or a cyclic .NET graph ends in an error (`TypeError` naming the path, or `RangeError` copying to script) instead of overflowing the stack.

A .NET exception thrown by an exported member arrives as `Error: <TypeName>: <message>` with `dotnetType`, and as `JsException.InnerException` in C#. Its report lists the .NET
frames (with file and line when debug information exists, method names only under NativeAOT) before the script frames.

## Build-time diagnostics

All are errors with a fix in the message.

| Id | Raised when | Fix |
|---|---|---|
| OE1001 | the declaration cannot be exported: generic type, `ref struct`, delegate, type nested in a non-accessible type | export a closed or non-generic type |
| OE1002 | a member shape has no script form: `ref`/`out`/`in` parameter, indexer, event, generic method, ref return | `[JsIgnore]`, or change the shape (events: take an `Action<…>`) |
| OE1003 | a member uses a type that cannot cross: `object`, an unexported class, `Dictionary<int, …>`, multi-dimensional array, pointer, a task as a parameter | add `[JsExport]` to the type, use `JsValue`, use `Dictionary<string, T>` |
| OE1004 | two overloads accept the same kinds of script values for some argument count | rename one with `[JsExport(Name = "…")]`, make kinds differ, or `[JsIgnore]` |
| OE1005 | two members (or two exported types) get the same script name | rename or ignore one |
| OE1006 | a by-value type cannot be built from a script object | add a parameterless constructor, or one whose parameters are named like its properties |
| OE1007 | a `Name` is not an identifier | letters, digits, `_`, `$`, not starting with a digit |
| OE1008 | `Kind` does not fit the type (a by-reference struct; a by-value interface, static or abstract class) | change `Kind` or the type |

A type with errors produces no binding and is left out of `JsExports.InstallAll`; the other types still generate.

## Generated code

For each exported type `T` the generator writes `TJsBinding` (same namespace; `internal` when the type is) with `JsType` (a `JsHostType` for `realm.Install`), and
`JsExports.g.cs` with `InstallAll(JsRealm)` and `Names` in the project's root namespace. By-value types also get `TryFromJs`/`ToJs`. The code is plain C# over the public
types in `host-api.md`; read it by building with `EmitCompilerGeneratedFiles=true`. Output order is deterministic. `tests/OneEcma.Generators.Tests/Snapshots` holds the
expected text for five shapes.

## JSON-Schema tools

`JsonSchemaTools` registers a function whose single argument is checked against a JSON Schema before the handler runs, with the same error form
(`createOrder: argument 'args.items[0].price' must be a number, got string "2"`, path root `args`):

```csharp
JsObject fn = JsonSchemaTools.Define(realm, "createOrder", schemaJson, arg => /* JsonElement in, JsonElement out */);
JsObject typed = JsonSchemaTools.Define(realm, "search", schemaJson, ToolJson.Default.SearchArgs, ToolJson.Default.SearchHitArray, args => Search(args));
JsObject slow = JsonSchemaTools.DefineAsync(realm, "slow", schemaJson, async arg => …);      // returns a Promise
JsObject checkedCall = JsonSchemaTools.Validated(realm, "name", schemaJson, existingFunction, thisValue);   // wrap an exported method
realm.DefineData(realm.Global, "createOrder", JsValue.FromObject(fn), JsAttributes.Method);
```

Typed overloads take `JsonTypeInfo<T>` from a `System.Text.Json` source-generated context, so they are AOT-safe; the context's naming policy decides the
.NET property names (use camel case to match schemas). The script value is converted to JSON by own enumerable properties (functions, symbols and `undefined`
properties are left out; non-finite numbers become `null`; nesting deeper than 64 is an error).

Supported keywords: `type` (also a list), `enum`, `const`, `properties`, `required`, `additionalProperties` (false or a schema), `items`, `minItems`, `maxItems`,
`minLength`, `maxLength` (UTF-16 units), `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `multipleOf`, `pattern` (.NET regular expression with a 200 ms
limit; a timeout counts as no match), `allOf`, `anyOf`, `oneOf`, `not`, local `$ref`. Everything else is ignored. A schema that is not JSON is an `ArgumentException`.

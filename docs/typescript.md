# Running TypeScript: type stripping

`ScriptEngine.StripTypes(source, options, cancellationToken)` turns TypeScript that uses only **erasable** syntax into JavaScript the engine runs. It is separate from auto-repair ([repair-table.md](repair-table.md)): repair acts on source that does not parse, stripping acts on source that is valid TypeScript. Nothing is type-checked; types are skipped, never resolved.

```csharp
using RecursiveAI.OneEcma.Syntax;   // TypeStripOptions, TypeStripResult

TypeStripResult r = ScriptEngine.StripTypes(File.ReadAllText("orders.ts"), new TypeStripOptions { FileName = "orders.ts" });
if (r.Success) { /* run r.Code as a module or script, named orders.ts */ }
else foreach (Diagnostic d in r.Diagnostics) Console.Error.WriteLine(d);
```

`TypeStripOptions.FileName` (default `<anonymous>`) names the file in reports; `Module` (default `true`) picks the goal the result is checked against. `TypeStripResult` has `Code` (null on failure), `Diagnostics` and `Success`. `.tsx` is not supported.

## Position preservation

Erased text becomes spaces. Every line terminator and every other character stays where it was, so a line and column in the result is the same line and column in the `.ts` file, and runtime reports, `err.stack` and syntax errors point at the original with no source map. Source with nothing to erase is returned as the same string.

Blanking alone would change meaning in a few places, so one token is written into erased text. The reference is ECMA-262 12.10 (automatic semicolon insertion): a statement that ends at a line break carries on into the next line when the next token can continue an expression.

- **A declaration that TypeScript reads as its own statement** (`type`, `interface`, `declare ...`, overload signatures, type-only `namespace`/`declare module`, `import type`/`export type`, and in a class body an erased member such as `declare x: T`, an index signature, an abstract or overload signature, or the modifiers at the start of a member) leaves only blanks. Without a guard `let x = f` newline `type T = number` newline `(g)()` would become `f(g)()`, and `r = tag` newline `` `x` `` would become a tagged template. When the token before the declaration can end an expression (an identifier, a literal, `)`, `]`, `}`, a template, `++`, `--`, `!`) and the token after it can continue one (`(`, `[`, a template, a binary or assignment operator, `.`, `?.`, `?`, `,`, `in`, `instanceof`, a regular expression or `/`), a `;` is written over the **first** column of the erased declaration. In a class body the same rule uses the next member's first token (`[`, `*`, ...).
- **A `;` in the source is never erased.** A declaration's own terminating `;` stays in the output (`type T = number;` becomes blanks followed by `;`), and a `;` that guards the next line (`;[1, 2].forEach(...)` after an `interface`) stays too; only semicolons inside the erased type itself (the separators of an interface body) are blanked. When the token after the declaration is such a `;`, nothing needs to be written.
- **A type-argument list at the end of a line** (`const g = f<string>` newline `[0]`): TypeScript ends the statement after the instantiation; a `;` is written over the last column of `<string>`. A non-null `!` needs nothing, because `a!` newline `[0]` and `a!` newline `(b)` continue in TypeScript exactly as they do blanked.
- **After `as T` / `satisfies T`.** If the next token starts a later line and is `(`, `[` or a template, JavaScript would join it to the expression now that the type no longer ends it (`b as T` newline `(c)` is two statements in TypeScript). A `;` is written over the last character of the erased text.
- **Arrow function return types that span a line break.** `(a): {` newline `v: number } => ...` would leave `=>` on a later line than `)`, which JavaScript forbids. The `=>` is moved up into the blanks right after `)` and its old place is blanked. If the colon is the last character on its line (`(a):` newline `number => a`) there is no room, and the file is refused with `ts-arrow-return-type-layout`; put the first line of the type after the colon.

## Erased

- Type annotations on variables, parameters (including `?`), return types, class fields, catch bindings and `this` parameters (removed with their comma).
- Type parameters and arguments on declarations and calls: `function f<T>()`, `f<T>(x)`, `new Map<string, number>()`, `f<T>\`x\``, class `extends B<T>`. `a < b > (c)` is read the TypeScript way, as a call with type arguments, as in a `.ts` file; a `<` followed by something that cannot end a type argument list stays a comparison (`a < b > c`, `a < b >= c`).
- Generic arrow functions (`<T,>(x: T) => x`, `async <T>(x) => x`); the `<T,>` form also works in `.ts`.
- `as T`, `as const`, `satisfies T` and the non-null `!`.
- `interface` and `type` declarations (exported or not, also `export default interface`), `declare` declarations (variables, functions, classes, enums, modules, `declare global`), `export as namespace X`, overload signatures (function declarations and methods without a body), abstract methods and fields, index signatures, `implements` clauses, `declare` fields.
- Namespaces and modules whose body holds only types (`interface`, `type`, `declare`, type-only imports and exports).
- `import type ...` and `export type ...` statements (whole statement). Inline `type` specifiers are removed with one comma; the statement stays, as `verbatimModuleSyntax` has it: `import { type A, b } from 'm'` keeps `b`, and `import { type A } from 'm'` becomes `import {  } from 'm'`, which still loads the module for its side effects. A module whose only import is `import type { X } from './y'` therefore has no import left.
- Modifiers `public`, `private`, `protected`, `readonly`, `override`, `abstract`, `declare` on class members; optional (`?`) and definite (`!:`) markers on fields.

Standard JavaScript passes through byte-identical.

## Refused

A refused construct makes `Success` false, returns no code and gives one diagnostic each (all of them in the file, in source order). The headline names the construct and says it is not erasable; there is a primary span, a label and a `help:` with the plain-JavaScript equivalent.

| Code | Construct | Why | Help |
|---|---|---|---|
| `ts-enum` | `enum`, `const enum` | compiles to an object built at run time | a `const` object with `as const` |
| `ts-namespace` | `namespace`/`module` that declares values | compiles to an IIFE | a module or a plain object |
| `ts-parameter-property` | `constructor(private x)` | compiles to field assignments | declare the field, assign in the constructor |
| `ts-import-equals` | `import x = require()`, `import A = B.C` | TypeScript's own import form | a standard import, or a `const` |
| `ts-export-assignment` | `export = x` | CommonJS export form | `export default` / named exports |
| `ts-angle-cast` | `<T>expr` | a type assertion in the old syntax | `expr as T` |
| `ts-parameter-decorator` | `m(@dec x)` | exists only under `experimentalDecorators` | pass the value explicitly |
| `ts-arrow-return-type-layout` | arrow return type that starts on the line after `:` | see above | start the type on the colon's line |

The reports are ordinary diagnostics: `error[SyntaxError]: TypeScript 'enum' is not erasable syntax`, a `-->` location in the `.ts` file, the primary `^^^` span, and `= help:`.

**Decorators.** The engine has no decorator syntax, so a decorator on a class or member passes through the stripper unchanged and the result then fails to parse; the parser's error is reported against the original text. Parameter decorators are refused as above because they can never be standard decorators.

## Syntax errors

If the source has a syntax error the stripper can see (for example `let b: string = ;`) it is reported as `SyntaxError` with V8's wording and a span in the original. After the erasing the result is parsed with the engine's parser; errors it finds there (a duplicate declaration, `import` in a script) are reported with spans in the `.ts` text, which are the same positions.

The reader that finds the types knows less JavaScript than the parser. If it stops on a construct it does not understand but the original source parses as JavaScript as it stands, the original is returned unchanged. On the first 19,200 non-negative test262 `language/` files that happens for four odd files (escaped `yield`/`await` labels, a generator named `yield`, `function(){} / {}`); every other file comes back as the same string without any stumble.

## Limits

Nesting is bounded (300 levels of statements, expressions and types, and the thread's stack is checked); deeper source gives a `Maximum nesting depth exceeded` diagnostic. Bracket-balanced type bodies (`{ ... }`, `[ ... ]`, `( ... )`) are skipped in constant time using a precomputed match. Cancellation is polled while reading.

## Choices where TypeScript leaves room

- `a < b > (c)` is a call with type arguments (TypeScript's rule), not two comparisons; the follow-token rule is: `(` or a template starts the call; `<`, `>`, `+`, `-` mean comparison; a token on a new line, or one that cannot start an expression, accepts type arguments (instantiation expressions such as `const g = f<string>;`); anything else means comparison.
- The `/` between a regular expression and a division is guessed from the tokens before it, looking at what a closing `}` and `)` closed.
- `import { type }` and `import { type as }` read `type` as a name; `type X` (two tokens) and four or more tokens (`type X as Y`) are type specifiers.
- Node's own type stripping gives the same text for the cases compared (annotations, generics, `as`, `satisfies`, interfaces, classes, overloads, `declare`, type imports and exports); the one visible difference is the arrow with a line break in its erased return type, where Node moves the `)` and this stripper moves the `=>`.

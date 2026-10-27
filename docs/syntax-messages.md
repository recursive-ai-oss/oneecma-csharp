# Syntax error wording

The headline of every syntax error and early error is word for word what V8 puts in `err.message`, so
scripts and error-reporting libraries that match on it keep working. The parser never invents a headline:
explanations go in the label, `help` and `note` lines of the report.

## How the wording was learned

By running Node as a black box. Each snippet in `testdata/syntax-messages/snippets.json` is compiled with
`new vm.Script` (`tools/node/record-syntax-messages.js`) and the message and line of the thrown
`SyntaxError` are recorded in `cases.json`. Node v22.22.1 and v24.22.1 print the same text for all 176
snippets. The test `SyntaxMessageTests` checks that our first error has the same headline and line, and that
snippets Node accepts produce no error.

## Rules the wording follows

- A token the grammar cannot accept at that point: `Unexpected token 'X'` for punctuators and keywords,
  `Unexpected identifier 'name'`, `Unexpected number`, `Unexpected string`, `Unexpected template string`,
  `Unexpected end of input`, `Unexpected reserved word` (`enum`, and `await` where it is reserved),
  `Unexpected strict mode reserved word`.
- A malformed token (bad number, escape, character, unterminated comment): `Invalid or unexpected token`;
  the more specific literal messages in the table below apply to numeric separators, escapes and octal forms.
- `@` is not a token in ES2026 (decorators are a later edition), so it is `Invalid or unexpected token`.
- A call missing its closing parenthesis: `missing ) after argument list`, positioned at the previous token.
- An unterminated template literal reports `Unexpected end of input`.
- `await x` outside an async function or module reports V8's `await is only valid in async functions and the
  top level bodies of modules`, positioned at `await`.
- Regular expression pattern errors are `Invalid regular expression: /pattern/flags: reason`. The pattern
  body is validated by the RegExp engine through `ParseOptions.RegExpValidator`; the parser itself only
  reports a missing closing `/` and invalid flags.
- The position of an error is the first token V8 could not accept, with these exceptions that follow V8:
  end-of-input errors point at the last line that has content; `try` without `catch` points at the closing
  brace of the block.

## Table

One row per distinct headline, with a snippet that produces it (all verified against Node).

| Headline | Example |
|---|---|
| `'arguments' is not allowed in class field initializer or static initialization block` | `class C { static { arguments } }` |
| `'super' keyword unexpected here` | `class A extends B { m(){ super() } }` |
| `A class may only have one constructor` | `class A { constructor(){} constructor(){} }` |
| `Async functions can only be declared at the top level or inside a block.` | `a: async function f(){}` |
| `Cannot use 'import.meta' outside a module` | `import.meta` |
| `Cannot use import statement outside a module` | `import x from 'y'` |
| `Class constructor may not be an accessor` | `class A { get constructor(){} }` |
| `Classes may not have a static property named 'prototype'` | `class A { static prototype(){} }` |
| `Decimals with leading zeros are not allowed in strict mode.` | `'use strict'; 08` |
| `Delete of an unqualified identifier in strict mode.` | `'use strict'; delete a` |
| `Duplicate __proto__ fields are not allowed in object literals` | `({__proto__:1,__proto__:2})` |
| `Duplicate parameter name not allowed in this context` | `function f(a,a){'use strict'}` |
| `Function statements require a function name` | `function(){}` |
| `Generators can only be declared at the top level or inside a block.` | `a: function* g(){}` |
| `Getter must not have any formal parameters.` | `({get a(x){}})` |
| `Identifier '#a' has already been declared` | `class A { #a; #a }` |
| `Identifier 'a' has already been declared` | `function f(){ let a; var a }` |
| `Identifier 'e' has already been declared` | `try {} catch(e){ let e }` |
| `Identifier 'x' has already been declared` | `let x; var x` |
| `Illegal 'use strict' directive in function with non-simple parameter list` | `function f(a=1){'use strict'}` |
| `Illegal await-expression in formal parameters of async function` | `async function f(a = await 1){}` |
| `Illegal break statement` | `break` |
| `Illegal continue statement: no surrounding iteration statement` | `continue` |
| `Illegal return statement` | `return` |
| `In non-strict mode code, functions can only be declared at top level, inside a block, or as the body of an if statement.` | `while(1) function f(){}` |
| `In strict mode code, functions can only be declared at top level or inside a block.` | `'use strict'; if(1) function f(){}` |
| `Invalid Unicode escape sequence` | `'\u12'` |
| `Invalid destructuring assignment target` | `({a:1} = 1)` |
| `Invalid hexadecimal escape sequence` | `'\x1'` |
| `Invalid left-hand side expression in postfix operation` | `1++` |
| `Invalid left-hand side expression in prefix operation` | `++a++` |
| `Invalid left-hand side in assignment` | `a?.b = 1` |
| `Invalid left-hand side in for-loop` | `for (a, b of c);` |
| `Invalid optional chain from new expression` | `new a?.b` |
| `Invalid or unexpected token` | `3in []` |
| `Invalid regular expression flags` | `/abc/gg` |
| `Invalid regular expression: /(/: Unterminated group` | `/(/` |
| `Invalid regular expression: /(?<=a)+/: Invalid quantifier` | `/(?<=a)+/` |
| `Invalid regular expression: /(?<a>.)(?<a>.)/: Duplicate capture group name` | `/(?<a>.)(?<a>.)/` |
| `Invalid regular expression: /[b-a]/: Range out of order in character class` | `/[b-a]/` |
| `Invalid regular expression: /\2(a)/u: Invalid escape` | `/\2(a)/u` |
| `Invalid regular expression: /\k<a>(?<b>.)/: Invalid named capture referenced` | `/\k<a>(?<b>.)/` |
| `Invalid regular expression: /\p{Foo}/u: Invalid property name` | `/\p{Foo}/u` |
| `Invalid regular expression: /a**/: Nothing to repeat` | `/a**/` |
| `Invalid regular expression: /{1}/u: Nothing to repeat` | `/{1}/u` |
| `Invalid regular expression: missing /` | `/abc` |
| `Invalid shorthand property initializer` | `({a=1})` |
| `Invalid tagged template on optional chain` | `a?.`x`` |
| `Keyword must not contain escaped characters` | `var \u0069f` |
| `Label 'label' has already been declared` | `label: label: 1` |
| `Lexical declaration cannot appear in a single-statement context` | `if (1) let x` |
| `Missing initializer in const declaration` | `const a;` |
| `Missing initializer in destructuring declaration` | `var [a];` |
| `Numeric separator can not be used after leading 0.` | `0_1` |
| `Numeric separators are not allowed at the end of numeric literals` | `1_` |
| `Octal escape sequences are not allowed in strict mode.` | `'use strict'; '\01'` |
| `Octal escape sequences are not allowed in template strings.` | ``\01`` |
| `Octal literals are not allowed in strict mode.` | `'use strict'; 010` |
| `Only one underscore is allowed as numeric separator` | `1__0` |
| `Private field '#a' must be declared in an enclosing class` | `this.#a` |
| `Private field '#b' must be declared in an enclosing class` | `class A { m(){ this.#b } }` |
| `Rest element must be last element` | `[...a,]=1` |
| `Rest parameter may not have a default initializer` | `function f(...a=1){}` |
| `Rest parameter must be last formal parameter` | `(...a,b)=>1` |
| `Setter function argument must not be a rest parameter` | `({set a(...x){}})` |
| `Setter must have exactly one formal parameter.` | `({set a(){}})` |
| `Strict mode code may not include a with statement` | `'use strict'; with(a){}` |
| `The left-hand side of a for-of loop may not be 'async'.` | `for (async of x);` |
| `Unary operator used immediately before exponentiation expression. Parenthesis must be used to disambiguate operator precedence` | `-1 ** 2` |
| `Undefined Unicode code-point` | `'\u{110000}'` |
| `Undefined label 'x'` | `break x` |
| `Unexpected end of input` | ``abc` |
| `Unexpected eval or arguments in strict mode` | `'use strict'; var eval` |
| `Unexpected identifier 'a'` | `({ async get a(){} })` |
| `Unexpected identifier 'b'` | `[a b]` |
| `Unexpected identifier 'bar'` | `foo bar` |
| `Unexpected identifier 'x'` | `for (let of x);` |
| `Unexpected identifier 'yield'` | `function* g(){ function yield(){} }` |
| `Unexpected number` | `1n.5` |
| `Unexpected reserved word` | `var enum` |
| `Unexpected strict mode reserved word` | `'use strict'; var let` |
| `Unexpected string` | `'a' 'b'` |
| `Unexpected token '('` | `(a,b)=>{} ()` |
| `Unexpected token ')'` | `)` |
| `Unexpected token '*'` | `function* g(){ yield\n* 1 }` |
| `Unexpected token ','` | `x = {a:1,,}` |
| `Unexpected token '=>'` | `a\n=> 1` |
| `Unexpected token '??'` | `a \|\| b ?? c` |
| `Unexpected token 'class'` | `if (1) class C {}` |
| `Unexpected token 'export'` | `export var a` |
| `Unexpected token '\|\|'` | `a ?? b \|\| c` |
| `Unexpected token '}'` | `}` |
| `Yield expression not allowed in formal parameter` | `function* g(a = yield){}` |
| `\8 and \9 are not allowed in strict mode.` | `'use strict'; '\8'` |
| `await is only valid in async functions and the top level bodies of modules` | `await 1` |
| `for-in loop variable declaration may not have an initializer.` | `'use strict'; for (var x = 1 in y);` |
| `for-of loop variable declaration may not have an initializer.` | `for (let x = 1 of y);` |
| `let is disallowed as a lexically bound name` | `let let = 1` |
| `missing ) after argument list` | `f(a b)` |
| `new.target expression is not allowed here` | `new.target` |

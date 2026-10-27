namespace RecursiveAI.OneEcma.Syntax;

/// <summary>
/// The V8 wording of syntax and early errors. Every entry was recorded by running Node on a script that
/// triggers it (see <c>testdata/syntax-messages</c>); none is taken from engine sources.
/// </summary>
internal static class Messages
{
    /// <summary>Fills <c>{0}</c>, <c>{1}</c>... in a message template.</summary>
    internal static string Format(string template, params string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            template = template.Replace("{" + i + "}", args[i], StringComparison.Ordinal);
        }

        return template;
    }

    internal const string InvalidToken = "Invalid or unexpected token";
    internal const string UnterminatedTemplate = "Unexpected end of input";
    internal const string UnterminatedRegExp = "Invalid regular expression: missing /";
    internal const string InvalidRegExpFlags = "Invalid regular expression flags";
    internal const string InvalidHexEscape = "Invalid hexadecimal escape sequence";
    internal const string InvalidUnicodeEscape = "Invalid Unicode escape sequence";
    internal const string UndefinedCodePoint = "Undefined Unicode code-point";
    internal const string NumericSeparatorAtEnd = "Numeric separators are not allowed at the end of numeric literals";
    internal const string NumericSeparatorDouble = "Only one underscore is allowed as numeric separator";
    internal const string NumericSeparatorAfterLeadingZero = "Numeric separator can not be used after leading 0.";
    internal const string OctalInTemplate = "Octal escape sequences are not allowed in template strings.";
    internal const string EightNineInTemplate = "\\8 and \\9 are not allowed in template strings.";
    internal const string OctalLiteralStrict = "Octal literals are not allowed in strict mode.";
    internal const string LeadingZeroStrict = "Decimals with leading zeros are not allowed in strict mode.";
    internal const string OctalEscapeStrict = "Octal escape sequences are not allowed in strict mode.";
    internal const string EightNineStrict = "\\8 and \\9 are not allowed in strict mode.";

    internal const string UnexpectedEnd = "Unexpected end of input";
    internal const string UnexpectedNumber = "Unexpected number";
    internal const string UnexpectedString = "Unexpected string";
    internal const string UnexpectedTemplate = "Unexpected template string";
    internal const string UnexpectedReserved = "Unexpected reserved word";
    internal const string UnexpectedStrictReserved = "Unexpected strict mode reserved word";
    internal const string UnexpectedEvalOrArguments = "Unexpected eval or arguments in strict mode";
    internal const string EscapedKeyword = "Keyword must not contain escaped characters";
    internal const string MissingParenAfterArgs = "missing ) after argument list";
    internal const string LetLexicallyBound = "let is disallowed as a lexically bound name";
    internal const string FunctionNameRequired = "Function statements require a function name";
    internal const string InvalidPostfixTarget = "Invalid left-hand side expression in postfix operation";
    internal const string InvalidPrefixTarget = "Invalid left-hand side expression in prefix operation";
    internal const string InvalidAssignTarget = "Invalid left-hand side in assignment";
    internal const string InvalidDestructuringTarget = "Invalid destructuring assignment target";
    internal const string InvalidForTarget = "Invalid left-hand side in for-loop";
    internal const string InvalidOptionalNew = "Invalid optional chain from new expression";
    internal const string InvalidOptionalTemplate = "Invalid tagged template on optional chain";
    internal const string InvalidShorthandInit = "Invalid shorthand property initializer";
    internal const string RestNotLastElement = "Rest element must be last element";
    internal const string RestNotLastParameter = "Rest parameter must be last formal parameter";
    internal const string RestParameterInit = "Rest parameter may not have a default initializer";
    internal const string DuplicateParameter = "Duplicate parameter name not allowed in this context";
    internal const string UseStrictNonSimple = "Illegal 'use strict' directive in function with non-simple parameter list";
    internal const string IllegalBreak = "Illegal break statement";
    internal const string IllegalContinue = "Illegal continue statement: no surrounding iteration statement";
    internal const string IllegalContinueTarget = "Illegal continue statement: '{0}' does not denote an iteration statement";
    internal const string IllegalReturn = "Illegal return statement";
    internal const string StrictWith = "Strict mode code may not include a with statement";
    internal const string StrictDelete = "Delete of an unqualified identifier in strict mode.";
    internal const string DeletePrivate = "Private fields can not be deleted";
    internal const string SingleStatementLexical = "Lexical declaration cannot appear in a single-statement context";
    internal const string SloppyFunctionPosition = "In non-strict mode code, functions can only be declared at top level, inside a block, or as the body of an if statement.";
    internal const string StrictFunctionPosition = "In strict mode code, functions can only be declared at top level or inside a block.";
    internal const string GeneratorPosition = "Generators can only be declared at the top level or inside a block.";
    internal const string AsyncFunctionPosition = "Async functions can only be declared at the top level or inside a block.";
    internal const string MissingCatchOrFinally = "Missing catch or finally after try";
    internal const string MissingConstInit = "Missing initializer in const declaration";
    internal const string MissingDestructuringInit = "Missing initializer in destructuring declaration";
    internal const string ForInOfInit = "{0} loop variable declaration may not have an initializer.";
    internal const string ForOfAsync = "The left-hand side of a for-of loop may not be 'async'.";
    internal const string ForOfLet = "The left-hand side of a for-of loop may not be 'let'.";
    internal const string MultipleDefaults = "More than one default clause in switch statement";
    internal const string NewlineAfterThrow = "Illegal newline after throw";
    internal const string UnaryBeforeExponent = "Unary operator used immediately before exponentiation expression. Parenthesis must be used to disambiguate operator precedence";
    internal const string DuplicateProto = "Duplicate __proto__ fields are not allowed in object literals";
    internal const string GetterParams = "Getter must not have any formal parameters.";
    internal const string SetterParams = "Setter must have exactly one formal parameter.";
    internal const string SetterRest = "Setter function argument must not be a rest parameter";
    internal const string DuplicateConstructor = "A class may only have one constructor";
    internal const string ConstructorAccessor = "Class constructor may not be an accessor";
    internal const string ConstructorSpecial = "Class constructor may not be a{0}";
    internal const string StaticPrototype = "Classes may not have a static property named 'prototype'";
    internal const string FieldConstructor = "Classes may not have a field named 'constructor'";
    internal const string SuperUnexpected = "'super' keyword unexpected here";
    internal const string NewTargetUnexpected = "new.target expression is not allowed here";
    internal const string ImportMetaOutsideModule = "Cannot use 'import.meta' outside a module";
    internal const string ImportOutsideModule = "Cannot use import statement outside a module";
    internal const string ArgumentsInClassField = "'arguments' is not allowed in class field initializer or static initialization block";
    internal const string YieldInParameter = "Yield expression not allowed in formal parameter";
    internal const string AwaitInParameter = "Illegal await-expression in formal parameters of async function";
    internal const string AwaitOutsideAsync = "await is only valid in async functions and the top level bodies of modules";
    internal const string DuplicateExport = "Duplicate export of '{0}'";
    internal const string UndefinedExport = "Export '{0}' is not defined in module";
    internal const string UndefinedLabel = "Undefined label '{0}'";
    internal const string DuplicateLabel = "Label '{0}' has already been declared";
    internal const string DuplicateBinding = "Identifier '{0}' has already been declared";
    internal const string UndeclaredPrivate = "Private field '{0}' must be declared in an enclosing class";
    internal const string InvalidRegExpPrefix = "Invalid regular expression: /{0}/{1}: {2}";
    internal const string DepthLimit = "Maximum nesting depth exceeded";
    internal const string InvalidLhsInFor = "Invalid left-hand side in for-in loop: Must have a single binding.";
    internal const string ClassHeritageInvalid = "Unexpected token";
    internal const string LabelledFunctionStrict = "In strict mode code, functions can only be declared at top level or inside a block.";
    internal const string ImportAttributeDuplicate = "Import attribute has duplicate key '{0}'";
    internal const string DeclarationInSwitchLabel = "Lexical declaration cannot appear in a single-statement context";
    internal const string IllegalStrictFunctionName = "Unexpected eval or arguments in strict mode";
    internal const string OptionalChainAssign = "Invalid left-hand side in assignment";
    internal const string NonSimpleUseStrictName = "Illegal 'use strict' directive in function with non-simple parameter list";
    internal const string AsyncArrowNewline = "Unexpected token '=>'";
    internal const string ExportBinding = "Unexpected token";
}

/// <summary>Reserved word lookup (ECMA-262 12.7.2).</summary>
internal static class Keywords
{
    private static readonly Dictionary<string, TokenType> Table = new(StringComparer.Ordinal)
    {
        ["break"] = TokenType.Break, ["case"] = TokenType.Case, ["catch"] = TokenType.Catch,
        ["class"] = TokenType.Class, ["const"] = TokenType.Const, ["continue"] = TokenType.Continue,
        ["debugger"] = TokenType.Debugger, ["default"] = TokenType.Default, ["delete"] = TokenType.Delete,
        ["do"] = TokenType.Do, ["else"] = TokenType.Else, ["enum"] = TokenType.Enum,
        ["export"] = TokenType.Export, ["extends"] = TokenType.Extends, ["false"] = TokenType.False,
        ["finally"] = TokenType.Finally, ["for"] = TokenType.For, ["function"] = TokenType.Function,
        ["if"] = TokenType.If, ["import"] = TokenType.Import, ["in"] = TokenType.In,
        ["instanceof"] = TokenType.Instanceof, ["new"] = TokenType.New, ["null"] = TokenType.Null,
        ["return"] = TokenType.Return, ["super"] = TokenType.Super, ["switch"] = TokenType.Switch,
        ["this"] = TokenType.This, ["throw"] = TokenType.Throw, ["true"] = TokenType.True,
        ["try"] = TokenType.Try, ["typeof"] = TokenType.Typeof, ["var"] = TokenType.Var,
        ["void"] = TokenType.Void, ["while"] = TokenType.While, ["with"] = TokenType.With,
    };

    /// <summary>The token type of a reserved word, or <see cref="TokenType.EndOfInput"/> when <paramref name="name"/> is not one.</summary>
    internal static TokenType Lookup(string name) => name.Length is >= 2 and <= 10 && Table.TryGetValue(name, out TokenType t) ? t : TokenType.EndOfInput;

    /// <summary>True for a reserved word's text.</summary>
    internal static bool IsReserved(string name) => Lookup(name) != TokenType.EndOfInput;

    /// <summary>ECMA-262 12.7.2 FutureReservedWord in strict mode code.</summary>
    internal static bool IsStrictReserved(string name) => name is "implements" or "interface" or "let" or "package" or "private" or "protected" or "public" or "static" or "yield";
}

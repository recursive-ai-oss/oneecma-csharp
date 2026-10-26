using RecursiveAI.OneEcma.Diagnostics;
using System.Globalization;
using RecursiveAI.OneEcma.Text;

namespace RecursiveAI.OneEcma.Syntax;

internal sealed partial class Parser
{
    private readonly Dictionary<string, Span> _exportedNames = new(StringComparer.Ordinal);
    private readonly List<(string Name, Span Span)> _exportedLocals = [];

    private bool AtModuleTopLevel(StmtCtx ctx) => _module && ctx == StmtCtx.List && _scope.Kind == ScopeKind.Module;

    private static bool IsWellFormedString(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]))
            {
                if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                {
                    return false;
                }

                i++;
            }
            else if (char.IsLowSurrogate(s[i]))
            {
                return false;
            }
        }

        return true;
    }

    // ---- import ----------------------------------------------------------------------------

    private ImportDeclaration ParseImportDeclarationStatement(StmtCtx ctx)
    {
        int start = _tok.Start;
        Token imp = _tok;
        if (!_module)
        {
            Report("import-outside-module", Messages.ImportOutsideModule, imp.Start, imp.End);
        }
        else if (!AtModuleTopLevel(ctx))
        {
            Unexpected();
        }

        Advance();
        var specifiers = new List<ImportSpecifier>();
        if (!Is(TokenType.String))
        {
            if (Is(TokenType.Identifier))
            {
                Identifier local = ParseImportBinding();
                specifiers.Add(new ImportSpecifier(local.Start, local.End, ImportSpecifierKind.Default, null, local));
                if (Eat(TokenType.Comma))
                {
                    ParseImportClauseRest(specifiers);
                }
            }
            else
            {
                ParseImportClauseRest(specifiers);
            }

            if (IsContextual("from"))
            {
                Advance();
            }
            else
            {
                Diagnostic? d = Unexpected();
                NoteInserted(d, _tok.Start, "from");
            }
        }

        StringLiteral source = ParseModuleSpecifier();
        List<ImportAttribute> attrs = ParseImportAttributes();
        ConsumeSemicolon();
        return new ImportDeclaration(start, _prevEnd, specifiers, source, attrs);
    }

    private void ParseImportClauseRest(List<ImportSpecifier> specifiers)
    {
        if (Is(TokenType.Star))
        {
            int s = _tok.Start;
            Advance();
            if (IsContextual("as"))
            {
                Advance();
            }
            else
            {
                Unexpected();
            }

            Identifier local = ParseImportBinding();
            specifiers.Add(new ImportSpecifier(s, local.End, ImportSpecifierKind.Namespace, null, local));
            return;
        }

        if (!Is(TokenType.LBrace))
        {
            Unexpected();
            return;
        }

        Advance();
        while (!Is(TokenType.RBrace) && !Is(TokenType.EndOfInput))
        {
            int before = _tok.Start;
            Token nameTok = _tok;
            Node imported;
            bool isString = false;
            if (Is(TokenType.String))
            {
                Advance();
                CheckStringToken(nameTok);
                if (!IsWellFormedString(nameTok.Text!))
                {
                    Report("ill-formed-string", "Invalid module export name: contains unpaired surrogate", nameTok.Start, nameTok.End);
                }

                imported = new StringLiteral(nameTok.Start, nameTok.End, nameTok.Text!);
                isString = true;
            }
            else if (IsIdentifierName(nameTok))
            {
                Advance();
                imported = new Identifier(nameTok.Start, nameTok.End, NameOf(nameTok));
            }
            else
            {
                Unexpected();
                break;
            }

            Identifier local;
            if (IsContextual("as"))
            {
                Advance();
                local = ParseImportBinding();
            }
            else
            {
                if (isString || nameTok.Type != TokenType.Identifier)
                {
                    Diagnostic? d = Unexpected();
                    NoteInserted(d, _tok.Start, "as");
                    local = new Identifier(nameTok.Start, nameTok.End, "");
                }
                else
                {
                    CheckBindingIdentifier(nameTok);
                    local = new Identifier(nameTok.Start, nameTok.End, nameTok.Text!);
                    DeclareLexical(local.Name, BindingKind.Import, local.Span);
                }
            }

            specifiers.Add(new ImportSpecifier(nameTok.Start, _prevEnd, ImportSpecifierKind.Named, imported, local));
            if (!Is(TokenType.RBrace) && !Eat(TokenType.Comma))
            {
                Diagnostic? d = Unexpected();
                NoteInserted(d, _prevEnd, ",");
                if (_tok.Start == before)
                {
                    break;
                }
            }
        }

        Expect(TokenType.RBrace);
    }

    private Identifier ParseImportBinding()
    {
        Token t = _tok;
        if (t.Type != TokenType.Identifier)
        {
            Unexpected();
            return new Identifier(t.Start, t.Start, "");
        }

        CheckBindingIdentifier(t);
        Advance();
        var id = new Identifier(t.Start, t.End, t.Text!);
        DeclareLexical(id.Name, BindingKind.Import, id.Span);
        return id;
    }

    private StringLiteral ParseModuleSpecifier()
    {
        Token t = _tok;
        if (t.Type != TokenType.String)
        {
            Unexpected();
            return new StringLiteral(t.Start, t.Start, "");
        }

        Advance();
        CheckStringToken(t);
        return new StringLiteral(t.Start, t.End, t.Text!);
    }

    // Import attributes: `with { type: "json" }` (ECMA-262 16.2.2).
    private List<ImportAttribute> ParseImportAttributes()
    {
        var attrs = new List<ImportAttribute>();
        if (!(Is(TokenType.With) && !_tok.NewlineBefore))
        {
            return attrs;
        }

        Advance();
        Expect(TokenType.LBrace);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (!Is(TokenType.RBrace) && !Is(TokenType.EndOfInput))
        {
            int before = _tok.Start;
            Token k = _tok;
            Node key;
            string keyText;
            if (k.Type == TokenType.String)
            {
                Advance();
                key = new StringLiteral(k.Start, k.End, k.Text!);
                keyText = k.Text!;
            }
            else if (IsIdentifierName(k))
            {
                Advance();
                key = new Identifier(k.Start, k.End, NameOf(k));
                keyText = NameOf(k);
            }
            else
            {
                Unexpected();
                break;
            }

            if (!seen.Add(keyText))
            {
                Report("duplicate-attribute", Messages.Format(Messages.ImportAttributeDuplicate, keyText), k.Start, k.End);
            }

            Expect(TokenType.Colon);
            StringLiteral value = ParseModuleSpecifier();
            attrs.Add(new ImportAttribute(k.Start, _prevEnd, key, value));
            if (!Is(TokenType.RBrace) && !Eat(TokenType.Comma))
            {
                Unexpected();
                if (_tok.Start == before)
                {
                    break;
                }
            }
        }

        Expect(TokenType.RBrace);
        return attrs;
    }

    // ---- export ----------------------------------------------------------------------------

    private void AddExportedName(string name, Span span)
    {
        if (!_exportedNames.TryAdd(name, span))
        {
            Diagnostic? d = ReportSpan("duplicate-export", Messages.Format(Messages.DuplicateExport, name), span);
            d?.SecondaryLabels.Add(new Label(_exportedNames[name], "first exported here"));
        }
    }

    private Node ParseExportDeclarationStatement(StmtCtx ctx)
    {
        int start = _tok.Start;
        if (!AtModuleTopLevel(ctx))
        {
            // Not valid here: say so, drop the keyword and parse what follows as an ordinary statement.
            Diagnostic? d = Unexpected();
            NoteSkipped(d, _tok.Start, _tok.End);
            Advance();
            return new ErrorStatement(start, _prevEnd);
        }

        Advance(); // export
        switch (_tok.Type)
        {
            case TokenType.Star:
                return ParseExportAll(start);
            case TokenType.LBrace:
                return ParseExportList(start);
            case TokenType.Default:
                return ParseExportDefault(start);
            case TokenType.Var:
            case TokenType.Const:
            case TokenType.Function:
            case TokenType.Class:
                return ParseExportedDeclaration(start);
            case TokenType.Identifier when IsContextual("let") || IsContextual("async"):
                return ParseExportedDeclaration(start);
            default:
                Unexpected();
                return new ErrorStatement(start, _prevEnd);
        }
    }

    private ExportNamedDeclaration ParseExportedDeclaration(int start)
    {
        Node decl = ParseStatement(StmtCtx.List);
        switch (decl)
        {
            case VariableDeclaration vd:
                var ids = new List<Identifier>();
                foreach (VariableDeclarator d in vd.Declarations)
                {
                    CollectBoundIdentifiers(d.Id, ids);
                }

                foreach (Identifier id in ids)
                {
                    AddExportedName(id.Name, id.Span);
                }

                break;
            case FunctionDeclaration { Function.Id: { } fid }:
                AddExportedName(fid.Name, fid.Span);
                break;
            case ClassDeclaration { Class.Id: { } cid }:
                AddExportedName(cid.Name, cid.Span);
                break;
        }

        return new ExportNamedDeclaration(start, _prevEnd, decl, [], null, []);
    }

    private ExportAllDeclaration ParseExportAll(int start)
    {
        Advance(); // *
        Node? exported = null;
        if (IsContextual("as"))
        {
            Advance();
            exported = ParseModuleExportName();
            if (exported is Identifier eid)
            {
                AddExportedName(eid.Name, eid.Span);
            }
            else if (exported is StringLiteral esl)
            {
                AddExportedName(esl.Value, esl.Span);
            }
        }

        ExpectContextual("from");
        StringLiteral source = ParseModuleSpecifier();
        List<ImportAttribute> attrs = ParseImportAttributes();
        ConsumeSemicolon();
        return new ExportAllDeclaration(start, _prevEnd, exported, source, attrs);
    }

    private void ExpectContextual(string word)
    {
        if (IsContextual(word))
        {
            Advance();
            return;
        }

        Diagnostic? d = Unexpected();
        NoteInserted(d, _tok.Start, word);
    }

    private Node ParseModuleExportName()
    {
        Token t = _tok;
        if (t.Type == TokenType.String)
        {
            Advance();
            CheckStringToken(t);
            if (!IsWellFormedString(t.Text!))
            {
                Report("ill-formed-string", "Invalid module export name: contains unpaired surrogate", t.Start, t.End);
            }

            return new StringLiteral(t.Start, t.End, t.Text!);
        }

        if (IsIdentifierName(t))
        {
            Advance();
            return new Identifier(t.Start, t.End, NameOf(t));
        }

        Unexpected();
        return new Identifier(t.Start, t.Start, "");
    }

    private ExportNamedDeclaration ParseExportList(int start)
    {
        Advance(); // {
        var specs = new List<ExportSpecifier>();
        var localTokens = new List<Token>();
        while (!Is(TokenType.RBrace) && !Is(TokenType.EndOfInput))
        {
            int before = _tok.Start;
            Token local = _tok;
            Node localNode = ParseModuleExportName();
            Node exported = localNode;
            if (IsContextual("as"))
            {
                Advance();
                exported = ParseModuleExportName();
            }

            specs.Add(new ExportSpecifier(local.Start, _prevEnd, localNode, exported));
            localTokens.Add(local);
            if (!Is(TokenType.RBrace) && !Eat(TokenType.Comma))
            {
                Diagnostic? d = Unexpected();
                NoteInserted(d, _prevEnd, ",");
                if (_tok.Start == before)
                {
                    break;
                }
            }
        }

        Expect(TokenType.RBrace);
        StringLiteral? source = null;
        List<ImportAttribute> attrs = [];
        if (IsContextual("from"))
        {
            Advance();
            source = ParseModuleSpecifier();
            attrs = ParseImportAttributes();
        }
        else
        {
            // Without `from` the local names are references to this module's bindings (16.2.3.1).
            for (int i = 0; i < specs.Count; i++)
            {
                Token lt = localTokens[i];
                if (lt.Type == TokenType.String || lt.Type >= TokenType.Break)
                {
                    Report("unexpected-token", UnexpectedMessage(lt), lt.Start, lt.End);
                }
                else if (lt.Type == TokenType.Identifier)
                {
                    CheckIdentifierReference(lt);
                    _exportedLocals.Add((lt.Text!, new Span(lt.Start, lt.End)));
                }
            }
        }

        foreach (ExportSpecifier s in specs)
        {
            string? name = s.Exported is Identifier eid ? eid.Name : (s.Exported as StringLiteral)?.Value;
            if (name is not null)
            {
                AddExportedName(name, s.Exported.Span);
            }
        }

        ConsumeSemicolon();
        return new ExportNamedDeclaration(start, _prevEnd, null, specs, source, attrs);
    }

    private ExportDefaultDeclaration ParseExportDefault(int start)
    {
        Token dflt = _tok;
        AddExportedName("default", new Span(dflt.Start, dflt.End));
        Advance();
        int dstart = _tok.Start;
        Node decl;
        if (Is(TokenType.Function))
        {
            FunctionDeclaration f = ParseFunctionDeclaration(dstart, isAsync: false, allowAnonymous: true);
            decl = f;
        }
        else if (IsContextual("async") && PeekToken().Type == TokenType.Function && !PeekToken().NewlineBefore)
        {
            Advance();
            decl = ParseFunctionDeclaration(dstart, isAsync: true, allowAnonymous: true);
        }
        else if (Is(TokenType.Class))
        {
            ClassNode c = ParseClass(declaration: true, allowAnonymous: true);
            decl = new ClassDeclaration(dstart, c.End, c);
        }
        else
        {
            decl = ParseAssignment(noIn: false);
            ConsumeSemicolon();
        }

        return new ExportDefaultDeclaration(start, _prevEnd, decl);
    }

    private void CheckExports()
    {
        foreach (var (name, span) in _exportedLocals)
        {
            if (_scope.Lookup(name) is null)
            {
                ReportSpan("undefined-export", Messages.Format(Messages.UndefinedExport, name), span);
            }
        }
    }
}

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace BlitzCS.Compiler;

internal readonly record struct SourcePosition(int Line, int Column, string? File = null);
internal enum TokenKind { Eof, Separator, Identifier, Integer, Hex, Binary, Float, String, Abs, Sgn, Plus, Minus, Star, Slash, Mod, Power, And, Or, Xor, Not, Shl, Shr, Sar, Less, Greater, LessEqual, GreaterEqual, NotEqual, Open, Close, Equal, Percent, Hash, Dollar, Comma, Dot, OpenSquare, CloseSquare, Backslash, Bridge, CSharp, Module }
internal sealed record Token(TokenKind Kind, string Text, SourcePosition Position, string? Original = null);
internal abstract record Expression(SourcePosition Position);
internal sealed record Literal(object Value, SourcePosition At) : Expression(At);
internal sealed record Variable(string Name, SourcePosition At) : Expression(At);
internal sealed record ArrayAccess(string Name, IReadOnlyList<Expression> Indices, SourcePosition At) : Expression(At);
internal sealed record ObjectOperation(string Operation, string? TypeName, Expression? Value, SourcePosition At) : Expression(At);
internal sealed record VectorAccess(Expression Owner, Expression Index, SourcePosition At) : Expression(At);
internal sealed record FieldAccess(Expression Owner, string Name, SourcePosition At) : Expression(At);
internal sealed record Call(string Name, IReadOnlyList<Expression> Arguments, bool IsCSharp, SourcePosition At) : Expression(At);
internal sealed record Unary(TokenKind Operator, Expression Operand, SourcePosition At) : Expression(At);
internal sealed record Binary(Expression Left, TokenKind Operator, Expression Right, SourcePosition At) : Expression(At);
internal abstract record Statement(SourcePosition Position);
internal sealed record FieldDeclaration(string Name, Expression? Bound, SourcePosition Position, Expression? Initializer = null);
internal sealed record TypeDeclaration(string Name, IReadOnlyList<FieldDeclaration> Fields, SourcePosition At) : Statement(At);
internal sealed record VectorAssignment(VectorAccess Target, Expression Value, SourcePosition At) : Statement(At);
internal sealed record FieldAssignment(FieldAccess Target, Expression Value, SourcePosition At) : Statement(At);
internal sealed record Delete(Expression? Value, string? TypeName, SourcePosition At) : Statement(At);
internal sealed record Insert(Expression Value, Expression Anchor, bool Before, SourcePosition At) : Statement(At);
internal sealed record ForEach(Expression Target, string TypeName, IReadOnlyList<Statement> Body, SourcePosition At) : Statement(At);
internal sealed record Print(Expression Value, SourcePosition At) : Statement(At);
internal sealed record ArrayAssignment(ArrayAccess Target, Expression Value, SourcePosition At) : Statement(At);
internal sealed record Dim(string Name, IReadOnlyList<Expression> Bounds, SourcePosition At) : Statement(At);
internal sealed record Assignment(string Name, Expression Value, SourcePosition At) : Statement(At);
internal sealed record ConstantDeclaration(string Name, Expression Value, SourcePosition At) : Statement(At);
internal sealed record DataDeclaration(IReadOnlyList<Expression> Values, SourcePosition At) : Statement(At);
internal sealed record DataLabel(string Name, SourcePosition At) : Statement(At);
internal sealed record ReadData(IReadOnlyList<Expression> Targets, SourcePosition At) : Statement(At);
internal sealed record RestoreData(string? Label, SourcePosition At) : Statement(At);
internal sealed record Declaration(string Name, Expression? Value, bool IsGlobal, SourcePosition At, Expression? Bound = null) : Statement(At);
internal sealed record Function(string Name, IReadOnlyList<string> Parameters, IReadOnlyList<Expression?> Defaults, IReadOnlyList<Statement> Body, SourcePosition At) : Statement(At);
internal sealed record Return(Expression? Value, SourcePosition At) : Statement(At);
internal sealed record CSharp(string Code, bool IsModule, SourcePosition At) : Statement(At);
internal sealed record CallStatement(Call Value, SourcePosition At) : Statement(At);
internal sealed record End(SourcePosition At) : Statement(At);
internal sealed record If(Expression Condition, IReadOnlyList<Statement> Then, IReadOnlyList<Statement> Else, SourcePosition At) : Statement(At);
internal sealed record While(Expression Condition, IReadOnlyList<Statement> Body, SourcePosition At) : Statement(At);
internal sealed record Repeat(IReadOnlyList<Statement> Body, Expression? Until, SourcePosition At) : Statement(At);
internal sealed record For(Expression Target, Expression From, Expression To, Expression Step, IReadOnlyList<Statement> Body, SourcePosition At) : Statement(At);
internal sealed record SelectCase(IReadOnlyList<Expression> Values, IReadOnlyList<Statement> Body, SourcePosition Position);
internal sealed record Select(Expression Value, IReadOnlyList<SelectCase> Cases, IReadOnlyList<Statement> Default, SourcePosition At) : Statement(At);
internal sealed record Exit(SourcePosition At) : Statement(At);

internal sealed class Lexer(string source, string sourcePath)
{
    private int offset;
    private int line = 1;
    private int column = 1;
    private char Current => offset < source.Length ? source[offset] : '\0';
    private void Advance() { offset++; column++; }
    private SourcePosition Position => new(line, column, sourcePath);
    private BasicDiagnosticException Error(SourcePosition at, string message) => new(at.File ?? sourcePath, at.Line, at.Column, message);

    private void AdvanceTo(int end)
    {
        while (offset < end)
        {
            var c = Current;
            Advance();
            if (c == '\r')
            {
                if (Current == '\n' && offset < end) Advance();
                line++; column = 1;
            }
            else if (c == '\n') { line++; column = 1; }
        }
    }

    private Token ReadCSharp(SourcePosition opening)
    {
        AdvanceTo(offset + 4);
        var module = false;
        var word = offset;
        while (word < source.Length && source[word] is ' ' or '\t') word++;
        if (source.AsSpan(word).StartsWith("module", StringComparison.Ordinal) &&
            (word + 6 == source.Length || char.IsWhiteSpace(source[word + 6])))
        {
            module = true;
            AdvanceTo(word + 6);
        }
        var start = offset;
        var bodyPosition = Position;
        // Roslyn's lexer treats strings (including interpolation/raw strings) and comments
        // atomically. Only adjacent punctuation tokens outside those constructs close a block.
        foreach (var token in SyntaxFactory.ParseTokens(source[start..], options: new CSharpParseOptions(LanguageVersion.CSharp14)))
        {
            if (!token.IsKind(SyntaxKind.QuestionToken)) continue;
            var end = start + token.SpanStart;
            if (end + 1 >= source.Length || source[end + 1] != '>') continue;
            var code = source[start..end];
            AdvanceTo(end + 2);
            return new(module ? TokenKind.Module : TokenKind.CSharp, code, bodyPosition);
        }
        throw Error(opening, "Unterminated C# block; expected '?>'.");
    }

    internal List<Token> Tokenize()
    {
        var result = new List<Token>();
        while (offset < source.Length)
        {
            var at = Position;
            var c = Current;
            if (c is ' ' or '\t' or '\f') { Advance(); continue; }
            if (source.AsSpan(offset).StartsWith("<?cs", StringComparison.Ordinal) &&
                (offset + 4 == source.Length || char.IsWhiteSpace(source[offset + 4])))
            {
                result.Add(ReadCSharp(at)); continue;
            }
            if (c == ';') { while (offset < source.Length && Current is not '\r' and not '\n') Advance(); continue; }
            if (c is '\r' or '\n')
            {
                Advance();
                if (c == '\r' && Current == '\n') Advance();
                line++; column = 1;
                result.Add(new(TokenKind.Separator, "\n", at));
                continue;
            }
            var start = offset;
            if (c == '%' && offset + 1 < source.Length && source[offset + 1] is '0' or '1')
            {
                Advance();
                while (Current is '0' or '1') Advance();
                result.Add(new(TokenKind.Binary, source[start..offset], at));
                continue;
            }
            if (c == '$' && offset + 1 < source.Length && Uri.IsHexDigit(source[offset + 1]))
            {
                Advance();
                while (Uri.IsHexDigit(Current)) Advance();
                result.Add(new(TokenKind.Hex, source[start..offset], at));
                continue;
            }
            if (char.IsAsciiLetter(c))
            {
                while (char.IsAsciiLetterOrDigit(Current) || Current == '_') Advance();
                var original = source[start..offset];
                var text = original.ToLowerInvariant();
                var keyword = text switch { "abs" => TokenKind.Abs, "sgn" => TokenKind.Sgn, "and" => TokenKind.And, "or" => TokenKind.Or, "xor" => TokenKind.Xor, "not" => TokenKind.Not, "shl" => TokenKind.Shl, "shr" => TokenKind.Shr, "sar" => TokenKind.Sar, "mod" => TokenKind.Mod, _ => TokenKind.Identifier };
                result.Add(new(keyword, text, at, original));
                continue;
            }
            if (char.IsAsciiDigit(c) || c == '.' && offset + 1 < source.Length && char.IsAsciiDigit(source[offset + 1]))
            {
                while (char.IsAsciiDigit(Current)) Advance();
                var floating = false;
                if (Current == '.') { floating = true; Advance(); while (char.IsAsciiDigit(Current)) Advance(); }
                if (Current is 'e' or 'E')
                {
                    floating = true; Advance();
                    if (Current is '+' or '-') Advance();
                    if (!char.IsAsciiDigit(Current)) throw Error(at, "Expected digits in floating-point exponent.");
                    while (char.IsAsciiDigit(Current)) Advance();
                }
                result.Add(new(floating ? TokenKind.Float : TokenKind.Integer, source[start..offset], at));
                continue;
            }
            if (c == '"')
            {
                Advance(); start = offset;
                while (offset < source.Length && Current is not '"' and not '\r' and not '\n') Advance();
                if (Current != '"') throw Error(at, "Unterminated string literal.");
                result.Add(new(TokenKind.String, source[start..offset], at));
                Advance(); continue;
            }
            if (c == ':' && offset + 1 < source.Length && source[offset + 1] == ':')
            {
                Advance(); Advance(); result.Add(new(TokenKind.Bridge, "::", at)); continue;
            }
            if (c is '<' or '>' || c == '=' && offset + 1 < source.Length && source[offset + 1] is '<' or '>')
            {
                Advance();
                var relation = c == '<' ? TokenKind.Less : TokenKind.Greater;
                if (c == '=') { relation = Current == '<' ? TokenKind.LessEqual : TokenKind.GreaterEqual; Advance(); }
                else if (Current == '=') { Advance(); relation = c == '<' ? TokenKind.LessEqual : TokenKind.GreaterEqual; }
                else if (c == '<' && Current == '>' || c == '>' && Current == '<') { Advance(); relation = TokenKind.NotEqual; }
                result.Add(new(relation, source[start..offset], at)); continue;
            }
            var kind = c switch
            {
                ':' => TokenKind.Separator, '+' => TokenKind.Plus, '-' => TokenKind.Minus,
                '^' => TokenKind.Power, '*' => TokenKind.Star, '/' => TokenKind.Slash, '(' => TokenKind.Open,
                ')' => TokenKind.Close, '[' => TokenKind.OpenSquare, ']' => TokenKind.CloseSquare, '=' => TokenKind.Equal, '%' => TokenKind.Percent,
                '#' => TokenKind.Hash, '$' => TokenKind.Dollar, ',' => TokenKind.Comma, '.' => TokenKind.Dot, '\\' => TokenKind.Backslash,
                _ => throw Error(at, $"Unsupported character '{c}'.")
            };
            Advance(); result.Add(new(kind, c.ToString(), at));
        }
        result.Add(new(TokenKind.Eof, "", Position));
        return result;
    }
}

internal sealed class Parser(IReadOnlyList<Token> tokens, string sourcePath, IncludeResolver includes, HashSet<string>? knownArrays = null)
{
    private static readonly HashSet<string> Reserved = new(
        ("dim goto gosub return exit if then else endif elseif while wend for to step next function endfunction " +
         "type endtype each local global field const select case default endselect repeat until forever data read restore " +
         "abs sgn mod pi true false int float str include new delete first last insert before after null object handle " +
         "and or xor not shl shr sar print end cs").Split(' '), StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> arrayNames = knownArrays ?? new(StringComparer.OrdinalIgnoreCase);
    internal static bool IsReservedName(string name) => Reserved.Contains(name);
    private int index;
    private Token Current => tokens[index];
    private Token Take() => tokens[index++];
    private BasicDiagnosticException Error(Token token, string message) => new(token.Position.File ?? sourcePath, token.Position.Line, token.Position.Column, message);
    private Token Expect(TokenKind kind)
    {
        if (Current.Kind != kind) throw Error(Current, $"Expected {kind}; found '{Current.Text}'.");
        return Take();
    }
    private string Keyword => Current.Kind != TokenKind.Identifier ? "" :
        Current.Text == "end" && tokens[index + 1].Kind == TokenKind.Identifier && tokens[index + 1].Text is "function" or "if" or "select" or "type"
            ? "end" + tokens[index + 1].Text
            : Current.Text == "else" && tokens[index + 1].Kind == TokenKind.Identifier && tokens[index + 1].Text == "if"
                ? "elseif" : Current.Text;
    private Token TakeKeyword(string keyword)
    {
        if (Keyword != keyword) throw Error(Current, $"Expected {keyword}.");
        var token = Take();
        if (token.Text == "end") Take();
        else if (token.Text == "else" && keyword == "elseif") token = Take();
        return token;
    }
    private bool IsLineEnd => Current.Kind == TokenKind.Eof || Current.Kind == TokenKind.Separator && Current.Text == "\n";
    internal List<Statement> Parse() => ParseStatements(false, true);

    private List<Statement> ParseStatements(bool inFunction, bool topLevel = false, string[]? stops = null, bool singleLine = false)
    {
        var statements = new List<Statement>();
        while (Current.Kind != TokenKind.Eof)
        {
            if (stops?.Contains(Keyword) == true || singleLine && IsLineEnd) break;
            if (Current.Kind == TokenKind.Separator) { Take(); continue; }
            if (Current.Kind is TokenKind.CSharp or TokenKind.Module)
            {
                var code = Take();
                if (!topLevel && code.Kind == TokenKind.Module) throw Error(code, "C# modules can only appear in the main program.");
                statements.Add(new CSharp(code.Text, code.Kind == TokenKind.Module, code.Position));
            }
            else if (Current.Kind == TokenKind.Dot)
            {
                var dot = Take();
                if (!topLevel) throw Error(dot, "Data labels can only appear in the main program.");
                var name = ParseName();
                if (Primitive.Tagged(name.Text)) throw Error(name, "Data label cannot have a suffix.");
                statements.Add(new DataLabel(name.Text, dot.Position));
            }
            else
            {
                var first = Expect(TokenKind.Identifier);
                switch (first.Text)
                {
                    case "include":
                        var filename = Expect(TokenKind.String);
                        if (includes.Load(first, filename) is { } includedTokens)
                        {
                            var child = new Parser(includedTokens, filename.Text, includes, arrayNames);
                            statements.AddRange(child.ParseStatements(inFunction, topLevel, singleLine: singleLine));
                            if (child.Current.Kind != TokenKind.Eof) throw child.Error(child.Current, "Expected end-of-file in include.");
                        }
                        break;
                    case "type":
                        if (!topLevel) throw Error(first, "Type can only appear in the main program.");
                        var typeName = ParseName();
                        if (Primitive.Tagged(typeName.Text)) throw Error(typeName, "Type name cannot have a suffix.");
                        Expect(TokenKind.Separator);
                        var fields = new List<FieldDeclaration>();
                        while (Keyword != "endtype")
                        {
                            if (Current.Kind == TokenKind.Separator) { Take(); continue; }
                            TakeKeyword("field");
                            do
                            {
                                var field = ParseName();
                                var bound = ParseBound();
                                Expression? initializer = null;
                                if (bound is null && Current.Kind == TokenKind.Equal) { Take(); initializer = ParseExpression(); }
                                fields.Add(new(field.Text, bound, field.Position, initializer));
                                if (Current.Kind != TokenKind.Comma) break;
                                Take();
                            } while (true);
                            Expect(TokenKind.Separator);
                        }
                        TakeKeyword("endtype");
                        statements.Add(new TypeDeclaration(typeName.Text, fields, first.Position)); break;
                    case "delete":
                        if (Keyword == "each") { Take(); statements.Add(new Delete(null, ParseName().Text, first.Position)); }
                        else statements.Add(new Delete(ParseExpression(), null, first.Position));
                        break;
                    case "insert":
                        var inserted = ParseExpression();
                        if (Keyword is not "before" and not "after") throw Error(Current, "Expected Before or After.");
                        var before = Take().Text == "before";
                        statements.Add(new Insert(inserted, ParseExpression(), before, first.Position)); break;
                    case "print":
                    {
                        var printValue = Current.Kind is TokenKind.Eof or TokenKind.Separator || Keyword is "else" or "elseif" or "endif"
                            ? new Literal(string.Empty, first.Position)
                            : ParseExpression();
                        statements.Add(new Print(printValue, first.Position)); break;
                    }
                    case "data":
                        if (!topLevel) throw Error(first, "Data can only appear in the main program.");
                        var dataValues = new List<Expression>();
                        do { dataValues.Add(ParseExpression()); if (Current.Kind != TokenKind.Comma) break; Take(); } while (true);
                        statements.Add(new DataDeclaration(dataValues, first.Position)); break;
                    case "read":
                        var targets = new List<Expression>();
                        do
                        {
                            var targetName = ParseName();
                            Expression target = Current.Kind == TokenKind.Open
                                ? new ArrayAccess(targetName.Text, ParseArguments(), targetName.Position)
                                : new Variable(targetName.Text, targetName.Position);
                            targets.Add(ParseFields(target));
                            if (Current.Kind != TokenKind.Comma) break;
                            Take();
                        } while (true);
                        statements.Add(new ReadData(targets, first.Position)); break;
                    case "restore":
                        string? label = null;
                        if (Current.Kind == TokenKind.Identifier && Keyword is not ("else" or "elseif" or "endif"))
                        {
                            var labelToken = ParseName();
                            if (Primitive.Tagged(labelToken.Text)) throw Error(labelToken, "Restore label cannot have a suffix.");
                            label = labelToken.Text;
                        }
                        else if (!IsLineEnd && Current.Kind != TokenKind.Separator && Keyword is not ("else" or "elseif" or "endif"))
                            throw Error(Current, "Restore accepts only a data label.");
                        statements.Add(new RestoreData(label, first.Position)); break;
                    case "end": statements.Add(new End(first.Position)); break;
                    case "return":
                        if (!inFunction) throw Error(first, "Return requires a Function.");
                        statements.Add(new Return(Current.Kind is TokenKind.Separator or TokenKind.Eof || Keyword is "else" or "elseif" ? null : ParseExpression(), first.Position));
                        break;
                    case "exit": statements.Add(new Exit(first.Position)); break;
                    case "if": statements.Add(ParseIf(inFunction, first)); break;
                    case "while":
                        var condition = ParseExpression();
                        var whileBody = ParseStatements(inFunction, stops: ["wend"]);
                        TakeKeyword("wend");
                        statements.Add(new While(condition, whileBody, first.Position)); break;
                    case "repeat":
                        var repeatBody = ParseStatements(inFunction, stops: ["until", "forever"]);
                        if (Keyword is not "until" and not "forever") throw Error(Current, "Expected Until or Forever.");
                        var until = Take().Text == "until" ? ParseExpression() : null;
                        statements.Add(new Repeat(repeatBody, until, first.Position)); break;
                    case "for":
                        var loopName = ParseName();
                        Expression loopTarget = Current.Kind == TokenKind.Open
                            ? new ArrayAccess(loopName.Text, ParseArguments(), loopName.Position)
                            : new Variable(loopName.Text, loopName.Position);
                        loopTarget = ParseFields(loopTarget);
                        Expect(TokenKind.Equal);
                        if (Keyword == "each")
                        {
                            Take(); var eachType = ParseName();
                            var eachBody = ParseStatements(inFunction, stops: ["next"]); TakeKeyword("next");
                            statements.Add(new ForEach(loopTarget, eachType.Text, eachBody, first.Position)); break;
                        }
                        var from = ParseExpression(); TakeKeyword("to");
                        var to = ParseExpression();
                        Expression step = new Literal(1, first.Position);
                        if (Keyword == "step") { Take(); step = ParseExpression(); }
                        var forBody = ParseStatements(inFunction, stops: ["next"]);
                        TakeKeyword("next");
                        statements.Add(new For(loopTarget, from, to, step, forBody, first.Position)); break;
                    case "select":
                        var value = ParseExpression();
                        var cases = new List<SelectCase>();
                        IReadOnlyList<Statement> defaultBody = [];
                        while (true)
                        {
                            while (Current.Kind == TokenKind.Separator) Take();
                            if (Keyword == "endselect") break;
                            if (Keyword == "default")
                            {
                                Take(); defaultBody = ParseStatements(inFunction, stops: ["endselect"]); break;
                            }
                            if (Keyword != "case") throw Error(Current, "Expected Case, Default or End Select.");
                            var caseToken = Take(); var values = new List<Expression>();
                            do { values.Add(ParseExpression()); if (Current.Kind != TokenKind.Comma) break; Take(); } while (true);
                            cases.Add(new(values, ParseStatements(inFunction, stops: ["case", "default", "endselect"]), caseToken.Position));
                        }
                        TakeKeyword("endselect");
                        statements.Add(new Select(value, cases, defaultBody, first.Position)); break;
                    case "dim":
                        do
                        {
                            var arrayName = ParseName();
                            var bounds = ParseArguments();
                            if (bounds.Count == 0) throw Error(arrayName, "Dim requires at least one dimension.");
                            arrayNames.Add(Primitive.Name(arrayName.Text));
                            statements.Add(new Dim(arrayName.Text, bounds, arrayName.Position));
                            if (Current.Kind != TokenKind.Comma) break;
                            Take();
                        } while (true);
                        break;
                    case "const":
                        if (!topLevel) throw Error(first, "Const can only appear in the main program.");
                        do
                        {
                            var constant = ParseName(); Expect(TokenKind.Equal);
                            statements.Add(new ConstantDeclaration(constant.Text, ParseExpression(), constant.Position));
                            if (Current.Kind != TokenKind.Comma) break;
                            Take();
                        } while (true);
                        break;
                    case "global":
                    case "local":
                        if (!topLevel && first.Text == "global") throw Error(first, "Global can only appear in the main program.");
                        do
                        {
                            var name = ParseName();
                            var bound = ParseBound();
                            Expression? initializer = null;
                            if (bound is null && Current.Kind == TokenKind.Equal) { Take(); initializer = ParseExpression(); }
                            statements.Add(new Declaration(name.Text, initializer, first.Text == "global", name.Position, bound));
                            if (Current.Kind != TokenKind.Comma) break;
                            Take();
                        } while (true);
                        break;
                    case "function":
                        if (!topLevel) throw Error(first, "Function can only appear in the main program.");
                        var functionName = ParseName();
                        Expect(TokenKind.Open);
                        var parameters = new List<string>();
                        var defaults = new List<Expression?>();
                        if (Current.Kind != TokenKind.Close)
                        {
                            do
                            {
                                parameters.Add(ParseName().Text);
                                Expression? defaultValue = null;
                                if (Current.Kind == TokenKind.Equal) { Take(); defaultValue = ParseExpression(); }
                                defaults.Add(defaultValue);
                                if (Current.Kind != TokenKind.Comma) break;
                                Take();
                            } while (true);
                        }
                        Expect(TokenKind.Close); Expect(TokenKind.Separator);
                        var functionBody = ParseStatements(true, stops: ["endfunction"]);
                        TakeKeyword("endfunction");
                        statements.Add(new Function(functionName.Text, parameters, defaults, functionBody, first.Position)); break;
                    default:
                        if (first.Text == "cs" && Current.Kind == TokenKind.Bridge)
                            statements.Add(new CallStatement(ParseCSharpCall(first), first.Position));
                        else
                        {
                            RequireVariable(first); first = WithSuffix(first);
                            if (Current.Kind == TokenKind.Open && arrayNames.Contains(Primitive.Name(first.Text)))
                            {
                                var target = ParseFields(new ArrayAccess(first.Text, ParseArguments(), first.Position));
                                Expect(TokenKind.Equal);
                                var assigned = ParseExpression();
                                statements.Add(target is FieldAccess field ? new FieldAssignment(field, assigned, first.Position) : target is VectorAccess vector ? new VectorAssignment(vector, assigned, first.Position) : new ArrayAssignment((ArrayAccess)target, assigned, first.Position));
                            }
                            else if (Current.Kind == TokenKind.Open && HasStatementArgumentParentheses())
                                statements.Add(new CallStatement(new Call(first.Text, ParseArguments(), false, first.Position), first.Position));
                            else if (Current.Kind is not (TokenKind.Equal or TokenKind.OpenSquare or TokenKind.Backslash))
                            {
                                var arguments = new List<Expression>();
                                if (Current.Kind is not (TokenKind.Separator or TokenKind.Eof) && Keyword is not ("else" or "elseif"))
                                {
                                    do
                                    {
                                        arguments.Add(ParseExpression());
                                        if (Current.Kind != TokenKind.Comma) break;
                                        Take();
                                    } while (true);
                                }
                                statements.Add(new CallStatement(new Call(first.Text, arguments, false, first.Position), first.Position));
                            }
                            else
                            {
                                var target = ParseFields(new Variable(first.Text, first.Position));
                                Expect(TokenKind.Equal);
                                var assigned = ParseExpression();
                                statements.Add(target is FieldAccess field ? new FieldAssignment(field, assigned, first.Position) : target is VectorAccess vector ? new VectorAssignment(vector, assigned, first.Position) : new Assignment(first.Text, assigned, first.Position));
                            }
                        }
                        break;
                }
            }
            if (stops?.Contains(Keyword) == true || singleLine && IsLineEnd) break;
            if (Current.Kind is not TokenKind.Eof and not TokenKind.Separator)
                throw Error(Current, $"Unsupported syntax '{Current.Text}'; expected a statement separator.");
        }
        return statements;
    }

    // Legacy statement calls distinguish Foo(1,2) from Foo (1),2 by the token
    // after the matching close parenthesis. The latter starts a grouped expression.
    private bool HasStatementArgumentParentheses()
    {
        var depth = 0;
        for (var scan = index; scan < tokens.Count; scan++)
        {
            var token = tokens[scan];
            if (token.Kind is TokenKind.Eof or TokenKind.Separator) return true;
            if (token.Kind == TokenKind.Open) depth++;
            if (token.Kind == TokenKind.Close && --depth == 0)
            {
                var following = tokens[scan + 1];
                return following.Kind is TokenKind.Eof or TokenKind.Separator ||
                    following.Kind == TokenKind.Identifier && following.Text is "else" or "elseif";
            }
        }
        return true; // ParseArguments supplies the source diagnostic for unmatched parentheses.
    }

    private If ParseIf(bool inFunction, Token first)
    {
        var condition = ParseExpression();
        if (Keyword == "then") Take();
        var block = Current.Kind == TokenKind.Separator;
        var thenBody = ParseStatements(inFunction, stops: ["else", "elseif", "endif"], singleLine: !block);
        IReadOnlyList<Statement> elseBody = [];
        if (Keyword == "elseif") elseBody = [ParseIf(inFunction, TakeKeyword("elseif"))];
        else
        {
            if (Keyword == "else") { Take(); elseBody = ParseStatements(inFunction, stops: ["endif"], singleLine: !block); }
            if (block) TakeKeyword("endif");
        }
        return new(condition, thenBody, elseBody, first.Position);
    }

    private Token ParseName()
    {
        var token = Expect(TokenKind.Identifier);
        RequireVariable(token);
        return WithSuffix(token);
    }
    private Token WithSuffix(Token token)
    {
        if (Current.Kind is TokenKind.Percent or TokenKind.Hash or TokenKind.Dollar)
            return token with { Text = token.Text + Take().Text };
        if (Current.Kind == TokenKind.Dot)
        {
            Take(); return token with { Text = token.Text + "." + ParseName().Text };
        }
        return token;
    }
    private Expression? ParseBound()
    {
        if (Current.Kind != TokenKind.OpenSquare) return null;
        Take(); var bound = ParseExpression(); Expect(TokenKind.CloseSquare); return bound;
    }
    private Expression ParseFields(Expression owner)
    {
        while (Current.Kind is TokenKind.Backslash or TokenKind.OpenSquare)
        {
            var token = Take();
            if (token.Kind == TokenKind.Backslash)
            {
                var field = ParseName(); owner = new FieldAccess(owner, field.Text, token.Position);
            }
            else
            {
                var index = ParseExpression(); Expect(TokenKind.CloseSquare);
                owner = new VectorAccess(owner, index, token.Position);
            }
        }
        return owner;
    }
    private void RequireVariable(Token token)
    {
        if (Reserved.Contains(token.Text))
            throw Error(token, $"Unsupported keyword '{token.Text}' cannot be used as a variable.");
    }
    private IReadOnlyList<Expression> ParseArguments()
    {
        Expect(TokenKind.Open);
        var arguments = new List<Expression>();
        if (Current.Kind != TokenKind.Close)
        {
            do
            {
                arguments.Add(ParseExpression());
                if (Current.Kind != TokenKind.Comma) break;
                Take();
            } while (true);
        }
        Expect(TokenKind.Close);
        return arguments;
    }
    private Call ParseCSharpCall(Token first)
    {
        Expect(TokenKind.Bridge);
        var name = Expect(TokenKind.Identifier).Original!;
        var parts = 1;
        while (Current.Kind == TokenKind.Dot)
        {
            Take(); name += "." + Expect(TokenKind.Identifier).Original; parts++;
        }
        if (parts < 2) throw Error(first, "CS:: calls require a qualified type and static method.");
        return new(name, ParseArguments(), true, first.Position);
    }
    private Expression ParseExpression(int minimum = 0, bool allowNot = true)
    {
        var token = Take();
        Expression left;
        switch (token.Kind)
        {
            case TokenKind.Integer:
                var number = 0;
                foreach (var digit in token.Text.AsSpan())
                {
                    var value = digit - '0';
                    if (number > (int.MaxValue - value) / 10)
                    {
                        number = int.MaxValue;
                        break;
                    }
                    number = number * 10 + value;
                }
                left = new Literal(number, token.Position); break;
            case TokenKind.Hex:
                var hexValue = 0;
                foreach (var digit in token.Text.AsSpan(1))
                    hexValue = unchecked(hexValue * 16 + (digit is >= '0' and <= '9' ? digit - '0' : char.ToUpperInvariant(digit) - 'A' + 10));
                left = new Literal(hexValue, token.Position); break;
            case TokenKind.Binary:
                var binaryValue = 0;
                foreach (var digit in token.Text.AsSpan(1))
                    binaryValue = unchecked(binaryValue * 2 + (digit == '1' ? 1 : 0));
                left = new Literal(binaryValue, token.Position); break;
            case TokenKind.Float:
                if (!float.TryParse(token.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var floating) || !float.IsFinite(floating))
                    throw Error(token, "Float literal is outside the supported single-precision range.");
                left = new Literal(floating, token.Position); break;
            case TokenKind.String: left = new Literal(token.Text, token.Position); break;
            case TokenKind.Identifier:
                if (token.Text == "cs" && Current.Kind == TokenKind.Bridge) { left = ParseCSharpCall(token); break; }
                if (token.Text is "int" or "float" or "str")
                {
                    var suffix = token.Text == "int" ? TokenKind.Percent : token.Text == "float" ? TokenKind.Hash : TokenKind.Dollar;
                    if (Current.Kind == suffix) Take();
                    left = new Call(token.Text, [ParseExpression(7)], false, token.Position); break;
                }
                if (token.Text == "handle") { left = new ObjectOperation("handle", null, ParseExpression(7), token.Position); break; }
                if (token.Text == "object")
                {
                    if (Current.Kind == TokenKind.Dot) Take();
                    var targetType = ParseName();
                    left = new ObjectOperation("fromhandle", targetType.Text, ParseExpression(7), token.Position); break;
                }
                if (token.Text == "null") { left = new ObjectOperation("null", null, null, token.Position); break; }
                if (token.Text is "new" or "first" or "last") { left = new ObjectOperation(token.Text, ParseName().Text, null, token.Position); break; }
                if (token.Text is "before" or "after") { left = new ObjectOperation(token.Text, null, ParseExpression(7), token.Position); break; }
                if (token.Text == "pi") { left = new Literal(MathF.PI, token.Position); break; }
                if (token.Text is "true" or "false") { left = new Literal(token.Text == "true" ? 1 : 0, token.Position); break; }
                RequireVariable(token);
                token = WithSuffix(token);
                left = Current.Kind == TokenKind.Open
                    ? arrayNames.Contains(Primitive.Name(token.Text))
                        ? new ArrayAccess(token.Text, ParseArguments(), token.Position)
                        : new Call(token.Text, ParseArguments(), false, token.Position)
                    : new Variable(token.Text, token.Position);
                break;
            case TokenKind.Not:
                if (minimum != 0 || !allowNot) throw Error(token, "Not requires the beginning of an expression; use parentheses here.");
                left = new Binary(ParseExpression(0, false), TokenKind.Equal, new Literal(0, token.Position), token.Position); break;
            case TokenKind.Plus or TokenKind.Minus or TokenKind.Abs or TokenKind.Sgn:
                left = new Unary(token.Kind, ParseExpression(7), token.Position); break;
            case TokenKind.Open:
                left = ParseExpression(); Expect(TokenKind.Close); break;
            default: throw Error(token, "Expected an expression.");
        }
        left = ParseFields(left);
        while (true)
        {
            var precedence = Current.Kind switch
            {
                TokenKind.And or TokenKind.Or or TokenKind.Xor => 0,
                TokenKind.Equal or TokenKind.NotEqual or TokenKind.Less or TokenKind.Greater or TokenKind.LessEqual or TokenKind.GreaterEqual => 1,
                TokenKind.Plus or TokenKind.Minus => 2,
                TokenKind.Shl or TokenKind.Shr or TokenKind.Sar => 3,
                TokenKind.Star or TokenKind.Slash or TokenKind.Mod => 4,
                TokenKind.Power => 5, _ => -1
            };
            if (precedence < minimum) return left;
            var op = Take();
            left = new Binary(left, op.Kind, ParseExpression(precedence + 1), op.Position);
        }
    }
}

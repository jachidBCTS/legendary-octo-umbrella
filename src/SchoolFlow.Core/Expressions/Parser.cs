using System.Globalization;
using System.Text;

namespace SchoolFlow.Core.Expressions;

public abstract record Node;
public sealed record LiteralNode(object? Value) : Node;
public sealed record IdentifierNode(string Name) : Node;
public sealed record MemberNode(Node Target, string Name) : Node;
public sealed record UnaryNode(string Op, Node Operand) : Node;
public sealed record BinaryNode(string Op, Node Left, Node Right) : Node;
public sealed record ConditionalNode(Node Test, Node WhenTrue, Node WhenFalse) : Node;
public sealed record CallNode(string Name, IReadOnlyList<Node> Args) : Node;
public sealed record ListNode(IReadOnlyList<Node> Items) : Node;

internal enum TokenKind { Number, String, Identifier, Op, End }

internal readonly record struct Token(TokenKind Kind, string Text, int Position, decimal Number = 0);

internal static class Lexer
{
    private static readonly string[] TwoCharOps = { "==", "!=", "<>", "<=", ">=", "&&", "||" };
    private const string OneCharOps = "+-*/%^()[],.?:<>=!&";

    public static List<Token> Tokenize(string src)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < src.Length)
        {
            var c = src[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }

            if (char.IsDigit(c) || (c == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1])))
            {
                var start = i;
                while (i < src.Length && char.IsDigit(src[i])) i++;
                if (i < src.Length && src[i] == '.' && i + 1 < src.Length && char.IsDigit(src[i + 1]))
                {
                    i++;
                    while (i < src.Length && char.IsDigit(src[i])) i++;
                }
                var text = src[start..i];
                if (!decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var num))
                    throw new ExpressionException($"Number '{text}' is too large");
                tokens.Add(new Token(TokenKind.Number, text, start, num));
                continue;
            }

            if (c == '"' || c == '\'')
            {
                var start = i;
                var quote = c;
                var sb = new StringBuilder();
                i++;
                var closed = false;
                while (i < src.Length)
                {
                    if (src[i] == '\\' && i + 1 < src.Length)
                    {
                        var n = src[i + 1];
                        sb.Append(n switch { 'n' => '\n', 't' => '\t', _ => n });
                        i += 2;
                        continue;
                    }
                    if (src[i] == quote) { closed = true; i++; break; }
                    sb.Append(src[i]);
                    i++;
                }
                if (!closed) throw new ExpressionException("Text is missing its closing quote");
                tokens.Add(new Token(TokenKind.String, sb.ToString(), start));
                continue;
            }

            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < src.Length && (char.IsLetterOrDigit(src[i]) || src[i] == '_')) i++;
                tokens.Add(new Token(TokenKind.Identifier, src[start..i], start));
                continue;
            }

            if (i + 1 < src.Length)
            {
                var two = src.Substring(i, 2);
                if (Array.IndexOf(TwoCharOps, two) >= 0)
                {
                    tokens.Add(new Token(TokenKind.Op, two, i));
                    i += 2;
                    continue;
                }
            }
            if (OneCharOps.Contains(c))
            {
                tokens.Add(new Token(TokenKind.Op, c.ToString(), i));
                i++;
                continue;
            }
            throw new ExpressionException($"Unexpected character '{c}' at position {i + 1}");
        }
        tokens.Add(new Token(TokenKind.End, "", src.Length));
        return tokens;
    }
}

/// <summary>Recursive-descent parser for the formula language.</summary>
public sealed class Parser
{
    public const int MaxLength = 4000;
    private const int MaxDepth = 80;

    private readonly List<Token> _tokens;
    private int _pos;
    private int _depth;

    private Parser(List<Token> tokens) => _tokens = tokens;

    public static Node Parse(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ExpressionException("Formula is empty");
        if (source.Length > MaxLength) throw new ExpressionException($"Formula is longer than {MaxLength} characters");
        var parser = new Parser(Lexer.Tokenize(source));
        var node = parser.ParseExpression();
        if (parser.Peek.Kind != TokenKind.End)
            throw new ExpressionException($"Unexpected '{parser.Peek.Text}' at position {parser.Peek.Position + 1}");
        return node;
    }

    private Token Peek => _tokens[_pos];
    private Token Next() => _tokens[_pos++];

    private bool IsOp(string op) => Peek.Kind == TokenKind.Op && Peek.Text == op;
    private bool IsWord(string word) =>
        Peek.Kind == TokenKind.Identifier && string.Equals(Peek.Text, word, StringComparison.OrdinalIgnoreCase);

    private bool TakeOp(string op)
    {
        if (!IsOp(op)) return false;
        _pos++;
        return true;
    }

    private void Expect(string op)
    {
        if (!TakeOp(op))
        {
            var found = Peek.Kind == TokenKind.End ? "end of formula" : $"'{Peek.Text}'";
            throw new ExpressionException($"Expected '{op}' but found {found} at position {Peek.Position + 1}");
        }
    }

    private Node ParseExpression()
    {
        if (++_depth > MaxDepth) throw new ExpressionException("Formula is nested too deeply");
        try
        {
            var test = ParseOr();
            if (!TakeOp("?")) return test;
            var whenTrue = ParseExpression();
            Expect(":");
            var whenFalse = ParseExpression();
            return new ConditionalNode(test, whenTrue, whenFalse);
        }
        finally { _depth--; }
    }

    private Node ParseOr()
    {
        var left = ParseAnd();
        while (true)
        {
            if (TakeOp("||")) { left = new BinaryNode("||", left, ParseAnd()); continue; }
            if (IsWord("or")) { _pos++; left = new BinaryNode("||", left, ParseAnd()); continue; }
            return left;
        }
    }

    private Node ParseAnd()
    {
        var left = ParseComparison();
        while (true)
        {
            if (TakeOp("&&")) { left = new BinaryNode("&&", left, ParseComparison()); continue; }
            // "and" followed by "(" is the AND(...) function, handled in ParsePrimary only at operand position.
            if (IsWord("and")) { _pos++; left = new BinaryNode("&&", left, ParseComparison()); continue; }
            return left;
        }
    }

    private Node ParseComparison()
    {
        var left = ParseConcat();
        while (Peek.Kind == TokenKind.Op)
        {
            var op = Peek.Text switch
            {
                "==" or "=" => "==",
                "!=" or "<>" => "!=",
                "<" or ">" or "<=" or ">=" => Peek.Text,
                _ => null,
            };
            if (op is null) break;
            _pos++;
            left = new BinaryNode(op, left, ParseConcat());
        }
        return left;
    }

    private Node ParseConcat()
    {
        var left = ParseAdditive();
        while (TakeOp("&")) left = new BinaryNode("&", left, ParseAdditive());
        return left;
    }

    private Node ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (true)
        {
            if (TakeOp("+")) left = new BinaryNode("+", left, ParseMultiplicative());
            else if (TakeOp("-")) left = new BinaryNode("-", left, ParseMultiplicative());
            else return left;
        }
    }

    private Node ParseMultiplicative()
    {
        var left = ParseUnary();
        while (true)
        {
            if (TakeOp("*")) left = new BinaryNode("*", left, ParseUnary());
            else if (TakeOp("/")) left = new BinaryNode("/", left, ParseUnary());
            else if (TakeOp("%")) left = new BinaryNode("%", left, ParseUnary());
            else return left;
        }
    }

    private Node ParseUnary()
    {
        if (++_depth > MaxDepth) throw new ExpressionException("Formula is nested too deeply");
        try
        {
            if (TakeOp("-")) return new UnaryNode("-", ParseUnary());
            if (TakeOp("+")) return ParseUnary();
            if (TakeOp("!")) return new UnaryNode("!", ParseUnary());
            // "not x" as a prefix word; "NOT(x)" parses the same way.
            if (IsWord("not")) { _pos++; return new UnaryNode("!", ParseUnary()); }
            var operand = ParsePostfix();
            if (TakeOp("^")) return new BinaryNode("^", operand, ParseUnary());
            return operand;
        }
        finally { _depth--; }
    }

    private Node ParsePostfix()
    {
        var node = ParsePrimary();
        while (TakeOp("."))
        {
            if (Peek.Kind != TokenKind.Identifier)
                throw new ExpressionException($"Expected a name after '.' at position {Peek.Position + 1}");
            node = new MemberNode(node, Next().Text);
        }
        return node;
    }

    private Node ParsePrimary()
    {
        var token = Peek;
        switch (token.Kind)
        {
            case TokenKind.Number:
                _pos++;
                return new LiteralNode(token.Number);
            case TokenKind.String:
                _pos++;
                return new LiteralNode(token.Text);
            case TokenKind.Identifier:
                _pos++;
                if (TakeOp("("))
                    return new CallNode(token.Text.ToUpperInvariant(), ParseArguments(")"));
                return token.Text.ToLowerInvariant() switch
                {
                    "true" => new LiteralNode(true),
                    "false" => new LiteralNode(false),
                    "null" or "blank" => new LiteralNode(null),
                    "and" or "or" => throw new ExpressionException(
                        $"'{token.Text}' needs a value on both sides (position {token.Position + 1})"),
                    _ => new IdentifierNode(token.Text),
                };
            case TokenKind.Op when token.Text == "(":
                _pos++;
                var inner = ParseExpression();
                Expect(")");
                return inner;
            case TokenKind.Op when token.Text == "[":
                _pos++;
                return new ListNode(ParseArguments("]"));
            case TokenKind.End:
                throw new ExpressionException("Formula ends unexpectedly");
            default:
                throw new ExpressionException($"Unexpected '{token.Text}' at position {token.Position + 1}");
        }
    }

    private List<Node> ParseArguments(string close)
    {
        var args = new List<Node>();
        if (TakeOp(close)) return args;
        while (true)
        {
            args.Add(ParseExpression());
            if (TakeOp(",")) continue;
            Expect(close);
            return args;
        }
    }
}

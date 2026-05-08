namespace Backend.Query;

/// <summary>
/// hand-written recursive-descent parser for the DiVoid path-query grammar.
///
/// Grammar (informal EBNF):
/// <code>
///   path        = segment ( "/" segment )*
///   segment     = "[" predicate ( "," predicate )* "]"
///               | "[" "]"
///   predicate   = key ":" valueList
///   key         = "id" | "type" | "name" | "status"
///   valueList   = value ( "|" value )*
///   value       = bareToken | quotedString
///   bareToken   = one-or-more chars excluding  , ] | : / [ " whitespace
///   quotedString= '"' chars (with \" and \\ escapes) '"'
/// </code>
///
/// v1 restrictions:
/// <list type="bullet">
/// <item>The <c>!</c> prefix on a value or segment is rejected as "reserved".</item>
/// <item>Parenthesised boolean expressions are not in v1.</item>
/// </list>
///
/// All positions in error messages are 1-based column indices within the input string.
/// </summary>
public static class PathQueryParser
{
    static readonly HashSet<string> AllowedKeys = ["id", "type", "name", "status"];

    /// <summary>
    /// parses <paramref name="input"/> and returns a <see cref="PathQuery"/>.
    /// Throws <see cref="PathQueryParseException"/> on any syntax or v1-constraint violation.
    /// </summary>
    public static PathQuery Parse(string input)
    {
        if (string.IsNullOrEmpty(input))
            throw new PathQueryParseException(1, "path expression is empty");

        var parser = new Parser(input);
        return parser.ParsePath();
    }

    // -----------------------------------------------------------------------
    // inner parser state
    // -----------------------------------------------------------------------

    sealed class Parser(string input)
    {
        readonly string _input = input;
        int _pos = 0;

        // 1-based column number for the character at _pos
        int Column => _pos + 1;

        // peek without advancing
        char Peek() => _pos < _input.Length ? _input[_pos] : '\0';

        // consume and return current char; throws at end
        char Consume()
        {
            if (_pos >= _input.Length)
                throw new PathQueryParseException(Column, "unexpected end of input");
            return _input[_pos++];
        }

        void Expect(char ch)
        {
            char got = Consume();
            if (got != ch)
                throw new PathQueryParseException(Column, $"expected '{ch}', got '{got}'");
        }

        public PathQuery ParsePath()
        {
            var hops = new List<HopSegment>();
            hops.Add(ParseSegment());
            while (Peek() == '/')
            {
                _pos++; // consume '/'
                hops.Add(ParseSegment());
            }
            if (_pos < _input.Length)
                throw new PathQueryParseException(Column, $"unexpected character '{Peek()}' — expected '/' or end of input");
            return new PathQuery { Hops = hops.ToArray() };
        }

        HopSegment ParseSegment()
        {
            // v1 deferred: '!' before '[' means negated segment
            if (Peek() == '!')
                throw new PathQueryParseException(Column, "operator '!' is reserved and not yet supported");

            // v1 deferred: '(' means parenthesised boolean group
            if (Peek() == '(')
                throw new PathQueryParseException(Column, "parenthesised boolean expressions are reserved and not yet supported");

            Expect('[');

            if (Peek() == ']')
            {
                // empty segment = any-node wildcard
                _pos++;
                return new HopSegment { Predicates = [], Negated = false };
            }

            var predicates = new List<HopPredicate>();
            predicates.Add(ParsePredicate());
            while (Peek() == ',')
            {
                _pos++; // consume ','
                predicates.Add(ParsePredicate());
            }

            Expect(']');
            return new HopSegment { Predicates = predicates.ToArray(), Negated = false };
        }

        HopPredicate ParsePredicate()
        {
            int keyStart = Column;
            string key = ParseBareToken(stopAt: ":]|,/[\"");
            if (key.Length == 0)
                throw new PathQueryParseException(keyStart, "expected a key name");

            if (!AllowedKeys.Contains(key))
                throw new PathQueryParseException(keyStart,
                    $"unsupported key '{key}' (allowed: id, type, name, status)");

            Expect(':');

            var values = new List<string>();
            values.Add(ParseValue());
            while (Peek() == '|')
            {
                _pos++; // consume '|'
                values.Add(ParseValue());
            }

            return new HopPredicate { Key = key, Values = values.ToArray(), Negated = false };
        }

        string ParseValue()
        {
            // v1 deferred: '!' prefix on a value means negation
            if (Peek() == '!')
                throw new PathQueryParseException(Column, "operator '!' is reserved and not yet supported");

            if (Peek() == '"')
                return ParseQuotedString();

            int start = Column;
            string tok = ParseBareToken(stopAt: ",]|/[\"");
            if (tok.Length == 0)
                throw new PathQueryParseException(start, "expected a value");
            return tok;
        }

        /// <summary>
        /// reads characters until one of the chars in <paramref name="stopAt"/> is encountered
        /// (or end of input), returning the accumulated token.
        /// </summary>
        string ParseBareToken(string stopAt)
        {
            int start = _pos;
            while (_pos < _input.Length && !stopAt.Contains(_input[_pos]) && !char.IsWhiteSpace(_input[_pos]))
                _pos++;
            return _input.Substring(start, _pos - start);
        }

        string ParseQuotedString()
        {
            int openCol = Column;
            Expect('"');
            var sb = new System.Text.StringBuilder();
            while (true)
            {
                if (_pos >= _input.Length)
                    throw new PathQueryParseException(openCol, "unterminated quoted string");
                char ch = _input[_pos++];
                if (ch == '"') break;
                if (ch == '\\')
                {
                    if (_pos >= _input.Length)
                        throw new PathQueryParseException(Column, "unexpected end after backslash in quoted string");
                    char esc = _input[_pos++];
                    switch (esc)
                    {
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        default:
                            throw new PathQueryParseException(Column, $"unrecognised escape '\\{esc}'");
                    }
                }
                else
                {
                    sb.Append(ch);
                }
            }
            return sb.ToString();
        }
    }
}

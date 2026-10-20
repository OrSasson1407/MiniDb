using System.Text;

namespace MiniDb.Query;

/// <summary>Splits SQL text into tokens. Keywords are case-insensitive.</summary>
public static class Tokenizer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "CREATE", "TABLE", "INSERT", "INTO", "VALUES", "SELECT", "FROM", "WHERE", "LIMIT",
        "DELETE", "PRIMARY", "KEY", "TRUE", "FALSE", "INT", "BIGINT", "DOUBLE", "BOOL", "VARCHAR",
        "INDEX", "ON",
    };

    public static IReadOnlyList<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        int index = 0;
        while (index < sql.Length)
        {
            if (char.IsWhiteSpace(sql[index]))
            {
                index++;
                continue;
            }

            tokens.Add(ReadToken(sql, ref index));
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, sql.Length));
        return tokens;
    }

    private static Token ReadToken(string sql, ref int index)
    {
        char c = sql[index];
        if (char.IsLetter(c) || c == '_')
        {
            return ReadWord(sql, ref index);
        }

        if (char.IsDigit(c) || (c == '-' && index + 1 < sql.Length && char.IsDigit(sql[index + 1])))
        {
            return ReadNumber(sql, ref index);
        }

        return c == '\''
            ? ReadString(sql, ref index)
            : ReadSymbol(sql, ref index);
    }

    private static Token ReadWord(string sql, ref int index)
    {
        int start = index;
        while (index < sql.Length && (char.IsLetterOrDigit(sql[index]) || sql[index] == '_'))
        {
            index++;
        }

        string text = sql[start..index];
        string upper = text.ToUpperInvariant();
        return Keywords.Contains(upper)
            ? new Token(TokenKind.Keyword, upper, start)
            : new Token(TokenKind.Identifier, text, start);
    }

    private static Token ReadNumber(string sql, ref int index)
    {
        int start = index;
        index++;
        while (index < sql.Length && (char.IsDigit(sql[index]) || sql[index] == '.'))
        {
            index++;
        }

        return new Token(TokenKind.Number, sql[start..index], start);
    }

    private static Token ReadString(string sql, ref int index)
    {
        int start = index;
        var text = new StringBuilder();
        index++;
        while (true)
        {
            if (index >= sql.Length)
            {
                throw new SqlSyntaxException("Unterminated string literal", start);
            }

            if (sql[index] != '\'')
            {
                text.Append(sql[index]);
                index++;
                continue;
            }

            if (index + 1 < sql.Length && sql[index + 1] == '\'')
            {
                text.Append('\'');
                index += 2;
                continue;
            }

            index++;
            return new Token(TokenKind.String, text.ToString(), start);
        }
    }

    private static Token ReadSymbol(string sql, ref int index)
    {
        int start = index;
        char c = sql[index];
        string symbol;
        if ((c == '<' || c == '>') && index + 1 < sql.Length && sql[index + 1] == '=')
        {
            symbol = sql.Substring(index, 2);
        }
        else if ("(),*=<>".Contains(c))
        {
            symbol = c.ToString();
        }
        else
        {
            throw new SqlSyntaxException($"Unexpected character '{c}'", start);
        }

        index += symbol.Length;
        return new Token(TokenKind.Symbol, symbol, start);
    }
}

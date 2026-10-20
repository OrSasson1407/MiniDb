using System.Globalization;
using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>Recursive-descent parser for the supported SQL subset.</summary>
public sealed class Parser
{
    private readonly IReadOnlyList<Token> _tokens;
    private int _index;

    private Parser(IReadOnlyList<Token> tokens)
    {
        _tokens = tokens;
    }

    public static Statement Parse(string sql)
    {
        var parser = new Parser(Tokenizer.Tokenize(sql));
        Statement statement = parser.ParseStatement();
        if (parser.Peek.Kind != TokenKind.End)
        {
            throw parser.Error("expected end of statement");
        }

        return statement;
    }

    private Token Peek => _tokens[_index];

    private Statement ParseStatement()
    {
        if (Peek.Kind != TokenKind.Keyword)
        {
            throw Error("expected a statement");
        }

        return Peek.Text switch
        {
            "CREATE" => ParseCreateTable(),
            "INSERT" => ParseInsert(),
            "SELECT" => ParseSelect(),
            "DELETE" => ParseDelete(),
            _ => throw Error("expected CREATE, INSERT, SELECT or DELETE"),
        };
    }

    private CreateTableStatement ParseCreateTable()
    {
        ExpectKeyword("CREATE");
        ExpectKeyword("TABLE");
        string name = ExpectIdentifier("table name");
        ExpectSymbol("(");
        var columns = new List<ColumnSpec> { ParseColumn() };
        while (PeekSymbol(","))
        {
            Advance();
            columns.Add(ParseColumn());
        }

        ExpectSymbol(")");
        return new CreateTableStatement(name, columns);
    }

    private ColumnSpec ParseColumn()
    {
        string name = ExpectIdentifier("column name");
        (ColumnType type, int maxLength) = ParseType();
        bool primaryKey = false;
        if (PeekKeyword("PRIMARY"))
        {
            Advance();
            ExpectKeyword("KEY");
            primaryKey = true;
        }

        return new ColumnSpec(name, type, maxLength, primaryKey);
    }

    private (ColumnType Type, int MaxLength) ParseType()
    {
        if (Peek.Kind != TokenKind.Keyword)
        {
            throw Error("expected a column type");
        }

        Token typeToken = Advance();
        switch (typeToken.Text)
        {
            case "INT":
                return (ColumnType.Int32, 0);
            case "BIGINT":
                return (ColumnType.Int64, 0);
            case "DOUBLE":
                return (ColumnType.Double, 0);
            case "BOOL":
                return (ColumnType.Bool, 0);
            case "VARCHAR":
                ExpectSymbol("(");
                int length = ParseInt("VARCHAR length");
                ExpectSymbol(")");
                return (ColumnType.String, length);
            default:
                throw new SqlSyntaxException($"'{typeToken.Text}' is not a column type", typeToken.Position);
        }
    }

    private InsertStatement ParseInsert()
    {
        ExpectKeyword("INSERT");
        ExpectKeyword("INTO");
        string name = ExpectIdentifier("table name");
        ExpectKeyword("VALUES");
        ExpectSymbol("(");
        var values = new List<object> { ParseLiteral() };
        while (PeekSymbol(","))
        {
            Advance();
            values.Add(ParseLiteral());
        }

        ExpectSymbol(")");
        return new InsertStatement(name, values);
    }

    private SelectStatement ParseSelect()
    {
        ExpectKeyword("SELECT");
        List<string>? columns = null;
        if (PeekSymbol("*"))
        {
            Advance();
        }
        else
        {
            columns = new List<string> { ExpectIdentifier("column name") };
            while (PeekSymbol(","))
            {
                Advance();
                columns.Add(ExpectIdentifier("column name"));
            }
        }

        ExpectKeyword("FROM");
        string name = ExpectIdentifier("table name");
        Condition? where = PeekKeyword("WHERE") ? ParseWhere() : null;
        int? limit = null;
        if (PeekKeyword("LIMIT"))
        {
            Advance();
            limit = ParseInt("LIMIT count");
        }

        return new SelectStatement(name, columns, where, limit);
    }

    private DeleteStatement ParseDelete()
    {
        ExpectKeyword("DELETE");
        ExpectKeyword("FROM");
        string name = ExpectIdentifier("table name");
        return new DeleteStatement(name, ParseWhere());
    }

    private Condition ParseWhere()
    {
        ExpectKeyword("WHERE");
        string column = ExpectIdentifier("column name");
        CompareOp op = ParseOperator();
        object value = ParseLiteral();
        return new Condition(column, op, value);
    }

    private CompareOp ParseOperator()
    {
        if (Peek.Kind == TokenKind.Symbol)
        {
            CompareOp? op = Peek.Text switch
            {
                "=" => CompareOp.Equal,
                "<" => CompareOp.Less,
                "<=" => CompareOp.LessOrEqual,
                ">" => CompareOp.Greater,
                ">=" => CompareOp.GreaterOrEqual,
                _ => null,
            };
            if (op is { } found)
            {
                Advance();
                return found;
            }
        }

        throw Error("expected a comparison operator");
    }

    private object ParseLiteral()
    {
        Token token = Peek;
        switch (token.Kind)
        {
            case TokenKind.Number:
                Advance();
                return ParseNumber(token);
            case TokenKind.String:
                Advance();
                return token.Text;
            case TokenKind.Keyword when token.Text is "TRUE" or "FALSE":
                Advance();
                return token.Text == "TRUE";
            default:
                throw Error("expected a value");
        }
    }

    private static object ParseNumber(Token token)
    {
        if (token.Text.Contains('.'))
        {
            if (!double.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double real))
            {
                throw new SqlSyntaxException($"'{token.Text}' is not a valid number", token.Position);
            }

            return real;
        }

        if (!long.TryParse(token.Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long integer))
        {
            throw new SqlSyntaxException($"'{token.Text}' is out of range for an integer", token.Position);
        }

        return integer;
    }

    private int ParseInt(string what)
    {
        if (Peek.Kind != TokenKind.Number || !int.TryParse(Peek.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int value))
        {
            throw Error($"expected {what}");
        }

        Advance();
        return value;
    }

    private string ExpectIdentifier(string what)
    {
        if (Peek.Kind != TokenKind.Identifier)
        {
            throw Error($"expected {what}");
        }

        return Advance().Text;
    }

    private void ExpectKeyword(string keyword)
    {
        if (!PeekKeyword(keyword))
        {
            throw Error($"expected {keyword}");
        }

        Advance();
    }

    private void ExpectSymbol(string symbol)
    {
        if (!PeekSymbol(symbol))
        {
            throw Error($"expected '{symbol}'");
        }

        Advance();
    }

    private bool PeekKeyword(string keyword) => Peek.Kind == TokenKind.Keyword && Peek.Text == keyword;

    private bool PeekSymbol(string symbol) => Peek.Kind == TokenKind.Symbol && Peek.Text == symbol;

    private Token Advance()
    {
        Token token = Peek;
        if (token.Kind != TokenKind.End)
        {
            _index++;
        }

        return token;
    }

    private SqlSyntaxException Error(string message)
    {
        string found = Peek.Kind == TokenKind.End ? "end of input" : $"'{Peek.Text}'";
        return new SqlSyntaxException($"{message}, found {found}", Peek.Position);
    }
}

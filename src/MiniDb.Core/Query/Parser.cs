using System.Globalization;
using MiniDb.Records;

namespace MiniDb.Query;

/// <summary>
/// Recursive-descent parser for the supported SQL subset. WHERE binds AND tighter than OR, and parentheses group.
/// </summary>
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
            "CREATE" => ParseCreate(),
            "INSERT" => ParseInsert(),
            "SELECT" => ParseSelect(),
            "UPDATE" => ParseUpdate(),
            "DELETE" => ParseDelete(),
            _ => throw Error("expected CREATE, INSERT, SELECT, UPDATE or DELETE"),
        };
    }

    private Statement ParseCreate()
    {
        ExpectKeyword("CREATE");
        return PeekKeyword("INDEX") ? ParseCreateIndex() : ParseCreateTable();
    }

    private CreateIndexStatement ParseCreateIndex()
    {
        ExpectKeyword("INDEX");
        ExpectKeyword("ON");
        string table = ExpectIdentifier("table name");
        ExpectSymbol("(");
        string column = ExpectIdentifier("column name");
        ExpectSymbol(")");
        return new CreateIndexStatement(table, column);
    }

    private CreateTableStatement ParseCreateTable()
    {
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
        List<SelectItem>? items = null;
        if (PeekSymbol("*"))
        {
            Advance();
        }
        else
        {
            items = new List<SelectItem> { ParseSelectItem() };
            while (PeekSymbol(","))
            {
                Advance();
                items.Add(ParseSelectItem());
            }
        }

        ExpectKeyword("FROM");
        string name = ExpectIdentifier("table name");
        var joins = new List<JoinClause>();
        while (PeekKeyword("JOIN") || PeekKeyword("INNER"))
        {
            joins.Add(ParseJoin());
        }

        Expr? where = PeekKeyword("WHERE") ? ParseWhere() : null;
        var groupBy = new List<string>();
        if (PeekKeyword("GROUP"))
        {
            Advance();
            ExpectKeyword("BY");
            groupBy = ParseColumnList();
        }

        var orderBy = new List<OrderTerm>();
        if (PeekKeyword("ORDER"))
        {
            Advance();
            ExpectKeyword("BY");
            orderBy.Add(ParseOrderTerm());
            while (PeekSymbol(","))
            {
                Advance();
                orderBy.Add(ParseOrderTerm());
            }
        }

        int? limit = null;
        if (PeekKeyword("LIMIT"))
        {
            Advance();
            limit = ParseInt("LIMIT count");
        }

        return new SelectStatement(name, items, joins, where, groupBy, orderBy, limit);
    }

    private SelectItem ParseSelectItem()
    {
        if (Peek.Kind == TokenKind.Identifier && _tokens[_index + 1] is { Kind: TokenKind.Symbol, Text: "(" })
        {
            return ParseAggregate();
        }

        return new ColumnItem(ParseColumnName());
    }

    private AggregateItem ParseAggregate()
    {
        Token function = Advance();
        AggregateKind kind = function.Text.ToUpperInvariant() switch
        {
            "COUNT" => AggregateKind.Count,
            "SUM" => AggregateKind.Sum,
            "MIN" => AggregateKind.Min,
            "MAX" => AggregateKind.Max,
            "AVG" => AggregateKind.Avg,
            _ => throw new SqlSyntaxException($"'{function.Text}' is not an aggregate function", function.Position),
        };
        ExpectSymbol("(");
        string? column = null;
        if (PeekSymbol("*"))
        {
            if (kind != AggregateKind.Count)
            {
                throw Error("only COUNT accepts *");
            }

            Advance();
        }
        else
        {
            column = ParseColumnName();
        }

        ExpectSymbol(")");
        return new AggregateItem(kind, column);
    }

    private JoinClause ParseJoin()
    {
        if (PeekKeyword("INNER"))
        {
            Advance();
        }

        ExpectKeyword("JOIN");
        string table = ExpectIdentifier("table name");
        ExpectKeyword("ON");
        string left = ParseColumnName();
        ExpectSymbol("=");
        string right = ParseColumnName();
        return new JoinClause(table, left, right);
    }

    private OrderTerm ParseOrderTerm()
    {
        string column = ParseColumnName();
        bool descending = false;
        if (PeekKeyword("DESC"))
        {
            Advance();
            descending = true;
        }
        else if (PeekKeyword("ASC"))
        {
            Advance();
        }

        return new OrderTerm(column, descending);
    }

    private List<string> ParseColumnList()
    {
        var columns = new List<string> { ParseColumnName() };
        while (PeekSymbol(","))
        {
            Advance();
            columns.Add(ParseColumnName());
        }

        return columns;
    }

    private UpdateStatement ParseUpdate()
    {
        ExpectKeyword("UPDATE");
        string table = ExpectIdentifier("table name");
        ExpectKeyword("SET");
        var assignments = new List<Assignment> { ParseAssignment() };
        while (PeekSymbol(","))
        {
            Advance();
            assignments.Add(ParseAssignment());
        }

        return new UpdateStatement(table, assignments, ParseWhere());
    }

    private Assignment ParseAssignment()
    {
        string column = ExpectIdentifier("column name");
        ExpectSymbol("=");
        return new Assignment(column, ParseLiteral());
    }

    private DeleteStatement ParseDelete()
    {
        ExpectKeyword("DELETE");
        ExpectKeyword("FROM");
        string name = ExpectIdentifier("table name");
        return new DeleteStatement(name, ParseWhere());
    }

    /// <summary>Parses <c>WHERE expr</c>. Used by UPDATE and DELETE, which both require a filter.</summary>
    private Expr ParseWhere()
    {
        ExpectKeyword("WHERE");
        return ParseOr();
    }

    private Expr ParseOr()
    {
        Expr left = ParseAnd();
        while (PeekKeyword("OR"))
        {
            Advance();
            left = new OrExpr(left, ParseAnd());
        }

        return left;
    }

    private Expr ParseAnd()
    {
        Expr left = ParsePrimary();
        while (PeekKeyword("AND"))
        {
            Advance();
            left = new AndExpr(left, ParsePrimary());
        }

        return left;
    }

    private Expr ParsePrimary()
    {
        if (PeekSymbol("("))
        {
            Advance();
            Expr inner = ParseOr();
            ExpectSymbol(")");
            return inner;
        }

        string column = ParseColumnName();
        CompareOp op = ParseOperator();
        object value = ParseLiteral();
        return new ComparisonExpr(new Condition(column, op, value));
    }

    /// <summary>A column name, or <c>table.column</c>.</summary>
    private string ParseColumnName()
    {
        string first = ExpectIdentifier("column name");
        if (!PeekSymbol("."))
        {
            return first;
        }

        Advance();
        return first + "." + ExpectIdentifier("column name");
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

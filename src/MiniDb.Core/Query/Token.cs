namespace MiniDb.Query;

public enum TokenKind
{
    Keyword,
    Identifier,
    Number,
    String,
    Symbol,
    End,
}

/// <summary>One lexical unit of a SQL statement. Keywords are stored upper-case; string literals hold their unescaped value.</summary>
public readonly record struct Token(TokenKind Kind, string Text, int Position);

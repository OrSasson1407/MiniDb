using MiniDb.Query;

namespace MiniDb.Tests.Query;

public class TokenizerTests
{
    [Fact]
    public void KeywordsAreCaseInsensitive_AndOperatorsAreTokenized()
    {
        IReadOnlyList<Token> tokens = Tokenizer.Tokenize("select * from users where id >= 10");

        Assert.Equal(
            new[] { TokenKind.Keyword, TokenKind.Symbol, TokenKind.Keyword, TokenKind.Identifier,
                    TokenKind.Keyword, TokenKind.Identifier, TokenKind.Symbol, TokenKind.Number, TokenKind.End },
            tokens.Select(t => t.Kind));
        Assert.Equal("SELECT", tokens[0].Text);
        Assert.Equal(">=", tokens[6].Text);
    }

    [Fact]
    public void StringLiteral_UnescapesDoubledQuotes()
    {
        IReadOnlyList<Token> tokens = Tokenizer.Tokenize("'it''s'");

        Assert.Equal(TokenKind.String, tokens[0].Kind);
        Assert.Equal("it's", tokens[0].Text);
    }

    [Fact]
    public void UnterminatedString_Throws()
    {
        Assert.Throws<SqlSyntaxException>(() => Tokenizer.Tokenize("'open"));
    }
}

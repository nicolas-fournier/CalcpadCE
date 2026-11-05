using System;
using System.Collections.Generic;

namespace Calcpad.Core
{
    public partial class ExpressionParser
    {
        private sealed class Token
        {
            internal string Value { get; set; }
            internal TokenTypes Type;
            internal int CacheID = -1;
            internal Token(string value, TokenTypes type)
            {
                Value = value;
                Type = type;
            }
            public override string ToString() => Value;
        }

        private enum TokenTypes
        {
            Expression,
            Heading,
            Text,
            Html,
            Error
        }

        private List<Token> GetTokens(ReadOnlySpan<char> s)
        {
            var tokens = new List<Token>();
            var ts = new TextSpan(s);
            var currentSeparator = ' ';
            for (int i = 0, len = s.Length; i < len; ++i)
            {
                var c = s[i];
                if (c == '\'' || c == '\"')
                {
                    if (currentSeparator == ' ' || currentSeparator == c)
                    {
                        if (currentSeparator == c)
                        {
                            var i1 = i + 1;
                            if (i1 < len && s[i1] == currentSeparator)
                            {
                                ts.Expand();
                                ts.Expand();
                                i = i1;
                                continue;
                            }
                        }
                        if (!ts.IsEmpty)
                            AddToken(tokens, ts.Cut(), currentSeparator);

                        ts.Reset(i + 1);
                        currentSeparator = currentSeparator == c ? ' ' : c;
                    }
                    else if (currentSeparator != ' ')
                        ts.Expand();
                }
                else
                    ts.Expand();
            }
            if (!ts.IsEmpty)
                AddToken(tokens, ts.Cut(), currentSeparator);

            return tokens;
        }

        // Markdown-first line classifier (#md full):
        // prose is the default, math is delimited by $…$ (inline) and $$…$$ (block).
        // A backslash escapes a literal delimiter: \$ \# \`.
        // Keyword lines (#if, #repeat, …) are intercepted before this is called, so a
        // leading '#'+space (an ATX heading) or any other prose reaches here as plain text
        // and is rendered by Markdig (ParseMarkdown).
        private List<Token> GetTokensMarkdown(ReadOnlySpan<char> s)
        {
            var tokens = new List<Token>();
            var ts = new TextSpan(s);
            var inMath = false;
            for (int i = 0, len = s.Length; i < len; ++i)
            {
                var c = s[i];
                if (c == '\\' && i + 1 < len && IsMarkdownEscapable(s[i + 1]))
                {
                    ts.Expand(); // backslash
                    ts.Expand(); // escaped char
                    ++i;
                }
                else if (c == '`')
                {
                    // Inline code `expr` is evaluated as Calcpad math (markdown-native formulas),
                    // exactly like $expr$. A literal backtick is \`.
                    if (!ts.IsEmpty || inMath)
                        AddMarkdownToken(tokens, ts.Cut(), inMath);

                    ts.Reset(i + 1);
                    inMath = !inMath;
                }
                else if (c == '$')
                {
                    var isBlock = i + 1 < len && s[i + 1] == '$';
                    if (!ts.IsEmpty || inMath)
                        AddMarkdownToken(tokens, ts.Cut(), inMath);

                    var skip = isBlock ? 2 : 1;
                    ts.Reset(i + skip);
                    if (isBlock)
                        ++i;

                    inMath = !inMath;
                }
                else
                    ts.Expand();
            }
            if (!ts.IsEmpty || inMath)
                AddMarkdownToken(tokens, ts.Cut(), inMath);

            return tokens;
        }

        private static bool IsMarkdownEscapable(char c) => c is '$' or '#' or '`' or '\\';

        private void AddMarkdownToken(List<Token> tokens, ReadOnlySpan<char> value, bool isMath)
        {
            if (isMath)
            {
                if (value.IsWhiteSpace())
                    return;

                tokens.Add(new Token(value.ToString(), TokenTypes.Expression));
                return;
            }
            var text = value.ToString();
            if (text.IndexOf('\\') >= 0)
                text = text
                    .Replace("\\$", "$")
                    .Replace("\\#", "#")
                    .Replace("\\`", "`")
                    .Replace("\\\\", "\\");

            if (_isVal < 1)
                text = tokens.Count == 0 ? text + " " : string.Concat(" ", text, " ");

            var token = new Token(text, TokenTypes.Text);
            var trimmed = text.AsSpan().TrimStart();
            if (trimmed.Length > 0 && trimmed[0] == '<')
                token.Type = TokenTypes.Html;

            tokens.Add(token);
        }

        private void AddToken(List<Token> tokens, ReadOnlySpan<char> value, char separator)
        {
            var tokenValue = value.ToString().Replace("\"\"", "&quot;").Replace("''", "&apos;");
            var tokenType = GetTokenType(separator);
            if (tokenType == TokenTypes.Expression)
            {
                if (value.IsWhiteSpace())
                    return;
            }
            else if (_isVal < 1)
            {
                if (tokens.Count == 0)
                    tokenValue += " ";
                else
                    tokenValue = string.Concat(" ", tokenValue," ");
            }

            var token = new Token(tokenValue, tokenType);
            if (token.Type == TokenTypes.Text)
            {
                tokenValue = tokenValue.TrimStart();
                if (tokenValue.Length > 0 && tokenValue[0] == '<')
                    token.Type = TokenTypes.Html;
            }
            tokens.Add(token);
        }

        private static TokenTypes GetTokenType(char separator)
        {
            return separator switch
            {
                ' ' => TokenTypes.Expression,
                '\"' => TokenTypes.Heading,
                '\'' => TokenTypes.Text,
                _ => TokenTypes.Error,
            };
        }
    }
}
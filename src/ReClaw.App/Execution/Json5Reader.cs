using System;
using System.Text;
using System.Text.Json;

namespace ReClaw.App.Execution;

public static class Json5Reader
{
    public static bool TryParse(string raw, out JsonDocument? document)
    {
        document = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        try
        {
            var normalized = Normalize(raw);
            document = JsonDocument.Parse(normalized, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Normalize(string raw)
    {
        var stripped = StripCommentsAndNormalizeQuotes(raw);
        return QuoteUnquotedKeys(stripped);
    }

    private static string StripCommentsAndNormalizeQuotes(string input)
    {
        var sb = new StringBuilder(input.Length);
        bool inString = false;
        char quote = '\0';
        bool escape = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];
            var next = i + 1 < input.Length ? input[i + 1] : '\0';

            if (!inString)
            {
                if (c == '/' && next == '/')
                {
                    while (i < input.Length && input[i] != '\n') i++;
                    if (i < input.Length) sb.Append('\n');
                    continue;
                }
                if (c == '/' && next == '*')
                {
                    i += 2;
                    while (i < input.Length - 1 && !(input[i] == '*' && input[i + 1] == '/')) i++;
                    i++;
                    continue;
                }
                if (c == '\'' || c == '"')
                {
                    inString = true;
                    quote = c;
                    sb.Append('"');
                    continue;
                }
                sb.Append(c);
                continue;
            }

            if (escape)
            {
                escape = false;
                sb.Append(c);
                continue;
            }

            if (c == '\\')
            {
                escape = true;
                sb.Append(c);
                continue;
            }

            if (c == quote)
            {
                inString = false;
                sb.Append('"');
                continue;
            }

            if (quote == '\'' && c == '"')
            {
                sb.Append("\\\"");
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static string QuoteUnquotedKeys(string input)
    {
        var sb = new StringBuilder(input.Length);
        bool inString = false;
        char quote = '\0';
        bool escape = false;
        bool expectingKey = false;

        for (var i = 0; i < input.Length; i++)
        {
            var c = input[i];

            if (inString)
            {
                sb.Append(c);
                if (escape)
                {
                    escape = false;
                }
                else if (c == '\\')
                {
                    escape = true;
                }
                else if (c == quote)
                {
                    inString = false;
                }
                continue;
            }

            if (c == '"' || c == '\'')
            {
                inString = true;
                quote = c;
                sb.Append(c);
                expectingKey = false;
                continue;
            }

            if (c == '{' || c == ',')
            {
                expectingKey = true;
                sb.Append(c);
                continue;
            }

            if (expectingKey)
            {
                if (char.IsWhiteSpace(c))
                {
                    sb.Append(c);
                    continue;
                }

                if (c == '}')
                {
                    expectingKey = false;
                    sb.Append(c);
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    quote = c;
                    sb.Append(c);
                    expectingKey = false;
                    continue;
                }

                var start = i;
                while (i < input.Length && IsIdentifierChar(input[i]))
                {
                    i++;
                }

                var token = input[start..i];
                sb.Append('"').Append(token).Append('"');
                expectingKey = false;
                i--;
                continue;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    private static bool IsIdentifierChar(char c)
    {
        return char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '-';
    }
}

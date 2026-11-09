namespace dotmeer.filebrowser.FileSystem.Search;

internal static class NameMatching
{
    public static bool ContainsOrdinalIgnoreCase(string name, string substring)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (substring.Length == 0)
        {
            throw new ArgumentException("Подстрока поиска не может быть пустой.", nameof(substring));
        }

        return name.Contains(substring, StringComparison.OrdinalIgnoreCase);
    }
}

internal enum GlobTokenType
{
    Literal,
    AnySequence,
    AnyChar,
    CharClass
}

internal readonly record struct GlobCharRange(char Low, char High);

internal readonly struct GlobToken
{
    private GlobToken(GlobTokenType type, char literal, GlobCharRange[]? ranges)
    {
        Type = type;
        Literal = literal;
        Ranges = ranges;
    }

    public GlobTokenType Type { get; }

    public char Literal { get; }

    public GlobCharRange[]? Ranges { get; }

    public static GlobToken AnySequence => new(GlobTokenType.AnySequence, '\0', null);

    public static GlobToken AnyChar => new(GlobTokenType.AnyChar, '\0', null);

    public static GlobToken LiteralChar(char value) => new(GlobTokenType.Literal, value, null);

    public static GlobToken CharClass(GlobCharRange[] ranges) => new(GlobTokenType.CharClass, '\0', ranges);
}

/// <summary>
/// Glob-шаблон (*, ?, [...] с диапазонами), сопоставляемый с полным именем объекта
/// без учёта регистра (SPEC §10.6). Невалидный шаблон отклоняется при парсинге.
/// </summary>
public sealed class GlobMatcher
{
    private readonly string _pattern;
    private readonly GlobToken[] _tokens;

    private GlobMatcher(string pattern, GlobToken[] tokens)
    {
        _pattern = pattern;
        _tokens = tokens;
    }

    public string Pattern => _pattern;

    public static GlobMatcher Parse(string pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            throw new InvalidFileSystemRequestException("Glob-шаблон не может быть пустым.");
        }

        var tokens = new List<GlobToken>();
        var index = 0;
        while (index < pattern.Length)
        {
            var current = pattern[index];
            switch (current)
            {
                case '*':
                    while (index + 1 < pattern.Length && pattern[index + 1] == '*')
                    {
                        index++;
                    }

                    tokens.Add(GlobToken.AnySequence);
                    break;
                case '?':
                    tokens.Add(GlobToken.AnyChar);
                    break;
                case '[':
                    var closeIndex = pattern.IndexOf(']', index + 1);
                    if (closeIndex < 0 || closeIndex == index + 1)
                    {
                        throw new InvalidFileSystemRequestException("Glob-шаблон невалиден: класс символов не закрыт.");
                    }

                    tokens.Add(GlobToken.CharClass(ParseCharClass(pattern, index + 1, closeIndex)));
                    index = closeIndex;
                    break;
                default:
                    tokens.Add(GlobToken.LiteralChar(current));
                    break;
            }

            index++;
        }

        return new GlobMatcher(pattern, tokens.ToArray());
    }

    public bool IsMatch(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var tokenIndex = 0;
        var valueIndex = 0;
        var starTokenIndex = -1;
        var starValueIndex = -1;

        while (valueIndex < value.Length)
        {
            if (tokenIndex < _tokens.Length && MatchesChar(_tokens[tokenIndex], value[valueIndex]))
            {
                tokenIndex++;
                valueIndex++;
            }
            else if (tokenIndex < _tokens.Length && _tokens[tokenIndex].Type == GlobTokenType.AnySequence)
            {
                starTokenIndex = tokenIndex;
                starValueIndex = valueIndex;
                tokenIndex++;
            }
            else if (starTokenIndex >= 0)
            {
                tokenIndex = starTokenIndex + 1;
                starValueIndex++;
                valueIndex = starValueIndex;
            }
            else
            {
                return false;
            }
        }

        while (tokenIndex < _tokens.Length && _tokens[tokenIndex].Type == GlobTokenType.AnySequence)
        {
            tokenIndex++;
        }

        return tokenIndex == _tokens.Length;
    }

    private static GlobCharRange[] ParseCharClass(string pattern, int startIndex, int closeIndex)
    {
        var ranges = new List<GlobCharRange>();
        var index = startIndex;
        while (index < closeIndex)
        {
            var current = pattern[index];
            if (current == '-' && index != startIndex && index != closeIndex - 1)
            {
                throw new InvalidFileSystemRequestException("Glob-шаблон невалиден: дефис вне диапазона.");
            }

            if (index + 2 < closeIndex && pattern[index + 1] == '-' && pattern[index + 2] != '-')
            {
                var high = pattern[index + 2];
                if (current > high)
                {
                    throw new InvalidFileSystemRequestException("Glob-шаблон невалиден: диапазон символов перевёрнут.");
                }

                ranges.Add(new GlobCharRange(current, high));
                index += 3;
            }
            else
            {
                ranges.Add(new GlobCharRange(current, current));
                index++;
            }
        }

        return ranges.ToArray();
    }

    private static bool MatchesChar(in GlobToken token, char value) => token.Type switch
    {
        GlobTokenType.AnyChar => true,
        GlobTokenType.Literal => char.ToUpperInvariant(token.Literal) == char.ToUpperInvariant(value),
        GlobTokenType.CharClass => ClassContains(token.Ranges!, value),
        _ => false,
    };

    private static bool ClassContains(GlobCharRange[] ranges, char value)
    {
        var upper = char.ToUpperInvariant(value);
        var lower = char.ToLowerInvariant(value);
        foreach (var range in ranges)
        {
            if ((range.Low <= value && value <= range.High)
                || (char.ToUpperInvariant(range.Low) <= upper && upper <= char.ToUpperInvariant(range.High))
                || (char.ToLowerInvariant(range.Low) <= lower && lower <= char.ToLowerInvariant(range.High)))
            {
                return true;
            }
        }

        return false;
    }
}

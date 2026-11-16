using System.Globalization;
using System.Text;

namespace BlackwaterExchange;

/// <summary>A Lua table from a saved file: keyed by string or number, in file order.</summary>
public sealed class LuaTable
{
    public Dictionary<object, object?> Fields { get; } = [];

    public object? this[object key] => Fields.GetValueOrDefault(key is int number ? (double)number : key);

    public LuaTable? Table(string key) => this[key] as LuaTable;
    public string? String(string key) => this[key] as string;
    public double? Number(string key) => this[key] as double?;

    /// <summary>The array part: values at keys 1, 2, 3… in order.</summary>
    public IEnumerable<object?> Array()
    {
        for (var index = 1.0; Fields.TryGetValue(index, out var value); index++)
            yield return value;
    }
}

public sealed class LuaFormatException(string message, int line) : Exception($"{message} (line {line})")
{
    public int Line { get; } = line;
}

/// <summary>
/// Reads the files WoW writes for SavedVariables: assignments of Lua literals (tables, strings, numbers,
/// booleans, nil) with line comments. It is a parser for data only. Nothing is evaluated, so a file can
/// contain nothing but values. <paramref name="keep"/> decides by path which values to build; the rest are
/// scanned past without being stored, so a large file costs little memory.
/// </summary>
public sealed class LuaSavedVariables
{
    public const int MaxDepth = 40;

    private readonly string text;
    private readonly Func<IReadOnlyList<object>, bool> keep;
    private readonly List<object> path = [];
    private int position;
    private int line = 1;

    private LuaSavedVariables(string text, Func<IReadOnlyList<object>, bool> keep)
    {
        this.text = text;
        this.keep = keep;
    }

    /// <summary>The top-level variables kept by <paramref name="keep"/> (whose path starts with the variable name).</summary>
    public static Dictionary<string, object?> Parse(string text, Func<IReadOnlyList<object>, bool>? keep = null)
    {
        var reader = new LuaSavedVariables(text, keep ?? (_ => true));
        return reader.ParseFile();
    }

    private Dictionary<string, object?> ParseFile()
    {
        var variables = new Dictionary<string, object?>(StringComparer.Ordinal);
        SkipSpace();
        while (position < text.Length)
        {
            var name = ReadName() ?? throw Error("Expected a variable name");
            SkipSpace();
            Expect('=');
            path.Add(name);
            var wanted = keep(path);
            var value = ReadValue(wanted, 1);
            path.RemoveAt(path.Count - 1);
            if (wanted)
                variables[name] = value;
            SkipSpace();
            if (Peek() == ';')
            {
                position++;
                SkipSpace();
            }
        }
        return variables;
    }

    private object? ReadValue(bool build, int depth)
    {
        if (depth > MaxDepth)
            throw Error("Tables are nested too deeply");
        SkipSpace();
        var next = Peek();
        if (next == '{')
            return ReadTable(build, depth);
        if (next is '"' or '\'')
            return ReadString(build);
        if (next == '[' && position + 1 < text.Length && text[position + 1] is '[' or '=')
            return ReadLongString(build);
        if (next == '-' || next == '.' || char.IsAsciiDigit(next))
            return ReadNumber();
        var word = ReadName() ?? throw Error($"Unexpected character '{next}'");
        return word switch
        {
            "true" => true,
            "false" => false,
            "nil" => null,
            // How WoW writes the infinities and NaN.
            "inf" => double.PositiveInfinity,
            "nan" => double.NaN,
            _ => throw Error($"Unexpected word '{word}'"),
        };
    }

    private LuaTable? ReadTable(bool build, int depth)
    {
        Expect('{');
        var table = build ? new LuaTable() : null;
        var arrayIndex = 1.0;
        while (true)
        {
            SkipSpace();
            if (Peek() == '}')
            {
                position++;
                return table;
            }

            object key;
            if (Peek() == '[' && position + 1 < text.Length && text[position + 1] is not ('[' or '='))
            {
                position++;
                key = ReadValue(true, depth + 1) switch
                {
                    string name => name,
                    double number => number,
                    bool flag => flag,
                    _ => throw Error("A table key must be a string, number or boolean"),
                };
                SkipSpace();
                Expect(']');
                SkipSpace();
                Expect('=');
            }
            else if (IsNameStart(Peek()) && LooksLikeNamedField())
            {
                key = ReadName()!;
                SkipSpace();
                Expect('=');
            }
            else
            {
                key = arrayIndex++;
            }

            path.Add(key);
            var wanted = build && keep(path);
            var value = ReadValue(wanted, depth + 1);
            path.RemoveAt(path.Count - 1);
            if (wanted && value is not null)
                table!.Fields[key] = value;

            SkipSpace();
            if (Peek() is ',' or ';')
                position++;
            else if (Peek() != '}')
                throw Error("Expected ',' or '}' in a table");
        }
    }

    // name = value, as opposed to a bare value such as true or a variable.
    private bool LooksLikeNamedField()
    {
        var at = position;
        while (at < text.Length && IsNamePart(text[at]))
            at++;
        while (at < text.Length && text[at] is ' ' or '\t' or '\r' or '\n')
            at++;
        return at < text.Length && text[at] == '=' && (at + 1 >= text.Length || text[at + 1] != '=');
    }

    private string? ReadString(bool build)
    {
        var quote = text[position++];
        var builder = build ? new StringBuilder() : null;
        while (true)
        {
            if (position >= text.Length)
                throw Error("A string is not closed");
            var character = text[position++];
            if (character == quote)
                return builder?.ToString();
            if (character == '\n')
                throw Error("A string runs past the end of its line");
            if (character != '\\')
            {
                builder?.Append(character);
                continue;
            }
            if (position >= text.Length)
                throw Error("A string is not closed");
            var escape = text[position++];
            switch (escape)
            {
                case 'n': builder?.Append('\n'); break;
                case 't': builder?.Append('\t'); break;
                case 'r': builder?.Append('\r'); break;
                case 'a': builder?.Append('\a'); break;
                case 'b': builder?.Append('\b'); break;
                case 'f': builder?.Append('\f'); break;
                case 'v': builder?.Append('\v'); break;
                case '\n': line++; builder?.Append('\n'); break;
                case >= '0' and <= '9':
                {
                    // \ddd: up to three decimal digits, a byte. WoW writes UTF-8 text this way rarely; keep it as a char.
                    var value = escape - '0';
                    for (var digits = 1; digits < 3 && position < text.Length && char.IsAsciiDigit(text[position]); digits++)
                        value = value * 10 + (text[position++] - '0');
                    if (value > 255)
                        throw Error("An escaped byte is out of range");
                    builder?.Append((char)value);
                    break;
                }
                default: builder?.Append(escape); break;
            }
        }
    }

    private string? ReadLongString(bool build)
    {
        position++; // [
        var level = 0;
        while (Peek() == '=')
        {
            level++;
            position++;
        }
        Expect('[');
        var close = "]" + new string('=', level) + "]";
        var end = text.IndexOf(close, position, StringComparison.Ordinal);
        if (end < 0)
            throw Error("A long string is not closed");
        var value = build ? text[position..end] : null;
        line += CountLines(position, end);
        position = end + close.Length;
        return value?.StartsWith('\n') == true ? value[1..] : value;
    }

    private double ReadNumber()
    {
        var start = position;
        if (Peek() == '-')
            position++;
        if (position + 1 < text.Length && text[position] == '0' && text[position + 1] is 'x' or 'X')
        {
            position += 2;
            var hexStart = position;
            while (position < text.Length && char.IsAsciiHexDigit(text[position]))
                position++;
            if (hexStart == position)
                throw Error("A hexadecimal number has no digits");
            if (!long.TryParse(text.AsSpan(hexStart, position - hexStart), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var hex))
                throw Error("A hexadecimal number is too large");
            return text[start] == '-' ? -hex : hex;
        }
        if (text.AsSpan(position).StartsWith("inf"))
        {
            position += 3;
            return text[start] == '-' ? double.NegativeInfinity : double.PositiveInfinity;
        }
        while (position < text.Length && (char.IsAsciiDigit(text[position]) || text[position] is '.' or 'e' or 'E'
                   || (text[position] is '+' or '-' && text[position - 1] is 'e' or 'E')))
            position++;
        if (!double.TryParse(text.AsSpan(start, position - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            throw Error($"'{text[start..position]}' is not a number");
        return number;
    }

    private string? ReadName()
    {
        if (position >= text.Length || !IsNameStart(text[position]))
            return null;
        var start = position;
        while (position < text.Length && IsNamePart(text[position]))
            position++;
        return text[start..position];
    }

    private static bool IsNameStart(char character) => char.IsAsciiLetter(character) || character == '_';
    private static bool IsNamePart(char character) => char.IsAsciiLetterOrDigit(character) || character == '_';

    private void SkipSpace()
    {
        while (position < text.Length)
        {
            var character = text[position];
            if (character == '\n')
            {
                line++;
                position++;
            }
            else if (character is ' ' or '\t' or '\r' or '﻿')
            {
                position++;
            }
            else if (character == '-' && position + 1 < text.Length && text[position + 1] == '-')
            {
                // A comment: "-- [1]" after array items, or a block comment --[[ ]].
                position += 2;
                if (Peek() == '[' && position + 1 < text.Length && text[position + 1] is '[' or '=')
                {
                    ReadLongString(false);
                }
                else
                {
                    while (position < text.Length && text[position] != '\n')
                        position++;
                }
            }
            else
            {
                return;
            }
        }
    }

    private char Peek() => position < text.Length ? text[position] : '\0';

    private void Expect(char character)
    {
        if (Peek() != character)
            throw Error(position >= text.Length ? $"The file ends where '{character}' was expected" : $"Expected '{character}'");
        position++;
    }

    private int CountLines(int from, int to)
    {
        var count = 0;
        for (var at = from; at < to; at++)
            if (text[at] == '\n')
                count++;
        return count;
    }

    private LuaFormatException Error(string message) => new(message, line);
}

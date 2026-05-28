using System.Text;

namespace Lugiano.Workflow.SyncService.Util;

// Best-effort RTF -> plain text. Returns null when the input does not look like
// RTF or cannot be parsed, so callers can fall back to storing only RawRtf.
public static class RtfConverter
{
    public static string? ToPlainText(string? rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf))
            return null;
        if (!rtf.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase))
            return null;

        try
        {
            return Parse(rtf);
        }
        catch
        {
            return null;
        }
    }

    private static string Parse(string rtf)
    {
        var sb = new StringBuilder(rtf.Length);
        // Tracks groups we should skip entirely (font tables, stylesheets, etc.).
        var ignoreDepth = new Stack<bool>();
        var ignoreGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "fonttbl", "colortbl", "stylesheet", "info", "pict", "*" };
        bool ignoring = false;

        int i = 0;
        while (i < rtf.Length)
        {
            char c = rtf[i];

            if (c == '{')
            {
                ignoreDepth.Push(ignoring);
                i++;
            }
            else if (c == '}')
            {
                ignoring = ignoreDepth.Count > 0 ? ignoreDepth.Pop() : false;
                i++;
            }
            else if (c == '\\')
            {
                i++;
                if (i >= rtf.Length) break;
                char next = rtf[i];

                if (next == '\\' || next == '{' || next == '}')
                {
                    if (!ignoring) sb.Append(next);
                    i++;
                }
                else if (next == '\'')
                {
                    // Hex-escaped byte: \'xx
                    if (i + 2 < rtf.Length)
                    {
                        var hex = rtf.Substring(i + 1, 2);
                        if (!ignoring && byte.TryParse(hex,
                                System.Globalization.NumberStyles.HexNumber, null, out var b))
                            sb.Append((char)b);
                        i += 3;
                    }
                    else i++;
                }
                else if (char.IsLetter(next))
                {
                    int start = i;
                    while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                    string word = rtf.Substring(start, i - start);

                    // Optional numeric parameter.
                    int paramStart = i;
                    if (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i])))
                    {
                        i++;
                        while (i < rtf.Length && char.IsDigit(rtf[i])) i++;
                    }
                    string param = rtf.Substring(paramStart, i - paramStart);

                    // A single trailing space is a delimiter and is consumed.
                    if (i < rtf.Length && rtf[i] == ' ') i++;

                    if (ignoreGroups.Contains(word))
                        ignoring = true;

                    if (!ignoring)
                        ApplyControlWord(sb, word, param, rtf, ref i);
                }
                else if (next == '*')
                {
                    ignoring = true;
                    i++;
                }
                else
                {
                    i++; // unknown symbol control, skip
                }
            }
            else if (c is '\r' or '\n')
            {
                i++; // raw line breaks in RTF are not content
            }
            else
            {
                if (!ignoring) sb.Append(c);
                i++;
            }
        }

        return CollapseWhitespace(sb.ToString());
    }

    private static void ApplyControlWord(StringBuilder sb, string word, string param,
        string rtf, ref int i)
    {
        switch (word.ToLowerInvariant())
        {
            case "par":
            case "line":
            case "sect":
                sb.Append('\n');
                break;
            case "tab":
                sb.Append('\t');
                break;
            case "u":
                // Unicode char: \uN, followed by a fallback char we should drop.
                if (int.TryParse(param, out var code))
                    sb.Append(char.ConvertFromUtf32(code & 0xFFFF));
                if (i < rtf.Length && rtf[i] != '\\' && rtf[i] != '{' && rtf[i] != '}')
                    i++; // skip the single fallback character
                break;
            // Everything else (formatting) produces no text.
        }
    }

    private static string CollapseWhitespace(string text)
    {
        var lines = text.Replace("\r", string.Empty)
            .Split('\n')
            .Select(l => l.Trim());
        var result = string.Join("\n", lines)
            .Trim();
        // Collapse 3+ blank lines down to a single blank line.
        while (result.Contains("\n\n\n"))
            result = result.Replace("\n\n\n", "\n\n");
        return result;
    }
}

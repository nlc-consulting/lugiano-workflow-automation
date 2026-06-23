using System.Globalization;
using System.Text;

namespace Lugiano.Workflow.SyncService.Util;

// A single styled span of note text: contiguous characters sharing one color +
// weight. ColorHex is "#RRGGBB" (auto/default resolves to black).
public sealed record RtfRun(string Text, string ColorHex, bool Bold);

// Best-effort RTF -> styled runs, grouped into paragraphs, so chart-note PDFs can
// reproduce ChiroTouch's blue/red coloring and bold section headers. Returns null
// when the input isn't RTF or can't be parsed; callers fall back to plain text.
//
// This is intentionally a separate pass from RtfConverter.ToPlainText: that one
// stays the fast, lossy path used by the scrubber and sync, while this one carries
// the formatting only the print/fax PDFs need.
public static class RtfRichConverter
{
    public const string DefaultColor = "#000000";

    public static IReadOnlyList<IReadOnlyList<RtfRun>>? ToRuns(string? rtf)
    {
        if (string.IsNullOrWhiteSpace(rtf)) return null;
        if (!rtf.TrimStart().StartsWith(@"{\rtf", StringComparison.OrdinalIgnoreCase)) return null;
        try { return Parse(rtf); }
        catch { return null; }
    }

    private static List<IReadOnlyList<RtfRun>> Parse(string rtf)
    {
        // Color table: index 0 is RTF's "auto" entry (the leading ';'). \cfN
        // indexes into this list. Empty until a \colortbl is seen → all black.
        var colors = new List<string>();

        var paragraphs = new List<IReadOnlyList<RtfRun>>();
        var currentPara = new List<RtfRun>();
        var buf = new StringBuilder();
        int runCf = 0; bool runBold = false;

        int cf = 0; bool bold = false;
        var stateStack = new Stack<(int Cf, bool Bold)>();
        var ignoreStack = new Stack<bool>();
        bool ignoring = false;

        var ignoreGroups = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "fonttbl", "stylesheet", "info", "pict" };

        string HexFor(int idx) => idx > 0 && idx < colors.Count ? colors[idx]
            : idx == 0 && colors.Count > 0 ? colors[0]
            : DefaultColor;

        void FlushRun()
        {
            if (buf.Length == 0) return;
            currentPara.Add(new RtfRun(buf.ToString(), HexFor(runCf), runBold));
            buf.Clear();
        }
        void EndPara()
        {
            FlushRun();
            paragraphs.Add(currentPara);
            currentPara = new List<RtfRun>();
        }
        void AppendText(string s)
        {
            if (s.Length == 0) return;
            if (buf.Length == 0) { runCf = cf; runBold = bold; }
            else if (runCf != cf || runBold != bold) { FlushRun(); runCf = cf; runBold = bold; }
            buf.Append(s);
        }

        int i = 0;
        while (i < rtf.Length)
        {
            char c = rtf[i];

            if (c == '{')
            {
                ignoreStack.Push(ignoring);
                stateStack.Push((cf, bold));
                i++;
            }
            else if (c == '}')
            {
                FlushRun(); // style scope ends at the group boundary
                ignoring = ignoreStack.Count > 0 ? ignoreStack.Pop() : false;
                if (stateStack.Count > 0) { var s = stateStack.Pop(); cf = s.Cf; bold = s.Bold; }
                i++;
            }
            else if (c == '\\')
            {
                i++;
                if (i >= rtf.Length) break;
                char next = rtf[i];

                if (next is '\\' or '{' or '}')
                {
                    if (!ignoring) AppendText(next.ToString());
                    i++;
                }
                else if (next == '\'')
                {
                    if (i + 2 < rtf.Length)
                    {
                        var hex = rtf.Substring(i + 1, 2);
                        if (!ignoring && byte.TryParse(hex, NumberStyles.HexNumber, null, out var b))
                            AppendText(((char)b).ToString());
                        i += 3;
                    }
                    else i++;
                }
                else if (char.IsLetter(next))
                {
                    int start = i;
                    while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                    string word = rtf.Substring(start, i - start);

                    int paramStart = i;
                    if (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i])))
                    {
                        i++;
                        while (i < rtf.Length && char.IsDigit(rtf[i])) i++;
                    }
                    string param = rtf.Substring(paramStart, i - paramStart);
                    if (i < rtf.Length && rtf[i] == ' ') i++;

                    if (word.Equals("colortbl", StringComparison.OrdinalIgnoreCase))
                    {
                        colors.Clear();
                        ParseColorTable(rtf, ref i, colors); // stops at the closing '}'
                    }
                    else if (ignoreGroups.Contains(word))
                    {
                        ignoring = true;
                    }
                    else if (!ignoring)
                    {
                        switch (word.ToLowerInvariant())
                        {
                            case "cf": cf = int.TryParse(param, out var ci) ? ci : 0; break;
                            case "b": bold = param != "0"; break;
                            case "plain": bold = false; cf = 0; break;
                            case "par":
                            case "line":
                            case "sect": EndPara(); break;
                            case "tab": AppendText("\t"); break;
                            case "u":
                                if (int.TryParse(param, out var code))
                                    AppendText(char.ConvertFromUtf32(code & 0xFFFF));
                                if (i < rtf.Length && rtf[i] != '\\' && rtf[i] != '{' && rtf[i] != '}')
                                    i++; // drop the single fallback char
                                break;
                        }
                    }
                }
                else if (next == '*')
                {
                    ignoring = true;
                    i++;
                }
                else
                {
                    i++; // unknown symbol control
                }
            }
            else if (c is '\r' or '\n')
            {
                i++; // raw line breaks aren't content
            }
            else
            {
                if (!ignoring) AppendText(c.ToString());
                i++;
            }
        }

        EndPara();
        return Normalize(paragraphs);
    }

    // Reads "\redN\greenN\blueN;" entries until the colortbl group's closing '}'.
    // Leaves i pointing at that '}' so the main loop pops the group state.
    private static void ParseColorTable(string rtf, ref int i, List<string> colors)
    {
        int r = 0, g = 0, b = 0;
        while (i < rtf.Length && rtf[i] != '}')
        {
            char c = rtf[i];
            if (c == '\\')
            {
                i++;
                int start = i;
                while (i < rtf.Length && char.IsLetter(rtf[i])) i++;
                string word = rtf.Substring(start, i - start);
                int ps = i;
                if (i < rtf.Length && (rtf[i] == '-' || char.IsDigit(rtf[i])))
                {
                    i++;
                    while (i < rtf.Length && char.IsDigit(rtf[i])) i++;
                }
                int val = int.TryParse(rtf.Substring(ps, i - ps), out var v) ? v : 0;
                if (i < rtf.Length && rtf[i] == ' ') i++;
                switch (word.ToLowerInvariant())
                {
                    case "red": r = val; break;
                    case "green": g = val; break;
                    case "blue": b = val; break;
                }
            }
            else if (c == ';')
            {
                colors.Add($"#{r:X2}{g:X2}{b:X2}");
                r = g = b = 0;
                i++;
            }
            else i++;
        }
    }

    // Drop leading/trailing blank paragraphs and collapse runs of blanks to one.
    private static List<IReadOnlyList<RtfRun>> Normalize(List<IReadOnlyList<RtfRun>> paras)
    {
        static bool IsBlank(IReadOnlyList<RtfRun> p) => p.All(r => string.IsNullOrWhiteSpace(r.Text));

        var res = new List<IReadOnlyList<RtfRun>>();
        foreach (var p in paras)
        {
            if (IsBlank(p))
            {
                if (res.Count == 0 || IsBlank(res[^1])) continue;
                res.Add(new List<RtfRun>());
            }
            else res.Add(p);
        }
        while (res.Count > 0 && IsBlank(res[^1])) res.RemoveAt(res.Count - 1);
        return res;
    }
}

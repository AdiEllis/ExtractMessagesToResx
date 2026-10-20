// ExtractMessagesToResx
// ---------------------
// סורק פרויקט C#, מוצא כל מחרוזת שמכילה עברית ויוצר ממנה קובץ RESX
// שבו ה-Key וה-Value הם ההודעה עצמה.
//
// מחרוזות עם משתנים (שרשור "טקסט " + name או $"טקסט {name}") הופכות
// לתבנית עם מצייני מקום: "טקסט {0}", והמשתנים נרשמים בדוח כדי שיהיה
// אפשר להחליף את הקוד ב- Messages.Format("טקסט {0}", name).
//
// שימוש:
//   ExtractMessagesToResx <תיקיית הפרויקט> [תיקיית פלט]
//
// פלט:
//   Messages.resx       - Key = Value = ההודעה (אם הקובץ כבר קיים, ערכים שערכת נשמרים)
//   Messages.report.csv - כל מופע: קובץ, שורה, סוג, Key, משתנים, קוד מקורי, קוד מוצע
//
// הקוד כולו בקלאס אחד, בלי תלויות חיצוניות (C# 7.3 ומעלה, ‎.NET Framework 4.x או ‎.NET 6+).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

public static class Program
{
    // ===================== הגדרות =====================

    static readonly string[] SkipDirs = { "bin", "obj", ".git", ".vs", "node_modules", "packages" };
    static readonly string[] SkipFileSuffixes = { ".Designer.cs", ".g.cs", ".g.i.cs", ".AssemblyInfo.cs" };
    const string ResxFileName = "Messages.resx";
    const string ReportFileName = "Messages.report.csv";
    const string HelperClassName = "Messages"; // משמש רק לקוד המוצע בדוח

    // ===================== Main =====================

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length < 1 || !Directory.Exists(args[0]))
        {
            Console.WriteLine("Usage: ExtractMessagesToResx <projectDir> [outputDir]");
            return 1;
        }

        string root = Path.GetFullPath(args[0]);
        string outDir = args.Length > 1 ? Path.GetFullPath(args[1]) : Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outDir);

        var found = new List<Occurrence>();
        int fileCount = 0;
        foreach (string file in EnumerateCsFiles(root))
        {
            fileCount++;
            string text = File.ReadAllText(file); // מזהה BOM אוטומטית; קבצים בלי BOM נקראים כ-UTF-8
            string rel = file.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            try
            {
                Analyze(text, rel, 1, found);
            }
            catch (Exception ex)
            {
                Console.WriteLine("WARN: failed to parse " + rel + ": " + ex.Message);
            }
        }

        string resxPath = Path.Combine(outDir, ResxFileName);
        int newKeys = WriteResx(resxPath, found);
        string reportPath = Path.Combine(outDir, ReportFileName);
        WriteReport(reportPath, found);

        Console.WriteLine("Files scanned:    " + fileCount);
        Console.WriteLine("Occurrences:      " + found.Count + "  (with variables: " + found.Count(o => o.Args.Count > 0) + ")");
        Console.WriteLine("Unique messages:  " + found.Select(o => o.Key).Distinct().Count() + "  (new in resx: " + newKeys + ")");
        Console.WriteLine("RESX:   " + resxPath);
        Console.WriteLine("Report: " + reportPath);
        return 0;
    }

    static IEnumerable<string> EnumerateCsFiles(string dir)
    {
        foreach (string f in Directory.GetFiles(dir, "*.cs"))
        {
            if (!SkipFileSuffixes.Any(s => f.EndsWith(s, StringComparison.OrdinalIgnoreCase)))
                yield return f;
        }
        foreach (string d in Directory.GetDirectories(dir))
        {
            if (SkipDirs.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)) continue;
            foreach (string f in EnumerateCsFiles(d)) yield return f;
        }
    }

    // ===================== מודל =====================

    enum TokKind { Ident, Number, String, Char, Punct }

    class Part
    {
        public bool IsHole;
        public string Text;      // טקסט (אחרי פענוח escapes) או הביטוי שבתוך {...}
        public string Suffix;    // לחור: ",5" או ":N2" (יישור/פורמט), אחרת null
        public int Offset;       // לחור: מיקום הביטוי בטקסט המקורי
    }

    class Token
    {
        public TokKind Kind;
        public int Start, End;   // [Start, End) בטקסט
        public string Text;      // טקסט המקור של הטוקן
        public List<Part> Parts; // רק למחרוזות
        public bool IsInterpolated;
        public bool Consumed;
        public bool IsStr { get { return Kind == TokKind.String; } }
        public bool Is(string p) { return Kind == TokKind.Punct && Text == p; }
        public bool IsWord(string w) { return Kind == TokKind.Ident && Text == w; }
    }

    class Occurrence
    {
        public string File;
        public int Line;
        public string Kind;      // Simple / Concat / Interpolated
        public string Key;
        public List<string> Args = new List<string>();
        public string Original;
    }

    // ===================== ניתוח קובץ =====================

    static void Analyze(string text, string file, int baseLine, List<Occurrence> output)
    {
        var lexer = new Lexer(text);
        List<Token> toks = lexer.Tokenize();

        for (int k = 0; k < toks.Count; k++)
        {
            Token t = toks[k];
            if (!t.IsStr || t.Consumed || !HasHebrew(t)) continue;

            // מרחיבים שמאלה וימינה לאורך שרשרת "+"
            var operands = new List<int[]>(); // [start,end] אינדקסים של טוקנים, כולל
            operands.Add(new[] { k, k });

            int left = k;
            while (left - 2 >= 0 && toks[left - 1].Is("+"))
            {
                int s = OperandBackward(toks, left - 2);
                if (s < 0) break;
                operands.Insert(0, new[] { s, left - 2 });
                left = s;
            }
            int right = k;
            while (right + 2 < toks.Count && toks[right + 1].Is("+"))
            {
                int e = OperandForward(toks, right + 2);
                if (e < 0) break;
                operands.Add(new[] { right + 2, e });
                right = e;
            }

            var occ = BuildOccurrence(text, toks, operands);
            occ.File = file;
            occ.Line = baseLine + lexer.LineOf(toks[left].Start) - 1;
            occ.Original = OneLine(text.Substring(toks[left].Start, toks[right].End - toks[left].Start));
            output.Add(occ);

            for (int i = left; i <= right; i++) if (toks[i].IsStr) toks[i].Consumed = true;
        }

        // מחרוזות בעברית שנמצאות בתוך {...} של מחרוזת אינטרפולציה
        foreach (Token t in toks)
        {
            if (!t.IsStr || !t.IsInterpolated) continue;
            foreach (Part p in t.Parts)
            {
                if (p.IsHole && ContainsHebrew(p.Text))
                    Analyze(p.Text, file, baseLine + lexer.LineOf(p.Offset) - 1, output);
            }
        }
    }

    static Occurrence BuildOccurrence(string text, List<Token> toks, List<int[]> operands)
    {
        var occ = new Occurrence();

        // ביטויים לא-מחרוזתיים שמופיעים לפני המחרוזת הראשונה מחוברים לארגומנט אחד,
        // כי ב- a + b + "טקסט" הם מתחברים ביניהם קודם (אם הם מספרים - זה חיבור).
        int firstLiteral = operands.FindIndex(o => o[0] == o[1] && toks[o[0]].IsStr);
        if (firstLiteral > 1)
        {
            var merged = new[] { operands[0][0], operands[firstLiteral - 1][1] };
            operands = new List<int[]> { merged }.Concat(operands.Skip(firstLiteral)).ToList();
        }

        bool interpolated = false;
        var pieces = new List<Part>();
        foreach (int[] o in operands)
        {
            Token single = o[0] == o[1] ? toks[o[0]] : null;
            if (single != null && single.IsStr)
            {
                if (single.IsInterpolated) interpolated = true;
                pieces.AddRange(single.Parts);
            }
            else
            {
                string expr = text.Substring(toks[o[0]].Start, toks[o[1]].End - toks[o[0]].Start);
                pieces.Add(new Part { IsHole = true, Text = expr.Trim() });
            }
        }

        bool hasArgs = pieces.Any(p => p.IsHole);
        var sb = new StringBuilder();
        foreach (Part p in pieces)
        {
            if (p.IsHole)
            {
                sb.Append('{').Append(occ.Args.Count).Append(p.Suffix).Append('}');
                occ.Args.Add(OneLine(p.Text));
            }
            else
            {
                // כשיש משתנים התבנית תעבור ב- string.Format, לכן { } בטקסט מוכפלים
                sb.Append(hasArgs ? p.Text.Replace("{", "{{").Replace("}", "}}") : p.Text);
            }
        }
        occ.Key = sb.ToString();
        occ.Kind = operands.Count > 1 ? "Concat" : interpolated && hasArgs ? "Interpolated" : "Simple";
        return occ;
    }

    // ---------- גבולות אופרנד בשרשרת ----------

    static readonly HashSet<string> MulOps = new HashSet<string> { "*", "/", "%" };
    static readonly HashSet<string> MemberOps = new HashSet<string> { ".", "?.", "::" };

    // מקבל אינדקס של הטוקן האחרון באופרנד, מחזיר את אינדקס הטוקן הראשון שלו (או -1)
    static int OperandBackward(List<Token> toks, int j)
    {
        int start = PostfixBackward(toks, j);
        while (start >= 2 && toks[start - 1].Kind == TokKind.Punct && MulOps.Contains(toks[start - 1].Text))
        {
            int s = PostfixBackward(toks, start - 2);
            if (s < 0) break;
            start = s;
        }
        return start;
    }

    static int PostfixBackward(List<Token> toks, int j)
    {
        if (j < 0) return -1;
        int i = j;
        while (true)
        {
            Token t = toks[i];
            while (t.Is("!") || t.Is("++") || t.Is("--")) { i--; if (i < 0) return -1; t = toks[i]; } // x! x++
            int e;
            if (t.Is(")") || t.Is("]"))
            {
                e = MatchBackward(toks, i);
                if (e < 0) return -1;
            }
            else if (t.Is(">"))
            {
                e = GenericBackward(toks, i);
                if (e < 0) return -1;
            }
            else if (t.Kind == TokKind.Ident || t.Kind == TokKind.Number || t.Kind == TokKind.String || t.Kind == TokKind.Char)
            {
                e = i;
            }
            else return -1;

            if (e - 1 >= 0)
            {
                Token p = toks[e - 1];
                if (p.Kind == TokKind.Punct && MemberOps.Contains(p.Text)) { i = e - 2; if (i < 0) return e; continue; }
                bool group = toks[e].Is("(") || toks[e].Is("[");
                if (group && (p.Kind == TokKind.Ident || p.Is(")") || p.Is("]") || p.Is(">")) && !IsKeywordBeforeParen(p))
                { i = e - 1; continue; } // קריאה לפונקציה / אינדקסר
                if (group && toks[e].Is("[") && p.Is("?")) { i = e - 2; if (i < 0) return -1; continue; } // x?[0]
                if (p.IsWord("new") || p.IsWord("await") || p.IsWord("typeof") || p.IsWord("nameof")) return e - 1;
                if (p.Kind == TokKind.Ident && toks[e].Kind == TokKind.Ident && e - 2 >= 0 && toks[e - 2].IsWord("new")) return e - 2; // new Foo
            }
            return e;
        }
    }

    static bool IsKeywordBeforeParen(Token t)
    {
        switch (t.Text)
        {
            case "if": case "while": case "for": case "foreach": case "switch": case "return":
            case "throw": case "using": case "lock": case "catch": case "when": case "in": case "is":
            case "as": case "case": case "yield": case "else":
                return true;
        }
        return false;
    }

    // מקבל אינדקס של הטוקן הראשון באופרנד, מחזיר את אינדקס הטוקן האחרון שלו (או -1)
    static int OperandForward(List<Token> toks, int i)
    {
        int end = PostfixForward(toks, i);
        while (end >= 0 && end + 2 < toks.Count && toks[end + 1].Kind == TokKind.Punct && MulOps.Contains(toks[end + 1].Text))
        {
            int e = PostfixForward(toks, end + 2);
            if (e < 0) break;
            end = e;
        }
        return end;
    }

    static int PostfixForward(List<Token> toks, int i)
    {
        if (i >= toks.Count) return -1;
        // אופרטורים אונריים / מילות מפתח לפני הביטוי
        while (i < toks.Count && (toks[i].Is("!") || toks[i].Is("-") || toks[i].Is("~") || toks[i].IsWord("new") || toks[i].IsWord("await")))
            i++;
        if (i >= toks.Count) return -1;

        int e;
        Token t = toks[i];
        if (t.Is("(") || t.Is("["))
        {
            e = MatchForward(toks, i);
            if (e < 0) return -1;
            // המרה: (string)x
            if (t.Is("(") && e + 1 < toks.Count && (toks[e + 1].Kind == TokKind.Ident || toks[e + 1].Is("(")) && !IsKeywordBeforeParen(toks[e + 1]))
                return PostfixForward(toks, e + 1);
        }
        else if (t.Kind == TokKind.Ident || t.Kind == TokKind.Number || t.Kind == TokKind.String || t.Kind == TokKind.Char)
            e = i;
        else return -1;

        while (e + 1 < toks.Count)
        {
            Token n = toks[e + 1];
            if (n.Kind == TokKind.Punct && MemberOps.Contains(n.Text) && e + 2 < toks.Count && toks[e + 2].Kind == TokKind.Ident) { e += 2; continue; }
            if (n.Is("(") || n.Is("[")) { int m = MatchForward(toks, e + 1); if (m < 0) break; e = m; continue; }
            if (n.Is("?") && e + 2 < toks.Count && toks[e + 2].Is("[")) { int m = MatchForward(toks, e + 2); if (m < 0) break; e = m; continue; }
            if (n.Is("!") || n.Is("++") || n.Is("--")) { e++; continue; }
            if (n.Is("<")) { int m = GenericForward(toks, e + 1); if (m > 0 && m + 1 < toks.Count && toks[m + 1].Is("(")) { e = m; continue; } }
            break;
        }
        return e;
    }

    static int MatchForward(List<Token> toks, int i)
    {
        int depth = 0;
        for (int j = i; j < toks.Count; j++)
        {
            if (toks[j].Is("(") || toks[j].Is("[") || toks[j].Is("{")) depth++;
            else if (toks[j].Is(")") || toks[j].Is("]") || toks[j].Is("}")) { depth--; if (depth == 0) return j; if (depth < 0) return -1; }
        }
        return -1;
    }

    static int MatchBackward(List<Token> toks, int i)
    {
        int depth = 0;
        for (int j = i; j >= 0; j--)
        {
            if (toks[j].Is(")") || toks[j].Is("]") || toks[j].Is("}")) depth++;
            else if (toks[j].Is("(") || toks[j].Is("[") || toks[j].Is("{")) { depth--; if (depth == 0) return j; if (depth < 0) return -1; }
        }
        return -1;
    }

    // Foo<A, B<C>> : רק שמות, נקודות, פסיקים, ? ו-[]
    static int GenericForward(List<Token> toks, int i)
    {
        int depth = 0;
        for (int j = i; j < toks.Count && j < i + 30; j++)
        {
            Token t = toks[j];
            if (t.Is("<")) depth++;
            else if (t.Is(">")) { depth--; if (depth == 0) return j; }
            else if (!(t.Kind == TokKind.Ident || t.Is(".") || t.Is(",") || t.Is("?") || t.Is("[") || t.Is("]"))) return -1;
        }
        return -1;
    }

    static int GenericBackward(List<Token> toks, int i)
    {
        int depth = 0;
        for (int j = i; j >= 0 && j > i - 30; j--)
        {
            Token t = toks[j];
            if (t.Is(">")) depth++;
            else if (t.Is("<")) { depth--; if (depth == 0) return j - 1 >= 0 && toks[j - 1].Kind == TokKind.Ident ? j - 1 : -1; }
            else if (!(t.Kind == TokKind.Ident || t.Is(".") || t.Is(",") || t.Is("?") || t.Is("[") || t.Is("]"))) return -1;
        }
        return -1;
    }

    // ===================== Lexer =====================

    class Lexer
    {
        readonly string s;
        int pos;
        readonly List<int> lineStarts = new List<int> { 0 };

        public Lexer(string text)
        {
            s = text;
            for (int i = 0; i < s.Length; i++) if (s[i] == '\n') lineStarts.Add(i + 1);
        }

        public int LineOf(int offset)
        {
            int idx = lineStarts.BinarySearch(offset);
            return (idx >= 0 ? idx : ~idx - 1) + 1;
        }

        static readonly string[] Puncts =
        {
            ">>>=", "<<=", ">>=", "??=", "?.", "??", "=>", "==", "!=", "<=", ">=", "&&", "||",
            "++", "--", "+=", "-=", "*=", "/=", "%=", "&=", "|=", "^=", "->", "::", ".."
        };

        char At(int i) { return i < s.Length ? s[i] : '\0'; }

        public List<Token> Tokenize()
        {
            var list = new List<Token>();
            bool lineStart = true;
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '\n') { lineStart = true; pos++; continue; }
                if (char.IsWhiteSpace(c)) { pos++; continue; }

                // הנחיות קדם-מעבד (#region וכו') - מדלגים על כל השורה
                if (c == '#' && lineStart) { SkipToEol(); continue; }
                lineStart = false;

                if (c == '/' && At(pos + 1) == '/') { SkipToEol(); continue; }
                if (c == '/' && At(pos + 1) == '*')
                {
                    int e = s.IndexOf("*/", pos + 2, StringComparison.Ordinal);
                    pos = e < 0 ? s.Length : e + 2;
                    continue;
                }

                int start = pos;
                Token str = TryString();
                if (str != null) { list.Add(str); continue; }

                if (c == '\'')
                {
                    pos++;
                    while (pos < s.Length && s[pos] != '\'' && s[pos] != '\n') pos += s[pos] == '\\' ? 2 : 1;
                    pos++;
                    list.Add(Make(TokKind.Char, start));
                    continue;
                }
                if (char.IsLetter(c) || c == '_' || (c == '@' && (char.IsLetter(At(pos + 1)) || At(pos + 1) == '_')))
                {
                    pos++;
                    while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_')) pos++;
                    list.Add(Make(TokKind.Ident, start));
                    continue;
                }
                if (char.IsDigit(c) || (c == '.' && char.IsDigit(At(pos + 1))))
                {
                    pos++;
                    while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] == '_' || (s[pos] == '.' && char.IsDigit(At(pos + 1))))) pos++;
                    list.Add(Make(TokKind.Number, start));
                    continue;
                }
                string p = Puncts.FirstOrDefault(x => string.CompareOrdinal(s, pos, x, 0, x.Length) == 0);
                pos += p != null ? p.Length : 1;
                list.Add(Make(TokKind.Punct, start));
            }
            return list;
        }

        void SkipToEol()
        {
            while (pos < s.Length && s[pos] != '\n') pos++;
        }

        Token Make(TokKind kind, int start)
        {
            return new Token { Kind = kind, Start = start, End = pos, Text = s.Substring(start, pos - start) };
        }

        // מזהה "..." / @"..." / $"..." / $@"..." / @$"..." / """raw""" / $$"""raw"""
        Token TryString()
        {
            int i = pos;
            int dollars = 0;
            bool verbatim = false;
            while (i < s.Length && (s[i] == '$' || s[i] == '@'))
            {
                if (s[i] == '$') dollars++; else if (verbatim) return null; else verbatim = true;
                i++;
            }
            if (At(i) != '"') return null;

            int start = pos;
            int quotes = 0;
            while (At(i + quotes) == '"') quotes++;

            List<Part> parts;
            if (!verbatim && quotes >= 3)
            {
                parts = ReadRaw(i, quotes, dollars);
            }
            else
            {
                pos = i + 1;
                parts = ReadQuoted(verbatim, dollars > 0);
            }
            if (At(pos) == 'u' && At(pos + 1) == '8') pos += 2; // "..."u8

            var tok = Make(TokKind.String, start);
            tok.Parts = MergeText(parts);
            tok.IsInterpolated = dollars > 0;
            return tok;
        }

        // מחרוזת רגילה או verbatim; pos נמצא אחרי ה-" הפותח
        List<Part> ReadQuoted(bool verbatim, bool interpolated)
        {
            var parts = new List<Part>();
            var sb = new StringBuilder();
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '"')
                {
                    if (verbatim && At(pos + 1) == '"') { sb.Append('"'); pos += 2; continue; }
                    pos++;
                    break;
                }
                if (!verbatim && c == '\\') { sb.Append(ReadEscape()); continue; }
                if (!verbatim && c == '\n') break; // מחרוזת לא סגורה
                if (interpolated && c == '{')
                {
                    if (At(pos + 1) == '{') { sb.Append('{'); pos += 2; continue; }
                    Flush(parts, sb);
                    pos++;
                    parts.Add(ReadHole(1));
                    continue;
                }
                if (interpolated && c == '}' && At(pos + 1) == '}') { sb.Append('}'); pos += 2; continue; }
                sb.Append(c);
                pos++;
            }
            Flush(parts, sb);
            return parts;
        }

        // raw string literal (C# 11)
        List<Part> ReadRaw(int quoteStart, int quotes, int dollars)
        {
            string delim = new string('"', quotes);
            int contentStart = quoteStart + quotes;
            int end = s.IndexOf(delim, contentStart, StringComparison.Ordinal);
            if (end < 0) end = s.Length;
            pos = Math.Min(s.Length, end + quotes);

            // מחרוזת רב-שורתית: מורידים את השורה הראשונה והאחרונה ואת ההזחה המשותפת
            int from = contentStart, to = end;
            string indent = "";
            int firstNl = s.IndexOf('\n', contentStart);
            if (firstNl >= 0 && firstNl < end)
            {
                int lastNl = s.LastIndexOf('\n', end - 1);
                indent = s.Substring(lastNl + 1, end - lastNl - 1);
                from = firstNl + 1;
                to = lastNl > firstNl ? lastNl : from;
                if (to > from && s[to - 1] == '\r') to--;
            }

            var parts = new List<Part>();
            var sb = new StringBuilder();
            int i = from;
            bool atLineStart = indent.Length > 0;
            while (i < to)
            {
                if (atLineStart && string.CompareOrdinal(s, i, indent, 0, indent.Length) == 0) i += indent.Length;
                atLineStart = false;
                if (i >= to) break;
                char c = s[i];
                if (c == '\n') { sb.Append(c); i++; atLineStart = indent.Length > 0; continue; }
                if (dollars > 0 && c == '{')
                {
                    int run = 0;
                    while (i + run < to && s[i + run] == '{') run++;
                    if (run >= dollars)
                    {
                        sb.Append('{', run - dollars);
                        Flush(parts, sb);
                        int save = pos;
                        pos = i + run;
                        parts.Add(ReadHole(dollars));
                        i = pos;
                        pos = save;
                        continue;
                    }
                    sb.Append('{', run);
                    i += run;
                    continue;
                }
                sb.Append(c);
                i++;
            }
            Flush(parts, sb);
            return parts;
        }

        // pos נמצא אחרי ה-{ הפותח. קורא עד ה-} הסוגר (מדלג על מחרוזות מקוננות)
        Part ReadHole(int closeBraces)
        {
            int exprStart = pos;
            int depth = 0;
            int suffixStart = -1;
            while (pos < s.Length)
            {
                char c = s[pos];
                if (c == '"' || ((c == '$' || c == '@') && (At(pos + 1) == '"' || At(pos + 1) == '$' || At(pos + 1) == '@')))
                {
                    if (TryString() != null) continue;
                }
                if (c == '\'')
                {
                    pos++;
                    while (pos < s.Length && s[pos] != '\'') pos += s[pos] == '\\' ? 2 : 1;
                    pos++;
                    continue;
                }
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']') depth--;
                else if (c == '}')
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (depth == 0 && suffixStart < 0 && c == ':' && At(pos + 1) != ':' && (pos == 0 || s[pos - 1] != ':'))
                    suffixStart = pos;
                pos++;
            }

            int exprEnd = suffixStart >= 0 ? suffixStart : pos;
            string expr = s.Substring(exprStart, exprEnd - exprStart);
            string suffix = suffixStart >= 0 ? s.Substring(suffixStart, pos - suffixStart) : "";

            // יישור: {x,5} או {x,-10}
            int comma = expr.LastIndexOf(',');
            if (comma > 0)
            {
                string align = expr.Substring(comma + 1).Trim();
                int n;
                if (int.TryParse(align, out n)) { suffix = "," + align + suffix; expr = expr.Substring(0, comma); }
            }

            pos = Math.Min(s.Length, pos + closeBraces);
            return new Part { IsHole = true, Text = expr.Trim(), Suffix = suffix, Offset = exprStart };
        }

        string ReadEscape()
        {
            char n = At(pos + 1);
            pos += 2;
            switch (n)
            {
                case 'n': return "\n";
                case 'r': return "\r";
                case 't': return "\t";
                case '0': return "\0";
                case 'a': return "\a";
                case 'b': return "\b";
                case 'f': return "\f";
                case 'v': return "\v";
                case 'e': return "\u001b";
                case 'u': return HexChar(4, 4);
                case 'U': return HexChar(8, 8);
                case 'x': return HexChar(1, 4);
                default: return n.ToString(); // \" \\ \'
            }
        }

        string HexChar(int min, int max)
        {
            int len = 0;
            while (len < max && Uri.IsHexDigit(At(pos + len))) len++;
            if (len < min) return "";
            int code = Convert.ToInt32(s.Substring(pos, len), 16);
            pos += len;
            return char.ConvertFromUtf32(code);
        }

        static void Flush(List<Part> parts, StringBuilder sb)
        {
            if (sb.Length == 0) return;
            parts.Add(new Part { Text = sb.ToString() });
            sb.Clear();
        }

        static List<Part> MergeText(List<Part> parts)
        {
            var result = new List<Part>();
            foreach (Part p in parts)
            {
                if (!p.IsHole && result.Count > 0 && !result[result.Count - 1].IsHole)
                    result[result.Count - 1].Text += p.Text;
                else
                    result.Add(p);
            }
            return result;
        }
    }

    // ===================== עזרים =====================

    static bool ContainsHebrew(string text)
    {
        foreach (char c in text)
            if ((c >= '֐' && c <= '׿') || (c >= 'יִ' && c <= 'ﭏ')) return true;
        return false;
    }

    static bool HasHebrew(Token t)
    {
        return t.Parts.Any(p => !p.IsHole && ContainsHebrew(p.Text));
    }

    static string OneLine(string code)
    {
        var sb = new StringBuilder();
        bool space = false;
        foreach (char c in code)
        {
            if (c == '\r' || c == '\n' || c == '\t' || c == ' ') { space = true; continue; }
            if (space && sb.Length > 0) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    static string CSharpLiteral(string value)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.Append('"').ToString();
    }

    static string Suggested(Occurrence o)
    {
        if (o.Args.Count == 0) return HelperClassName + ".Get(" + CSharpLiteral(o.Key) + ")";
        return HelperClassName + ".Format(" + CSharpLiteral(o.Key) + ", " + string.Join(", ", o.Args) + ")";
    }

    // ===================== כתיבת RESX =====================

    static int WriteResx(string path, List<Occurrence> found)
    {
        // ערכים קיימים (אם כבר ערכת את ה-Value בקובץ קודם - הם נשמרים)
        var existing = new Dictionary<string, string>(StringComparer.Ordinal);
        var order = new List<string>();
        if (File.Exists(path))
        {
            foreach (XElement d in XDocument.Load(path).Root.Elements("data"))
            {
                string name = (string)d.Attribute("name");
                if (name == null || existing.ContainsKey(name)) continue;
                existing[name] = (string)d.Element("value") ?? "";
                order.Add(name);
            }
        }

        var locations = found
            .GroupBy(o => o.Key, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        int added = 0;
        foreach (Occurrence o in found)
        {
            if (existing.ContainsKey(o.Key)) continue;
            existing[o.Key] = o.Key;
            order.Add(o.Key);
            added++;
        }

        XNamespace xml = XNamespace.Xml;
        var root = new XElement("root",
            ResHeader("resmimetype", "text/microsoft-resx"),
            ResHeader("version", "2.0"),
            ResHeader("reader", "System.Resources.ResXResourceReader, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"),
            ResHeader("writer", "System.Resources.ResXResourceWriter, System.Windows.Forms, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"));

        foreach (string key in order)
        {
            var data = new XElement("data",
                new XAttribute("name", key),
                new XAttribute(xml + "space", "preserve"),
                new XElement("value", existing[key]));

            List<Occurrence> occ;
            if (locations.TryGetValue(key, out occ))
            {
                var where = occ.Take(5).Select(o => o.File + ":" + o.Line).ToList();
                if (occ.Count > 5) where.Add("+" + (occ.Count - 5) + " more");
                string args = occ[0].Args.Count > 0
                    ? "Args: " + string.Join(" | ", occ[0].Args.Select((a, i) => "{" + i + "}=" + a)) + "\n"
                    : "";
                data.Add(new XElement("comment", args + string.Join("\n", where)));
            }
            else
            {
                data.Add(new XElement("comment", "NOT FOUND in last scan"));
            }
            root.Add(data);
        }

        var settings = new System.Xml.XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(true) };
        using (var w = System.Xml.XmlWriter.Create(path, settings))
            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(w);
        return added;
    }

    static XElement ResHeader(string name, string value)
    {
        return new XElement("resheader", new XAttribute("name", name), new XElement("value", value));
    }

    // ===================== כתיבת דוח CSV =====================

    static void WriteReport(string path, List<Occurrence> found)
    {
        var sb = new StringBuilder();
        sb.AppendLine("File,Line,Kind,Key,Args,Original,Suggested");
        foreach (Occurrence o in found)
        {
            sb.AppendLine(string.Join(",", new[]
            {
                Csv(o.File), o.Line.ToString(), o.Kind, Csv(o.Key), Csv(string.Join(" | ", o.Args)), Csv(o.Original), Csv(Suggested(o))
            }));
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true)); // BOM כדי שאקסל יציג עברית
    }

    static string Csv(string v)
    {
        return "\"" + v.Replace("\"", "\"\"") + "\"";
    }
}

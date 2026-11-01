// ServerMessages
// --------------
// צד לקוח: מקבל שגיאה שחזרה מהשרת (FaultException של WCF / SOAP וכו'),
// מוצא בתוכה את ההודעה המקורית בעברית, מחפש אותה בקובץ ה-RESX
// ומחזיר את ה-Value שלה.
//
// במקום לנחש איפה מתחילה ונגמרת העברית בתוך הטקסט הארוך, הקלאס בודק
// אילו מה-Keys שב-RESX מופיעים בתוכו. Key עם {0} הופך לתבנית חיפוש,
// כך ש"לא ניתן לבצע את הפעולה עבור העץ אלון" נמצא לפי
// "לא ניתן לבצע את הפעולה עבור העץ {0}", והערך "אלון" מוצב ב-Value.
//
// שימוש:
//   var messages = ServerMessages.FromResxFile("Messages.resx");
//   // או: new ServerMessages(Properties.Resources.ResourceManager)  (RESX שמוטמע בפרויקט)
//   catch (Exception ex) { MessageBox.Show(messages.Translate(ex)); }
//
// קלאס אחד, בלי תלויות חיצוניות (C# 7.3 ומעלה, ‎.NET Framework 4.x או ‎.NET 6+).

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Resources;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

public class ServerMessages
{
    /// <summary>מה מוחזר כשהשגיאה ריקה לגמרי (אין בה שום טקסט)</summary>
    public string DefaultMessage { get; set; } = "אירעה שגיאה, נסו שוב מאוחר יותר";

    class Entry
    {
        public string Key;
        public string Value;
        public Regex Pattern;   // null כשאין ב-Key משתנים
        public int LiteralLength;
    }

    readonly List<Entry> entries = new List<Entry>();

    // ===================== טעינה =====================

    /// <summary>טעינה מקובץ RESX על הדיסק</summary>
    public static ServerMessages FromResxFile(string path)
    {
        var pairs = XDocument.Load(path).Root.Elements("data")
            .Where(d => d.Attribute("type") == null) // רק מחרוזות
            .Select(d => new KeyValuePair<string, string>((string)d.Attribute("name"), (string)d.Element("value") ?? ""));
        return new ServerMessages(pairs);
    }

    /// <summary>טעינה מ-RESX שמוטמע בפרויקט (למשל Properties.Resources.ResourceManager)</summary>
    public ServerMessages(ResourceManager resources)
        : this(ReadAll(resources))
    {
    }

    public ServerMessages(IEnumerable<KeyValuePair<string, string>> keyValues)
    {
        foreach (var kv in keyValues)
        {
            if (string.IsNullOrWhiteSpace(kv.Key)) continue;
            entries.Add(BuildEntry(kv.Key, kv.Value ?? kv.Key));
        }
        // ההתאמה הארוכה ביותר מנצחת ("העץ לא נמצא בתיקייה" לפני "העץ לא נמצא")
        entries.Sort((a, b) => b.LiteralLength.CompareTo(a.LiteralLength));
    }

    static IEnumerable<KeyValuePair<string, string>> ReadAll(ResourceManager rm)
    {
        ResourceSet set = rm.GetResourceSet(CultureInfo.CurrentUICulture, true, true);
        foreach (DictionaryEntry e in set)
        {
            var value = e.Value as string;
            if (value != null) yield return new KeyValuePair<string, string>((string)e.Key, value);
        }
    }

    // ===================== תרגום =====================

    /// <summary>מחזיר את ה-Value מה-RESX להודעה שבתוך השגיאה</summary>
    public string Translate(Exception ex)
    {
        string result;
        if (TryTranslate(ex, out result)) return result;
        // לא ב-RESX: ההודעה בעברית, ואם אין - ההודעה המקורית מהשרת (גם באנגלית)
        return ExtractHebrew(ex)
            ?? CandidateTexts(ex).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
            ?? DefaultMessage;
    }

    /// <summary>מחזיר את ה-Value מה-RESX להודעה שבתוך טקסט השגיאה</summary>
    public string Translate(string errorText)
    {
        string result;
        if (TryTranslate(errorText, out result)) return result;
        return ExtractHebrew(errorText)
            ?? (string.IsNullOrWhiteSpace(errorText) ? DefaultMessage : errorText);
    }

    public bool TryTranslate(Exception ex, out string result)
    {
        foreach (string text in CandidateTexts(ex))
            if (TryTranslate(text, out result)) return true;
        result = null;
        return false;
    }

    public bool TryTranslate(string errorText, out string result)
    {
        result = null;
        if (string.IsNullOrEmpty(errorText)) return false;

        foreach (string text in new[] { errorText, WebUtility.HtmlDecode(errorText) }.Distinct())
        {
            foreach (Entry e in entries)
            {
                if (e.Pattern == null)
                {
                    if (text.IndexOf(e.Key, StringComparison.Ordinal) >= 0) { result = e.Value; return true; }
                    continue;
                }
                // ההודעה מופיעה לפעמים כמה פעמים (למשל עם "(Fault Detail is equal to..." אחריה) -
                // לוקחים את ההופעה עם הערכים הקצרים ביותר
                Match best = null;
                foreach (Match m in e.Pattern.Matches(text))
                    if (best == null || ArgsLength(m) < ArgsLength(best)) best = m;
                if (best == null) continue;
                var args = new List<object>();
                for (int i = 0; best.Groups["a" + i].Success; i++) args.Add(best.Groups["a" + i].Value.Trim());
                result = SafeFormat(e.Value, args.ToArray());
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// בלי RESX: מחזיר את השורה הראשונה בעברית מתוך השגיאה (או null).
    /// שימושי להודעות שעוד לא נכנסו ל-RESX.
    /// </summary>
    public static string ExtractHebrew(Exception ex)
    {
        return CandidateTexts(ex).Select(ExtractHebrew).FirstOrDefault(s => s != null);
    }

    public static string ExtractHebrew(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        text = WebUtility.HtmlDecode(text);
        foreach (string line in text.Split('\r', '\n'))
        {
            // מהאות העברית הראשונה ועד סוף השורה / תגית XML
            Match m = Regex.Match(line, @"[֐-׿][^<]*");
            if (m.Success) return m.Value.Trim().TrimEnd('"', '\'');
        }
        return null;
    }

    // ===================== עזרים =====================

    // הטקסטים שבהם מחפשים, מהמדויק לכללי:
    // FaultException<ExceptionDetail>.Detail (ההודעה המקורית מהשרת), Message של כל שרשרת ה-InnerException,
    // ובסוף ex.ToString() המלא.
    static IEnumerable<string> CandidateTexts(Exception ex)
    {
        for (Exception e = ex; e != null; e = e.InnerException)
        {
            // WCF: FaultException<ExceptionDetail>.Detail.Message / Detail.InnerException.Message
            // (דרך Reflection כדי לא לחייב הפניה ל-System.ServiceModel)
            object detail = e.GetType().GetProperty("Detail") != null ? e.GetType().GetProperty("Detail").GetValue(e, null) : null;
            for (int guard = 0; detail != null && guard < 10; guard++)
            {
                var msg = detail.GetType().GetProperty("Message");
                if (msg != null) yield return msg.GetValue(detail, null) as string;
                var inner = detail.GetType().GetProperty("InnerException");
                detail = inner != null ? inner.GetValue(detail, null) : null;
            }
            yield return e.Message;
        }
        yield return ex.ToString();
    }

    static Entry BuildEntry(string key, string value)
    {
        var entry = new Entry { Key = key, Value = value };
        // {0} / {0,5} / {1:N2} ; {{ ו-}} הם סוגריים רגילים
        var holes = Regex.Matches(key, @"\{\{|\}\}|\{(\d+)(,-?\d+)?(:[^}]*)?\}");
        if (!holes.Cast<Match>().Any(h => h.Groups[1].Success))
        {
            entry.Key = key; // אין משתנים - חיפוש טקסט רגיל
            entry.LiteralLength = key.Length;
            return entry;
        }

        var pattern = new StringBuilder();
        int last = 0, literal = 0;
        var seen = new HashSet<string>();
        for (int i = 0; i < holes.Count; i++)
        {
            Match h = holes[i];
            string text = key.Substring(last, h.Index - last);
            pattern.Append(Regex.Escape(text));
            literal += text.Length;
            last = h.Index + h.Length;

            if (!h.Groups[1].Success) { pattern.Append(Regex.Escape(h.Value.Substring(1))); literal++; continue; }

            string name = "a" + h.Groups[1].Value;
            bool first = h.Index == 0;
            bool lastHole = last == key.Length;
            if (seen.Contains(name)) pattern.Append(@"\k<" + name + ">"); // אותו משתנה פעמיים
            else if (first) pattern.Append("(?<" + name + @">[^\s:>""']+)");  // בתחילת ההודעה: מילה אחת
            else if (lastHole) pattern.Append("(?<" + name + @">[^\r\n<""]*)"); // בסוף: עד סוף השורה / תגית
            else pattern.Append("(?<" + name + @">[^\r\n<]*?)");
            seen.Add(name);
        }
        string tail = key.Substring(last);
        pattern.Append(Regex.Escape(tail));
        literal += tail.Length;

        entry.Pattern = new Regex(pattern.ToString(), RegexOptions.CultureInvariant);
        entry.LiteralLength = literal;
        return entry;
    }

    static int ArgsLength(Match m)
    {
        int len = 0;
        for (int i = 0; m.Groups["a" + i].Success; i++) len += m.Groups["a" + i].Length;
        return len;
    }

    static string SafeFormat(string format, object[] args)
    {
        try
        {
            int needed = Regex.Matches(format, @"(?<!\{)\{(\d+)").Cast<Match>().Select(m => int.Parse(m.Groups[1].Value) + 1).DefaultIfEmpty(0).Max();
            if (args.Length < needed) args = args.Concat(Enumerable.Repeat((object)"", needed - args.Length)).ToArray();
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch (FormatException)
        {
            return format;
        }
    }
}

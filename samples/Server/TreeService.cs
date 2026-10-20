using System;
#region הודעות - לא אמור להיחלץ
#endregion
namespace Demo
{
    // הערה בעברית - לא אמורה להיחלץ
    public class TreeService
    {
        const string NotFound = "העץ לא נמצא";
        public string ErrorMessage { get; set; }

        public void Run(string name, int count, Tree tree, Item[] items, int a, int b)
        {
            /* "גם זה בהערה" */
            if (name == null) throw new Exception("שם העץ חסר");
            throw new Exception("לא ניתן לבצע את הפעולה עבור העץ " + name);
            throw new InvalidOperationException("העץ " + tree.Name + " כבר קיים בתיקייה " + tree.Parent?.Name + ".");
            ErrorMessage = $"נמצאו {count} שגיאות בעץ {tree.GetName()}";
            ErrorMessage = $"סכום: {count,5:N2} ({(count > 1 ? "רבים" : "יחיד")})";
            throw new MyException("שגיאה בשורה " + (count + 1) + " בקובץ " + items[0].Name);
            ErrorMessage = count + 1 + " פריטים נמחקו";
            ErrorMessage = "ערך " + a * b + " לא חוקי";
            ErrorMessage = string.Format("העץ {0} נמחק", name);
            ErrorMessage = @"נתיב ""שגוי"" C:\temp";
            ErrorMessage = "שורה\tעם \"מירכאות\"";
            ErrorMessage = "Hello " + name; // לא עברית
            ErrorMessage = "מחרוזת עם {סוגריים} ו-" + name;
            ErrorMessage = (string)Get("x") + " - " + "חסר ערך";
            ErrorMessage = $$"""
                עץ {{name}} עם {סוגריים}
                שורה שנייה
                """;
            var x = NotFound;
            ErrorMessage = "העץ לא נמצא";
        }
    }
}

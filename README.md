# ExtractMessagesToResx

כלי קונסול (קלאס אחד – `Program.cs`) שסורק פרויקט C#, מוצא כל מחרוזת עם עברית ויוצר `Messages.resx` שבו Key = Value = ההודעה.

## הרצה
```
dotnet run -- <תיקיית הפרויקט> [תיקיית פלט]
```
או: להעתיק את `Program.cs` לכל Console App (‎.NET Framework 4.x / ‎.NET 6+) ולהריץ.

## פלט
- `Messages.resx` – כל הודעה פעם אחת. בהערה (comment): איפה היא מופיעה ומה כל `{0}`. הרצה חוזרת **שומרת ערכים שערכת**.
- `Messages.report.csv` – כל מופע: קובץ, שורה, סוג, Key, משתנים, הקוד המקורי וקוד מוצע להחלפה (נפתח באקסל).

## הודעות עם משתנים
| בקוד | Key ב-RESX | קוד מוצע |
|---|---|---|
| `"לא ניתן לבצע את הפעולה עבור העץ " + name` | `לא ניתן לבצע את הפעולה עבור העץ {0}` | `Messages.Format("…{0}", name)` |
| `$"נמצאו {count} שגיאות בעץ {tree.Name}"` | `נמצאו {0} שגיאות בעץ {1}` | `Messages.Format("…", count, tree.Name)` |
| `count + 1 + " פריטים"` | `{0} פריטים` | `Messages.Format("{0} פריטים", count + 1)` |

## קלאס עזר לפרויקט השרת
```csharp
public static class Messages
{
    static readonly ResourceManager Rm =
        new ResourceManager("MyProject.Resources.Messages", typeof(Messages).Assembly);

    public static string Get(string key) => Rm.GetString(key) ?? key;

    public static string Format(string key, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, Get(key), args);
}
```

## צד לקוח: `Client/ServerMessages.cs`
שגיאה מהשרת (WCF / SOAP) מגיעה לקליינט עטופה בטקסט ארוך באנגלית. הקלאס מוצא בתוכה איזה Key מה-RESX מופיע ומחזיר את ה-Value שלו.
Key עם `{0}` הופך לתבנית, כך ש"…עבור העץ אלון" נמצא לפי "…עבור העץ {0}", והערך "אלון" מוצב ב-Value.

```csharp
var messages = ServerMessages.FromResxFile("Messages.resx");
// או מ-RESX שמוטמע בפרויקט: new ServerMessages(Properties.Resources.ResourceManager)

try { client.DoSomething(); }
catch (Exception ex) { MessageBox.Show(messages.Translate(ex)); }
```
- קודם נבדק `FaultException<ExceptionDetail>.Detail` (ההודעה המקורית מהשרת), אחר כך ה-Message של כל שרשרת ה-InnerException ובסוף `ex.ToString()`.
- הודעה שלא נמצאה ב-RESX: מוחזרת השורה הראשונה בעברית מתוך השגיאה; אם אין עברית: ההודעה המקורית מהשרת (גם באנגלית); רק שגיאה ריקה לגמרי מחזירה `DefaultMessage`.
- `Translate(string)` עובד גם על טקסט שגיאה גולמי.

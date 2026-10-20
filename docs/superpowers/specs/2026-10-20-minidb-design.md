# MiniDb: מנוע מסד נתונים קטן ב-C# — מסמך תכנון

תאריך: 2026-10-20
סטטוס: טיוטה, ממתינה לאישור המשתמש

## 1. מטרה

לבנות מנוע מסד נתונים משלו, בלי PostgreSQL או SQLite כמנוע מרכזי, שמדגים:
- אחסון נתונים בקבצים עם טעינה מחדש.
- אינדקס B+ tree לחיפוש מהיר.
- טרנזקציות והתאוששות מקריסה בעזרת WAL.
- ממשק C# ישיר, ושפת שאילתות בסיסית דמוית SQL.
- חיסכון במשאבים (זיכרון, קריאות/כתיבות לדיסק, הקצאות) עם מדידה במספרים.
- קוד מסודר: פונקציות קצרות, שכבות ברורות, כך שמתכנתים אחרים יוכלו להמשיך לעבוד עליו.

**מחוץ לתחום (YAGNI):** גישה ממספר תהליכים במקביל, רשת/שרת, JOIN, GROUP BY, ALTER TABLE, MVCC, הצפנה, דחיסה, VACUUM. אלה רשומים כהרחבות עתידיות בלבד.

## 2. סביבה וכלים

- שפה: C#, .NET 8 (LTS). ניתן לשדרג בהמשך.
- `Nullable` מופעל, `TreatWarningsAsErrors` מופעל ב-Core.
- בדיקות: xUnit.
- מדידות: BenchmarkDotNet, כולל `MemoryDiagnoser`.
- השוואת ייחוס ב-benchmarks: SQLite דרך `Microsoft.Data.Sqlite` (לא כתלות של Core).
- עיצוב קוד: `.editorconfig` ו-`dotnet format`.

## 3. מבנה הפרויקט

```
NewDatabase/
  MiniDb.sln
  src/
    MiniDb.Core/          המנוע
    MiniDb.Cli/           אפליקציית קונסול להדגמה
  tests/
    MiniDb.Tests/         בדיקות יחידה, אינטגרציה והתאוששות
  benchmarks/
    MiniDb.Benchmarks/    מדידות ביצועים וזיכרון
  docs/superpowers/specs/ מסמכי תכנון
```

### שכבות ב-Core

```
Api  →  Query  →  Index / Transactions  →  Storage
```

כל שכבה תלויה רק בשכבה שמתחתיה. Storage אינה יודעת על טבלאות, ו-Index אינו יודע על SQL.

| תיקייה ב-Core | אחריות | ממשקים מרכזיים |
|---|---|---|
| `Storage/` | קריאה וכתיבה של דפים, Buffer pool, checksum | `IPageStore`, `IBufferPool` |
| `Index/` | B+ tree מעל דפים | `IBPlusTree` |
| `Transactions/` | WAL, commit, recovery, checkpoint | `IWriteAheadLog`, `ITransaction` |
| `Catalog/` | סכמות טבלאות ושמירתן | `ICatalog` |
| `Records/` | סריאליזציה של שורות ומבנה slotted page | `IRecordCodec` |
| `Query/` | tokenizer, parser, AST, planner, executor | `IQueryExecutor` |
| `Api/` | נקודת הכניסה הציבורית | `MiniDatabase`, `Table` |

## 4. פורמט הקבצים

- **קובץ נתונים** `*.mdb`: רצף דפים בגודל קבוע של **4096 בייט**. מספר דף הוא `pageId`, והמיקום בקובץ הוא `pageId * 4096`.
- **קובץ יומן** `*.wal`: רצף רשומות WAL (סעיף 7).

### דף 0: כותרת הקובץ
| שדה | גודל | תיאור |
|---|---|---|
| magic | 8 | `MINIDB01` |
| format version | 4 | גרסה, מתחיל ב-1 |
| page size | 4 | 4096 |
| page count | 8 | מספר הדפים הכולל |
| catalog root page | 8 | דף השורש של קטלוג הטבלאות |
| free list head | 8 | דף פנוי ראשון (0 אם אין) |
| checksum | 4 | CRC32 של שאר הדף |

### דף נתונים (slotted page)
| שדה | גודל | תיאור |
|---|---|---|
| page type | 1 | Header, Catalog, Leaf, Internal, Heap, Free |
| page id | 8 | למניעת דפים שהוקצו לא נכון |
| checksum | 4 | CRC32 על הדף ללא השדה הזה |
| slot count | 2 | מספר ה-slots |
| free space offset | 2 | תחילת אזור הנתונים הפנוי |
| next page | 8 | לרשימות וקישור עלים |

מבנה slotted: מערך ה-slots גדל מההתחלה, והרשומות גדלות מהסוף. כל slot הוא (offset, length), וslot עם length = 0 הוא רשומה שנמחקה.

## 5. Storage וניהול זיכרון

- **IPageStore**: `ReadPage(pageId, Span<byte>)`, `WritePage(pageId, ReadOnlySpan<byte>)`, `AllocatePage()`, `FreePage(pageId)`, `Sync()`. מימוש אחד לקובץ, ומימוש זיכרון לבדיקות.
- **Buffer pool**: LRU בגודל קבוע (ברירת מחדל 256 דפים, כלומר 1MB). דף שנמצא בשימוש מסומן כ-pinned ולא מפונה. דף ששונה מסומן dirty. דף dirty של טרנזקציה פעילה לא מפונה לדיסק לעולם (ראו סעיף 8). אם כל הדפים תפוסים, הטרנזקציה נכשלת עם שגיאה ברורה ולא מגדילה את הזיכרון בשקט.
- **חיסכון במשאבים:**
  - כל הקריאות והכתיבות הן לפי דף שלם, לא לפי רשומה.
  - מאגר הדפים בגודל קבוע, כך שהזיכרון לא גדל עם גודל הנתונים.
  - `ArrayPool<byte>` לבאפרים זמניים, ו-`Span<T>` כדי להימנע מהעתקות.
  - תוצאות שאילתה מוחזרות כ-`IEnumerable` עם `yield return`, כך שלא נטענת קבוצה מלאה לזיכרון.
  - `ITransaction` הוא מבנה קטן שמחזיק רק את הדפים שהשתנו.

## 6. Records ו-Catalog

- **סכמה**: שמות עמודות וטיפוסים. הטיפוסים הנתמכים: `Int32`, `Int64`, `Double`, `Bool`, `String` (UTF-8, אורך מקסימלי מוגדר).
- **מפתח ראשי**: כל טבלה חייבת עמודת `Int64` אחת כמפתח ראשי. ה-PK מקבל B+ tree משלו.
- **אינדקס משני**: אופציונלי, על עמודה אחת. ערך האינדקס מצביע על ה-PK.
- **סריאליזציה**: עמודה קבועה-גודל נכתבת ישירות. מחרוזת נכתבת עם prefix של אורך (2 בייט). שורה נכתבת לסדר העמודות בסכמה.
- **Catalog**: טבלה מיוחדת שנשמרת בדפים עצמם. כל רשומה בה מתארת טבלה: שם, סכמה, שורש ה-B+ tree של ה-PK, ושורשי האינדקסים המשניים.

**מיקום שורה:** `RowLocation(pageId, slotIndex)`. ה-B+ tree של ה-PK מחזיר מיקום, והשורה נקראת מהדף.

## 7. B+ Tree

- **עלים**: זוגות `(key, RowLocation)`, מסודרים לפי key, עם מצביע `next page` לעלה הבא (לסריקות טווח).
- **צמתים פנימיים**: מפתחות ומצביעים לילדים.
- **פעולות**: `Find(key)`, `Insert(key, location)`, `Delete(key)`, `Range(from, to)`.
- **פיצול**: כאשר דף מתמלא, הוא מתפצל לשניים והמפתח האמצעי עולה לאב. אם השורש מתפצל, נוצר שורש חדש.
- **מחיקה**: מסירה מהעלה בלבד, בלי מיזוג. זו בחירה מודעת לפשטות. הערה: דפים ריקים לא יוחזרו לרשימה החופשית עד VACUUM עתידי.
- **בדיקה**: כל פעולה נבדקת מול `SortedDictionary` כמודל ייחוס, עם פעולות אקראיות (fuzz).

## 8. Transactions ו-WAL

**מודל:** דפים שמשתנים נשארים בזיכרון בתוך הטרנזקציה עד ה-commit. דפים מלוכלכים לא נכתבים לקובץ הנתונים לפני commit (no-steal). לכן אין צורך ב-undo log.

**פורמט WAL:** רשומות (frames) עם:
- `transactionId` (8), `type` (1: PageImage, Commit), `pageId` (8), `payloadLength` (4), `payload` (4096 bytes ל-PageImage, אפס ל-Commit), `checksum` (4, CRC32 על כל הרשומה).

**Commit:**
1. כתיבת PageImage לכל דף מלוכלך ב-WAL.
2. כתיבת רשומת Commit.
3. `fsync` על קובץ ה-WAL (ברירת מחדל: מופעל, וניתן לכיבוי לצורך מדידה).
4. הטרנזקציה נחשבת מחויבת.

**Checkpoint:** העתקת הדפים האחרונים מה-WAL לקובץ הנתונים, `fsync` לקובץ הנתונים, ואז קיצוץ ה-WAL לאפס. ניתן להפעיל ידנית או אוטומטית מעל סף גודל.

**Recovery (בפתיחה):**
1. אם יש WAL, קוראים אותו מההתחלה.
2. רשומה עם checksum שגוי או חתוכה בסוף הקובץ נחשבת "קרע" (torn write): קוראים עד אליה ועוצרים.
3. מיישמים רק טרנזקציות שיש להן רשומת Commit. טרנזקציות ללא Commit מתעלמות.
4. מבצעים checkpoint, ומקצצים את ה-WAL.

**בידוד:** RW lock פשוט: קורא אחד או יותר במקביל, או כותב אחד בכל רגע. כלומר רמת בידוד Serializable, ובפשטות גבוהה.

## 9. Query (שלב שני)

**שפה נתמכת:**
```sql
CREATE TABLE name (col TYPE [PRIMARY KEY] [INDEX], ...)
INSERT INTO name VALUES (v1, v2, ...)
SELECT * | col, ... FROM name [WHERE col op value] [LIMIT n]
DELETE FROM name WHERE col op value
```
`op` הוא אחד מ: `=`, `<`, `<=`, `>`, `>=`. תנאי יחיד בלבד.

**Pipeline:**
1. **Tokenizer**: מחרוזת → רשימת Tokens.
2. **Parser**: recursive descent, מייצר AST.
3. **Planner**: אם ה-WHERE על עמודת PK או אינדקס משני, משתמש ב-B+ tree. אחרת סריקה מלאה.
4. **Executor**: מודל pull (`IEnumerable`), שורה אחת בכל פעם.

שגיאות תחביר מחזירות `SqlSyntaxException` עם מיקום בשאילתה.

## 10. API ציבורי (Api)

```csharp
using var db = MiniDatabase.Open("shop.mdb", options);

db.CreateTable(new TableSchema("products", ...));
db.Insert("products", row);
Row? item = db.Get("products", id);
IEnumerable<Row> rows = db.Scan("products", where);

using var tx = db.BeginTransaction();
tx.Insert(...);
tx.Commit();

db.Execute("SELECT * FROM products WHERE price < 100");
```

- `MiniDatabase` הוא `IDisposable`, והסגירה מבצעת checkpoint.
- `Options` כולל: גודל buffer pool, `FsyncOnCommit`, סף checkpoint.
- כל החריגות יורשות מ-`MiniDbException`. אין כשלים שקטים.

## 11. סטנדרטי קוד (לשמירה על קוד קריא לאחרים)

- פונקציה: עד כ-30 שורות. מעבר לכך נחשב סימן לפיצול.
- מחלקה או interface: אחריות אחת. קובץ אחד לכל טיפוס ציבורי.
- אין static state הניתן לשינוי. תלויות מוזרקות דרך constructor.
- שמות מלאים ומשמעותיים, ללא קיצורים (`pageId`, לא `pid`).
- XML doc comments לכל API ציבורי.
- Magic numbers כקבועים עם שם (למשל `PageSize = 4096`).
- README קצר בשורש הפרויקט עם איך מריצים, איך בודקים, ומפת השכבות.

## 12. בדיקות

- **יחידה**: לכל שכבה, עם `IPageStore` בזיכרון ככל האפשר.
- **B+ tree fuzz**: פעולות אקראיות מול `SortedDictionary`, עם בדיקת תקינות המבנה אחרי כל פעולה.
- **התאוששות**: סימולציה של קריסה בנקודות שונות:
  - אחרי כתיבת WAL ולפני Commit: הטרנזקציה נעלמת.
  - אחרי Commit ולפני checkpoint: הטרנזקציה נשמרת.
  - חיתוך של רשומת WAL באמצע: הרשומה מתעלמת ללא שגיאה.
  - checksum שגוי בדף: זיהוי ושגיאה ברורה.
- **Query**: טסטים לכל צורת שאילתה נתמכת, וטסטי תחביר שגוי.

## 13. מדידות (חיסכון במשאבים)

BenchmarkDotNet עם `MemoryDiagnoser`, על N = 10,000 ו-100,000 רשומות:
- הכנסה (עם ובלי fsync).
- חיפוש נקודתי לפי PK.
- סריקת טווח.
- זיכרון מוקצה לכל פעולה.
- מספר קריאות וכתיבות לדיסק (מונה ב-`IPageStore`).

השוואה: אותן פעולות על SQLite. המטרה היא להציג את המספרים ולא לטעון לעקיפה של SQLite. מדידה לפני ואחרי כל אופטימיזציה, כדי שכל חיסכון יוכח במספר.

## 14. קריטריונים להצלחה

1. הכנסה, חיפוש ומחיקה עוברים את בדיקות ה-fuzz מול מודל הייחוס.
2. לאחר קריסה מדומה, המצב אחרי ההתאוששות תואם בדיוק לטרנזקציות שחויבו.
3. שאילתת SQL בסיסית עובדת מה-CLI, ומשתמשת באינדקס כשמתאים.
4. זיכרון המנוע חסום בגודל ה-buffer pool, גם עם נתונים גדולים מהזיכרון.
5. המדידות מוצגות ב-README עם הסבר.

## 15. שלבי מימוש (ייכנסו לתוכנית נפרדת)

1. Storage: קובץ דפים, header, buffer pool, checksum.
2. Records ו-Catalog: סכמה, סריאליזציה, יצירת טבלה.
3. B+ tree: find, insert עם פיצול, scan, delete.
4. Transactions: WAL, commit, checkpoint, recovery, ובדיקות קריסה.
5. Api: `MiniDatabase`, `Table`, `Transaction`.
6. Query: tokenizer, parser, planner, executor.
7. CLI והדגמה.
8. Benchmarks ו-README.

## 16. החלטות שנלקחו בטיוטה (ניתנות לשינוי)

- גודל דף 4KB, buffer pool של 256 דפים.
- מחיקה בלי מיזוג דפים.
- טרנזקציה אחת בכל רגע (RW lock), בלי MVCC.
- WAL של דפים שלמים (page images), ולא לוג לוגי. פשוט יותר ואמין יותר להתאוששות.
- תנאי WHERE יחיד בשאילתות.
- מפתח ראשי מסוג `Int64` בלבד.

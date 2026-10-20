# MiniDb

מנוע מסד נתונים קטן ב-C#, נבנה מאפס כפרויקט לימודי. תכנון מלא: [docs/superpowers/specs/2026-10-20-minidb-design.md](docs/superpowers/specs/2026-10-20-minidb-design.md). תוכנית המימוש: [docs/superpowers/plans/2026-10-20-minidb.md](docs/superpowers/plans/2026-10-20-minidb.md).

## מה יש בפנים

- **Storage**: קובץ דפים של 4 KB עם CRC-32 בסוף כל דף, ו-buffer pool (LRU) בגודל קבוע. עמודים שהשתנו בטרנזקציה נשארים בבריכה עד פינוי או checkpoint.
- **Database**: דף כותרת עם magic `MINIDB01`, ובדיקת שלמות בפתיחה.
- **Records**: סריאליזציה של שורות (Int32, Int64, Double, Bool, String ב-UTF-8). עמודות ברוחב קבוע באות ראשונות, כך שכל אחת נקראת ממיקום ידוע. ה-API מחזיר `RowView` שמפענח עמודה רק לפי דרישה.
- **Catalog**: הגדרות טבלאות, נשמרות בדפים עצמם, עם מטמון בזיכרון.
- **Index**: B+ tree על מפתח ראשי `Int64`, עם פיצול עלים ופנימיים וסריקת טווח.
- **Transactions**: WAL של דפים שלמים, commit עם fsync (אפשר לכבות), checkpoint, והתאוששות מקריסה (טרנזקציות ללא commit מתעלמות, וקרע בסוף היומן נחתך).
- **API**: `MiniDatabase`, `Table`, `RowView` בקוד C#.
- **SQL**: `CREATE TABLE`, `INSERT`, `SELECT ... WHERE ... LIMIT`, `DELETE ... WHERE`, עם planner שבוחר בין חיפוש נקודתי, סריקת טווח ב-B+ tree, וסריקה מלאה. סינון נעשה על `RowView`, כך שכל השוואה קוראת רק את העמודה שבתנאי.

## הרצה

```bash
dotnet run --project src/MiniDb.Cli -- shop.mdb
```

```sql
CREATE TABLE users (id BIGINT PRIMARY KEY, name VARCHAR(50))
INSERT INTO users VALUES (1, 'שלום')
SELECT * FROM users WHERE id >= 1 LIMIT 10
DELETE FROM users WHERE id = 1
```

## בדיקות

```bash
dotnet test tests/MiniDb.Tests
```

108 בדיקות: שכבה לשכבה, כולל בדיקת fuzz של ה-B+ tree מול `SortedDictionary`, בדיקות התאוששות מקריסה ו-torn tail, בדיקות checksum, ובדיקות SQL מקצה לקצה.

## השוואה למסדי נתונים אחרים

הריצה: BenchmarkDotNet, Release, Windows 11, .NET 8, מכונה אחת. כל המנועים על אותם נתונים (`id BIGINT`, `name` מחרוזת, `score INT` עם ערך 7 בכ-10% מהשורות), ללא אינדקס משני בכל אחד מהם. תנאים שווים בכל המנועים:

- **MiniDb** בהגדרות ברירת המחדל (מאגר של 256 דפים, 1 MB). הטעינה בקבוצות של 2,000 שורות לטרנזקציה.
- **SQLite** במצב WAL עם `synchronous=FULL`, מטמון ברירת המחדל (2 MB). קריאות רצות בתוך transaction.
- **LiteDB** בהגדרות ברירת המחדל. לא בדקתי אם הוא מבצע fsync לכל commit.
- **MongoDB** על localhost, `w:1, journal:true`, בלי אימות. כולל overhead של רשת ו-BSON.
- **PostgreSQL** לא נכלל: הוא דורש סיסמה שאין לי.

**הכנסה batch: 10,000 שורות בטרנזקציה אחת**

| מנוע | זמן | הקצאות |
|---|---|---|
| SQLite | 18.4 ms | 5.6 MB |
| MiniDb | 24.5 ms | 8.1 MB |
| MongoDB | 60.8 ms | 5.5 MB |
| LiteDB | 172 ms | 114 MB |

**הכנסה autocommit: 1,000 טרנזקציות נפרדות**

| מנוע | זמן | הערה |
|---|---|---|
| LiteDB | 72.5 ms | durability לא נבדק |
| SQLite | 357 ms | WAL, `synchronous=FULL` |
| MiniDb | 647 ms | fsync לכל commit. רעש גדול (סטיית תקן 290 ms) |
| MongoDB | 734 ms | `journal:true` |

**חיפוש נקודתי לפי מפתח, 100,000 שורות (לכל קריאה)**

| מנוע | זמן | הערה |
|---|---|---|
| SQLite | 1.65 µs | SQL ו-prepared statement, בתוך transaction |
| MiniDb | 10.7 µs | API בתוך התהליך, בלי SQL |
| LiteDB | 28.6 µs | |
| MongoDB | 311 µs | דרך הרשת |

**סריקה מלאה, 100,000 שורות**

| מנוע | זמן | הקצאות |
|---|---|---|
| MiniDb | 6.3 ms | 5.5 MB |
| SQLite | 15.4 ms | 1.7 KB |
| LiteDB | 80 ms | 147 MB |
| MongoDB | 183 ms | 36 MB |

**סריקה מסוננת (`score = 7`), 100,000 שורות**

| מנוע | זמן | הקצאות |
|---|---|---|
| MiniDb | 8.0 ms | 5.5 MB |
| SQLite | 14.0 ms | 1.7 KB |
| MongoDB | 59 ms | 3.7 MB |
| LiteDB | 73 ms | 153 MB |

### איך לקרוא את הטבלאות

- **SQLite מוביל בחיפוש נקודתי, בהכנסה batch ובהכנסה autocommit.** זה הבדל אמיתי, ומתקבל גם בתנאים שווים. ה-B+ tree שלנו עדיין מוסיף עבודה לכל שורה, ואין לו מטמון גדול כמו ל-SQLite.
- **MiniDb מהיר יותר בסריקות, בערך פי 2 מ-SQLite.** זה יתרון צר. הוא נובע מקריאה רציפה של הדפים ומ-`RowView`, אבל MiniDb מקצה יותר זיכרון בסריקה (5.5 MB מול 1.7 KB).
- **MongoDB ו-LiteDB** משתמשים בפורמט BSON, ומצריכים הקצאות זיכרון גבוהות. בעומסי הכתיבה LiteDB היה מהיר מאוד באוטוקומיט, אבל לא בדקתי את רמת ה-durability שלו, לכן אי אפשר לקרוא את התוצאה הזו כהשוואה ישירה.
- **MiniDb לא שווה ביכולות** לאף אחד מהמנועים: אין JOIN, אין ORDER BY, אין אינדקסים משניים, אין מקביליות, ואין פרוטוקול רשת.

### הגבלות של המדידה

- 100,000 שורות הן קובץ של כמה MB, והמטמון של מערכת ההפעלה חם. זו לא מדידה של מאגר שגדול מהזיכרון.
- זו מכונה אחת, ללא בידוד מלא, ולכן חלק מהסטיות גבוהות (מצוין בטבלאות).
- מפתח אחד, שתי עמודות, ללא תנאים מורכבים, ללא עומס מקבילי.
- השאילתות של SQLite כוללות פענוח SQL, ואילו MiniDb ו-LiteDB ומונגו נקראים דרך API.

### מדידות פנימיות: לפני ואחרי האופטימיזציות

המדידות האלה רצו עם מאגר של 8,192 דפים (כ-32 MB), כלומר הנתונים היו כמעט כולם בזיכרון. לכן הן לא מתאימות להשוואה עם המנועים האחרים, אבל הן מראות את השפעת השינויים על הקוד עצמו.

| מדידה | לפני | אחרי |
|---|---|---|
| חיפוש נקודתי, 10k | 0.74 µs, 1752 B | 0.27 µs, 264 B |
| הכנסה batch, 10k | 43 ms, 23.4 MB | 22 ms, 8.1 MB |
| סריקה מסוננת דרך API, 100k | 5.8 ms, 14.4 MB | 3.3 ms, 5.6 MB |
| שאילתת SQL מסוננת, 100k | 11.4 ms, 16.4 MB | 8.4 ms, 7.9 MB |

## מבנה

```
src/MiniDb.Core/                המנוע: Storage, Database, Records, Catalog, Index, Transactions, Query, Api
src/MiniDb.Cli/                 מעטפת SQL אינטראקטיבית
tests/MiniDb.Tests/             בדיקות יחידה, התאוששות ו-SQL
benchmarks/MiniDb.Benchmarks/   מדידות מול SQLite, LiteDB ו-MongoDB
```

השכבות תלויות רק כלפי מטה: `Api → Query → Index/Transactions → Storage`.

## מגבלות ידועות

- אינדקסים משניים לא ממומשים (המפרט מגדיר אותם כאופציונלי). WHERE על עמודה שאינה המפתח הראשי הוא סריקה מלאה.
- מחיקה משאירה חורים בדפי הנתונים; אין compaction או VACUUM.
- טרנזקציה אחת בכל רגע, וכל הגישה חד-חוטית. אין MVCC.
- טרנזקציה חייבת להיכנס ל-buffer pool כולו (no-steal), ולכן ברירת המחדל של 256 דפים מגבילה את גודל הטרנזקציה.
- AllocatePage אינו טרנזקציוני: דפים שהוקצו בטרנזקציה שבוטלה נשארים מוקצים כאפסים.
- `FsyncOnCommit=false` מאיץ commits, אבל אחרי הפסקת חשמל ייתכן שיאבדו ה-commits האחרונים. הקובץ נשאר עקבי, כי היומן נכתב לדיסק לפני כל דף.

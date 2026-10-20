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

הריצה: BenchmarkDotNet, Release, Windows 11, .NET 8. כל המנועים רצו על אותה מכונה, על אותם נתונים (`id BIGINT`, `name` מחרוזת, `score INT` עם ערך 7 בכ-10% מהשורות), ובאותם עומסים. אין אינדקס משני באף מנוע, כך שסינון על `score` הוא סריקה מלאה בכולם.

**שני מנועים רצים כשרתים מקומיים:** MongoDB (`localhost:27017`, ללא אימות) ו-PostgreSQL 17 (`localhost:5432`, לא נכלל כי נדרשה סיסמה). LiteDB ו-SQLite רצים בתוך התהליך.

**הכנסה batch: 10,000 שורות בטרנזקציה אחת**

| מנוע | זמן | הקצאות |
|---|---|---|
| SQLite | 24.1 ms | 5.6 MB |
| MiniDb | 29.8 ms | 8.1 MB |
| MongoDB | 56.6 ms | 5.3 MB |
| LiteDB | 182 ms | 114 MB |

**הכנסה autocommit: 1,000 טרנזקציות נפרדות**

| מנוע | זמן | הערה |
|---|---|---|
| LiteDB | 83.6 ms | ייתכן שאינו מבצע fsync לכל commit (לא נבדק) |
| MongoDB | 348 ms | ברירות מחדל של הדרייבר |
| MiniDb | 459 ms | fsync לכל commit |
| SQLite | 3,900 ms | `synchronous=FULL`, מצב journal ברירת מחדל (לא WAL) |

**חיפוש נקודתי לפי מפתח, 100,000 שורות (לכל קריאה)**

| מנוע | זמן | הערה |
|---|---|---|
| MiniDb | 0.45 µs | קריאה בתוך התהליך, בלי SQL |
| LiteDB | 36 µs | |
| SQLite | 125 µs | דרך `ExecuteScalar` וSQL |
| MongoDB | 291 µs | קריאה לשרת דרך הרשת |

**סריקה מלאה, 100,000 שורות**

| מנוע | זמן | הקצאות |
|---|---|---|
| MiniDb | 3.3 ms | 5.6 MB |
| SQLite | 20.6 ms | 0.4 KB |
| LiteDB | 93 ms | 151 MB |
| MongoDB | 169 ms | 37 MB |

**סריקה מסוננת (`score = 7`), 100,000 שורות**

| מנוע | זמן | הקצאות |
|---|---|---|
| MiniDb | 4.9 ms | 5.6 MB |
| SQLite | 8.3 ms | 0.4 KB |
| MongoDB | 82 ms | 3.8 MB |
| LiteDB | 85 ms | 157 MB |

**שאילתת SQL מסוננת (`SELECT id ... WHERE score = 7`), 100,000 שורות**

| מנוע | זמן | הקצאות |
|---|---|---|
| SQLite | 4.6 ms | 0.7 KB |
| MiniDb | 8.4 ms | 7.9 MB |

### איך לקרוא את הטבלאות

- **MiniDb מוביל בקריאה** (חיפוש נקודתי וסריקות). היתרון אמיתי אבל צר: המנוע רץ בתוך התהליך, בלי SQL ובלי רשת, והנתיב של `RowView` לא עושה boxing.
- **בכתיבה ב-batch MiniDb איטי מ-SQLite בכ-20%.** השכבות (B+ tree, WAL, catalog) מוסיפות עבודה לכל שורה, בעוד ש-SQLite כתוב בצורה יעילה מאוד.
- **בשאילתות SQL MiniDb איטי פי ~1.8 מ-SQLite.** הפער נובע מפענוח ה-SQL ומהקצאת רשומה לכל שורה נסרקת. עדיין לא בוצע סינון ישירות בתוך הדף.
- **השוואות autocommit צריך לקרוא בזהירות.** לכל מנוע רמת durability שונה, ולא בדקתי את כולן. SQLite במצב ברירת המחדל איטי מאוד, ולו היה WAL היה מהיר בהרבה.
- **MiniDb לא שווה ביכולות** לאף אחד מהמנועים: אין JOIN, אין אינדקסים משניים, אין מקביליות, ואין פרוטוקול רשת. ההשוואה מודדת ביצועים של פעולות בסיסיות בלבד.
- **MongoDB ו-LiteDB** משתמשים בפורמט BSON, וזמני המשלוח והפענוח שלהם חלק מהתוצאה.

### מדידות פנימיות: לפני ואחרי האופטימיזציות

| מדידה | לפני | אחרי |
|---|---|---|
| חיפוש נקודתי, 10k | 0.74 µs, 1752 B | 0.27 µs, 264 B |
| הכנסה batch, 10k | 43 ms, 23.4 MB | 22 ms, 8.1 MB |
| הכנסה autocommit, 1000 (fsync פעיל) | 436 ms | 388 ms |
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

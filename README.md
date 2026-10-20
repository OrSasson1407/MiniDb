# MiniDb

מנוע מסד נתונים קטן ב-C#, נבנה מאפס כפרויקט לימודי. תכנון מלא: [docs/superpowers/specs/2026-10-20-minidb-design.md](docs/superpowers/specs/2026-10-20-minidb-design.md). תוכנית המימוש: [docs/superpowers/plans/2026-10-20-minidb.md](docs/superpowers/plans/2026-10-20-minidb.md).

## מה יש בפנים

- **Storage**: קובץ דפים של 4 KB עם CRC-32 בסוף כל דף, ו-buffer pool (LRU) בגודל קבוע.
- **Database**: דף כותרת עם magic `MINIDB01`, ובדיקת שלמות בפתיחה.
- **Records**: סריאליזציה של שורות (Int32, Int64, Double, Bool, String ב-UTF-8), ודפי slotted.
- **Catalog**: הגדרות טבלאות, נשמרות בדפים עצמם.
- **Index**: B+ tree על מפתח ראשי `Int64`, עם פיצול עלים ופנימיים וסריקת טווח.
- **Transactions**: WAL של דפים שלמים, commit עם fsync, checkpoint, והתאוששות מקריסה (טרנזקציות ללא commit מתעלמות, וקרע בסוף היומן נחתך).
- **API**: `MiniDatabase` ו-`Table` בקוד C#.
- **SQL**: `CREATE TABLE`, `INSERT`, `SELECT ... WHERE ... LIMIT`, `DELETE ... WHERE`, עם planner שבוחר בין חיפוש נקודתי, סריקת טווח ב-B+ tree, וסריקה מלאה.

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

96 בדיקות: שכבה לשכבה, כולל בדיקת fuzz של ה-B+ tree מול `SortedDictionary`, בדיקות התאוששות מקריסה ו-torn tail, ובדיקות SQL מקצה לקצה.

## מדידות

BenchmarkDotNet, Release, על Windows 11, .NET 8. המדידה מול SQLite (Microsoft.Data.Sqlite 9.0) כנקודת ייחוס. המספרים להלן נמדדו לאחר תיקוני הביקורת הסופית.

```bash
dotnet run -c Release --project benchmarks/MiniDb.Benchmarks -- --filter "*"
```

| מדידה | Rows | MiniDb | SQLite | יחס |
|---|---|---|---|---|
| הכנסה, טרנזקציה אחת | 10,000 | 43.1 ms, 23.4 MB הוקצו | 24.3 ms, 4.4 MB הוקצו | MiniDb איטי פי ~1.8 (ראו הערה 5) |
| חיפוש נקודתי לפי מפתח (לכל קריאה) | 10,000 | 0.74 µs | 65.4 µs | ראו הערה 1 |
| חיפוש נקודתי לפי מפתח (לכל קריאה) | 100,000 | 1.85 µs | 62.9 µs | ראו הערה 1 |
| סריקה מלאה | 10,000 | 0.50 ms, 1.4 MB הוקצו | 1.03 ms, 0.7 KB הוקצו | MiniDb מהיר פי ~2 |
| סריקה מלאה | 100,000 | 5.41 ms, 14.1 MB הוקצו | 12.26 ms, 0.7 KB הוקצו | MiniDb מהיר פי ~2.3 |

הערות לקריאת המספרים:

1. בחיפוש הנקודתי, ה-MiniDb הוא קריאה בתהליך (ללא IPC), ו-SQLite רץ דרך `ExecuteScalar` עם overhead של ספק הנתונים. ההשוואה מודדת את שני הצדדים בדרך שמשתמש בפועל היה משתמש בהם, אבל אינה "עקיפה" של SQLite במובן אקדמי. ב-MiniDb כל `Get` גם קורא את הקטלוג מחדש.
2. ההקצאות של MiniDb גבוהות: כל שורה מפוענחת לאובייקטים (`object?[]` עם boxing). זו הנקודה הבולטת ביותר לשיפור, ולא בוצע עליה שום ייעול נוסף.
3. בהכנסה נבדקה טרנזקציה אחת לכל ה-10,000 שורות. commit לכל שורה עם fsync לא נמדד (הוא איטי בסדרי גודל, ולא נכלל בזמן הריצה).
4. מדידת 100,000 בהכנסה ומדידת ספירת קריאות דיסק (מונה ב-`IPageStore`) מהמפרט לא בוצעו בסבב זה.
5. סטיית התקן בהכנסה של MiniDb גבוהה יחסית (ראו קובץ הלוג של הריצה), ולכן היחס מוערך בגסות.

## מבנה

```
src/MiniDb.Core/          המנוע: Storage, Database, Records, Catalog, Index, Transactions, Query, Api
src/MiniDb.Cli/           מעטפת SQL אינטראקטיבית
tests/MiniDb.Tests/       בדיקות יחידה, התאוששות ו-SQL
benchmarks/MiniDb.Benchmarks/   מדידות מול SQLite
```

השכבות תלויות רק כלפי מטה: `Api → Query → Index/Transactions → Storage`.

## מגבלות ידועות

- אינדקסים משניים לא ממומשים (המפרט מגדיר אותם כאופציונלי). WHERE על עמודה שאינה המפתח הראשי הוא סריקה מלאה.
- מחיקה משאירה חורים בדפי הנתונים; אין compaction או VACUUM.
- טרנזקציה אחת בכל רגע, וכל הגישה חד-חוטית. אין MVCC.
- טרנזקציה חייבת להיכנס ל-buffer pool כולו (no-steal), ולכן ברירת המחדל של 256 דפים מגבילה את גודל הטרנזקציה.
- AllocatePage אינו טרנזקציוני: דפים שהוקצו בטרנזקציה שבוטלה נשארים מוקצים כאפסים.
- `FsyncOnCommit=false` אינו בטוח: אחרי הפסקת חשמל הקובץ עלול להיות קדימה מהיומן. האפשרות קיימת למדידות בלבד.

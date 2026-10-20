# שינויים

כל השינויים החשובים מתועדים כאן. הפורמט מבוסס על [Keep a Changelog](https://keepachangelog.com/he/1.1.0/), והגרסאות לפי [SemVer](https://semver.org/lang/he/).

## מדיניות גרסאות

- **לפני 1.0** (גרסאות `0.y.z`): שינויים שוברים מותרים בעלייה של `y`, ומתועדים תחת "שינויים שוברים".
- **מ-1.0:** שינוי שובר דורש עלייה של המספר הראשון בלבד.
- **פורמט הקובץ** מקבל מספר גרסה נפרד (ב-`HeaderCodec.CurrentFormatVersion`). כל שינוי בפורמט נשמר כך שקובץ ישן נפתח, או שמתועדת דרך שדרוג מפורשת.

## Unreleased

### נוסף
- מנוע אחסון: דפים של 4 KB עם CRC-32, buffer pool עם LRU ו-pinning, וקובץ דפים.
- WAL עם page deltas, commit עם fsync, checkpoint, והתאוששות מקריסה שמתעלמת מטרנזקציות שלא אושרו.
- B+ tree לאינדקסים משניים, וטבלה מקובצת שמחזיקה את השורות בעלים.
- SQL: `CREATE TABLE`, `CREATE INDEX`, `INSERT`, `SELECT` (עם `WHERE` ב-AND/OR, `INNER JOIN`, `GROUP BY` עם `COUNT`/`SUM`/`MIN`/`MAX`/`AVG`, `ORDER BY`, `LIMIT`), `UPDATE` ו-`DELETE`.
- אינדקסים על עמודות `INT`, `BIGINT` ו-`VARCHAR`.
- API בקוד C#: `MiniDatabase`, `Table`, `RowView`, `Transaction`.
- מעטפת SQL אינטראקטיבית (`MiniDb.Cli`).
- חבילת בדיקות עם 162 בדיקות, כולל התאוששות מקריסה, checksum, ונתונים גדולים מהמאגר.
- מערכת benchmarks מול SQLite, LiteDB ו-MongoDB.

### שינויים שוברים
- אין עדיין. זו הגרסה הראשונה.

### תוקן
- SUM על עמודת INT החזיר double במקום long.
- דפים שהוקצו בטרנזקציה שבוטלה נשארו מוקצים לתמיד. מעכשיו הם מוחזרים לרשימה הפנויה.

### ידוע
- רשומה גדולה מדף אחד נדחית. מתוכנן לשלב 4 (דפי overflow).
- דפים שהוחזרו לרשימה הפנויה נשארים לא בשימוש אם המערכת קורסת לפני ההחזרה.
- אין group commit: כל commit מבצע fsync בעצמו, ולכן כתיבה מקבילית איטית. מתוכנן לשלב 2.

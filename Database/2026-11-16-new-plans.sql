/* =====================================================================
   ClassKendra — new plans (16 Nov 2026)
   Run once on your database (SSMS → New Query → select ClassKendra → Execute).
   Safe to run again: plans are added or updated, never duplicated.

   Tuition & coaching:  Free (25 students, forever) · Tuition Lite · Starter · Growth · Pro
   Schools:             School 100 · School 200 · School 300 · School 500 · School 1000
   Prices are per month, + GST. Yearly = 10 months (2 months free).

   Old school plans (School, School Plus, School Max) are switched off, not deleted,
   so schools already paying for them keep working until they renew.
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

UPDATE dbo.Plans SET IsActive = 0
WHERE Code IN ('School', 'SchoolPlus', 'SchoolMax');

MERGE dbo.Plans AS t
USING (VALUES
    --  Code,          Name,           Type,       StudentsLabel,           Max,  Monthly, Yearly, Messages, Popular, Sort
    ('Free',        'Free',         'Coaching', 'Up to 25 students',       25,     0,      0,   100, 0,  5),
    ('TuitionLite', 'Tuition Lite', 'Coaching', 'Up to 50 students',       50,   399,   3990,   500, 0, 10),
    ('Starter',     'Starter',      'Coaching', 'Up to 100 students',     100,   799,   7990,  1000, 0, 20),
    ('Growth',      'Growth',       'Coaching', 'Up to 300 students',     300,  1999,  19990,  3000, 1, 30),
    ('Pro',         'Pro',          'Coaching', 'Up to 600 students',     600,  3499,  34990,  6000, 0, 40),

    ('School100',   'School 100',   'School',   'Up to 100 students',     100,   749,   7490,   300, 0, 10),
    ('School200',   'School 200',   'School',   'Up to 200 students',     200,   999,   9990,   600, 1, 20),
    ('School300',   'School 300',   'School',   'Up to 300 students',     300,  1299,  12990,   900, 0, 30),
    ('School500',   'School 500',   'School',   'Up to 500 students',     500,  1899,  18990,  1500, 0, 40),
    ('School1000',  'School 1000',  'School',   'Up to 1,000 students',  1000,  2999,  29990,  3000, 0, 50)
) AS s (Code, Name, InstitutionType, StudentsLabel, MaxStudents, MonthlyPrice, YearlyPrice, MessageLimit, IsPopular, SortOrder)
ON t.Code = s.Code
WHEN MATCHED THEN UPDATE SET
    Name = s.Name, InstitutionType = s.InstitutionType, StudentsLabel = s.StudentsLabel, MaxStudents = s.MaxStudents,
    MonthlyPrice = s.MonthlyPrice, YearlyPrice = s.YearlyPrice, MessageLimit = s.MessageLimit,
    IsPopular = s.IsPopular, IsActive = 1, SortOrder = s.SortOrder
WHEN NOT MATCHED THEN INSERT
    (Code, Name, InstitutionType, StudentsLabel, MaxStudents, MonthlyPrice, YearlyPrice, MessageLimit, IsPopular, IsActive, SortOrder)
    VALUES (s.Code, s.Name, s.InstitutionType, s.StudentsLabel, s.MaxStudents, s.MonthlyPrice, s.YearlyPrice,
            s.MessageLimit, s.IsPopular, 1, s.SortOrder);

COMMIT;

/* Check the result */
SELECT Code, Name, InstitutionType, StudentsLabel, MaxStudents, MonthlyPrice, YearlyPrice, MessageLimit, IsPopular, IsActive
FROM dbo.Plans
ORDER BY InstitutionType, IsActive DESC, SortOrder;

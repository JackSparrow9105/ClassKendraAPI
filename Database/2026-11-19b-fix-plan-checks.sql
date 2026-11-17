/* =====================================================================
   ClassKendra — fix two old CHECK rules that block plan billing (19 Nov 2026)
   Safe to run again. RUN IT AGAIN whenever you add a new plan to dbo.Plans.

   1. CK_Tenants_PlanCode only allowed the old plan codes (Trial, Starter, Growth, Pro, School, SchoolPlus, SchoolMax).
      Free, TuitionLite and School100…School1000 were refused, so:
        - a coaching centre whose trial ended could not be moved to the Free plan;
        - buying a new plan failed after Razorpay had already taken the money.
      Now: 'Trial' plus every code in dbo.Plans.

   2. CK_Subscriptions_Cycle allowed 'monthly' / 'yearly' (lower case) while the API writes 'Monthly' / 'Yearly'.
      Existing rows are changed to 'Monthly' / 'Yearly' and the rule allows exactly those.
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

/* ---------- 1. Tenants.PlanCode: Trial + every plan ---------- */
DECLARE @codes NVARCHAR(MAX) = N'''Trial''';
SELECT @codes = @codes + N', ''' + REPLACE(Code, '''', '''''') + N'''' FROM dbo.Plans WHERE Code <> 'Trial';

/* Every centre's current plan must be allowed, or the new rule can't be added. */
IF EXISTS (SELECT 1 FROM dbo.Tenants t WHERE t.PlanCode <> 'Trial' AND NOT EXISTS (SELECT 1 FROM dbo.Plans p WHERE p.Code = t.PlanCode))
BEGIN
    SELECT Id, Name, PlanCode AS UnknownPlanCode FROM dbo.Tenants t
     WHERE t.PlanCode <> 'Trial' AND NOT EXISTS (SELECT 1 FROM dbo.Plans p WHERE p.Code = t.PlanCode);
    THROW 50000, 'These centres have a plan code that is not in dbo.Plans. Add the plan (or fix the centre) and run again.', 1;
END

IF OBJECT_ID('dbo.CK_Tenants_PlanCode') IS NOT NULL
    ALTER TABLE dbo.Tenants DROP CONSTRAINT CK_Tenants_PlanCode;
DECLARE @sql NVARCHAR(MAX) = N'ALTER TABLE dbo.Tenants WITH CHECK ADD CONSTRAINT CK_Tenants_PlanCode CHECK (PlanCode IN (' + @codes + N'))';
EXEC (@sql);

/* ---------- 2. Subscriptions.Cycle: Monthly / Yearly ---------- */
IF OBJECT_ID('dbo.CK_Subscriptions_Cycle') IS NOT NULL
    ALTER TABLE dbo.Subscriptions DROP CONSTRAINT CK_Subscriptions_Cycle;
UPDATE dbo.Subscriptions SET Cycle = 'Monthly' WHERE Cycle = 'monthly' COLLATE Latin1_General_CS_AS;
UPDATE dbo.Subscriptions SET Cycle = 'Yearly'  WHERE Cycle = 'yearly'  COLLATE Latin1_General_CS_AS;
ALTER TABLE dbo.Subscriptions WITH CHECK ADD CONSTRAINT CK_Subscriptions_Cycle
    CHECK (Cycle COLLATE Latin1_General_CS_AS IN ('Monthly', 'Yearly'));

COMMIT;
GO

/* Check: the new rules */
SELECT name, definition FROM sys.check_constraints
 WHERE name IN ('CK_Tenants_PlanCode', 'CK_Subscriptions_Cycle');
SELECT Cycle, COUNT(*) AS Subscriptions FROM dbo.Subscriptions GROUP BY Cycle;

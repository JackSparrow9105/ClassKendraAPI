/* =====================================================================
   ClassKendra — undo a promotion (20 Nov 2026)
   Run once, AFTER 2026-11-19-advance-refunds-late-fees-siblings.sql (SSMS → ClassKendra → Execute). Safe to run again.

   PromotionRuns.UndoneAt, UndoneByName   when a promotion was undone, and by whom (it can be undone only once,
                                          within 7 days).

   Nothing else in Phase 3 needs the database: Excel downloads read existing tables, restoring a cancelled payment
   uses the Phase 1 columns, and offline attendance uses the existing registers.
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

IF COL_LENGTH('dbo.PromotionRuns', 'UndoneAt') IS NULL
    ALTER TABLE dbo.PromotionRuns ADD
        UndoneAt     DATETIME2(0)  NULL,   -- UTC
        UndoneByName NVARCHAR(100) NULL;

COMMIT;
GO

/* Check: both should be NOT NULL */
SELECT COL_LENGTH('dbo.PromotionRuns', 'UndoneAt') AS UndoneAt, COL_LENGTH('dbo.PromotionRuns', 'UndoneByName') AS UndoneByName;

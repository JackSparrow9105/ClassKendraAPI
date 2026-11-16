/* =====================================================================
   ClassKendra — WhatsApp message settings per centre (16 Nov 2026)
   Run once on your database (SSMS → New Query → select ClassKendra → Execute).
   Safe to run again: each change is skipped if it already exists.

   SendReceipts  1 = send the parent a WhatsApp receipt for every payment (default), 0 = don't
   AbsentAlerts  'Every'   = alert the parent for every absence (default)
                 'TwoDays' = only when the student is absent 2 classes in a row
                 'Off'     = no absent alerts
   (Fee reminder timing already lives in FeeSettings: RemindBefore / RemindOnDue / RemindAfter.)
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

IF COL_LENGTH('dbo.Tenants', 'SendReceipts') IS NULL
    ALTER TABLE dbo.Tenants ADD SendReceipts BIT NOT NULL
        CONSTRAINT DF_Tenants_SendReceipts DEFAULT 1;

IF COL_LENGTH('dbo.Tenants', 'AbsentAlerts') IS NULL
    ALTER TABLE dbo.Tenants ADD AbsentAlerts VARCHAR(10) NOT NULL
        CONSTRAINT DF_Tenants_AbsentAlerts DEFAULT 'Every';

COMMIT;
GO

/* Only these three values are allowed */
IF OBJECT_ID('CK_Tenants_AbsentAlerts') IS NULL
    ALTER TABLE dbo.Tenants WITH CHECK ADD CONSTRAINT CK_Tenants_AbsentAlerts
        CHECK (AbsentAlerts IN ('Every', 'TwoDays', 'Off'));

GO

/* Check */
SELECT Name, SendReceipts, AbsentAlerts FROM dbo.Tenants;

/* =====================================================================
   ClassKendra — advance, refunds, waivers and charges, opening balance, late fee, siblings (19 Nov 2026)
   Run once, AFTER 2026-11-18-leaving-holidays-activity-log.sql (SSMS → ClassKendra → Execute). Safe to run again.

   1. FeeHeads.Kind            NULL = the centre's own fee. 'OpeningBalance' / 'LateFee' / 'Charge' = hidden fee types
                               ClassKendra makes for its own bills (never shown in fee setup).
   2. FeeDues.Kind, ParentDueId  the same kind on each bill; a late fee points at the bill it is for.
   3. Payments.GroupId         receipts paid together (brothers and sisters).
      Payments.Mode            also allows 'Opening' = advance paid before ClassKendra (never counted as collection).
   4. FeeSettings              late fee (None / Once / PerDay, amount, after days, maximum)
                               and sibling discount (None / Percent / Amount, value, which fee).
   5. Refunds                  money given back to parents, "RF-2026-0001". Always out of the student's advance.
   6. FeeAdjustments           part of a fee waived, or an extra charge, with the reason and who did it.

   Advance needs no column: it is worked out from payments, what they put on fees, and refunds.
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

/* ---------- 1. Hidden fee types ---------- */
IF COL_LENGTH('dbo.FeeHeads', 'Kind') IS NULL
    ALTER TABLE dbo.FeeHeads ADD Kind VARCHAR(15) NULL;

/* ---------- 2. Kind of each bill, and the bill a late fee belongs to ---------- */
IF COL_LENGTH('dbo.FeeDues', 'Kind') IS NULL
    ALTER TABLE dbo.FeeDues ADD Kind VARCHAR(15) NULL;
IF COL_LENGTH('dbo.FeeDues', 'ParentDueId') IS NULL
    ALTER TABLE dbo.FeeDues ADD ParentDueId UNIQUEIDENTIFIER NULL;

/* ---------- 3. Receipts paid together ---------- */
IF COL_LENGTH('dbo.Payments', 'GroupId') IS NULL
    ALTER TABLE dbo.Payments ADD GroupId UNIQUEIDENTIFIER NULL;

/* ---------- 4. Late fee and sibling discount settings ---------- */
IF COL_LENGTH('dbo.FeeSettings', 'LateFeeType') IS NULL
    ALTER TABLE dbo.FeeSettings ADD
        LateFeeType          VARCHAR(10)      NOT NULL CONSTRAINT DF_FeeSettings_LateFeeType DEFAULT 'None',
        LateFeeAmount        INT              NOT NULL CONSTRAINT DF_FeeSettings_LateFeeAmount DEFAULT 0,
        LateFeeAfterDays     INT              NOT NULL CONSTRAINT DF_FeeSettings_LateFeeAfterDays DEFAULT 0,
        LateFeeMax           INT              NOT NULL CONSTRAINT DF_FeeSettings_LateFeeMax DEFAULT 0,
        SiblingDiscountType  VARCHAR(10)      NOT NULL CONSTRAINT DF_FeeSettings_SiblingType DEFAULT 'None',
        SiblingDiscountValue INT              NOT NULL CONSTRAINT DF_FeeSettings_SiblingValue DEFAULT 0,
        SiblingDiscountHeadId UNIQUEIDENTIFIER NULL;

/* ---------- 5. Refunds ---------- */
IF OBJECT_ID('dbo.Refunds') IS NULL
    CREATE TABLE dbo.Refunds (
        Id         UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Refunds_Id DEFAULT NEWSEQUENTIALID(),
        TenantId   UNIQUEIDENTIFIER NOT NULL,
        StudentId  UNIQUEIDENTIFIER NOT NULL,
        RefundNo   VARCHAR(20)      NOT NULL,     -- RF-2026-0001, per session
        Amount     INT              NOT NULL,
        Mode       VARCHAR(10)      NOT NULL,     -- Cash / UPI / Cheque / Bank
        RefundedOn DATE             NOT NULL,
        Reason     NVARCHAR(300)    NOT NULL,
        ByUserId   UNIQUEIDENTIFIER NULL,
        ByName     NVARCHAR(100)    NOT NULL,
        CreatedAt  DATETIME2(0)     NOT NULL CONSTRAINT DF_Refunds_Created DEFAULT SYSUTCDATETIME(),   -- UTC
        CONSTRAINT PK_Refunds PRIMARY KEY (Id),
        CONSTRAINT FK_Refunds_Tenant FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id),
        CONSTRAINT FK_Refunds_Student FOREIGN KEY (StudentId) REFERENCES dbo.Students (Id),
        CONSTRAINT CK_Refunds_Amount CHECK (Amount > 0),
        CONSTRAINT CK_Refunds_Mode CHECK (Mode IN ('Cash', 'UPI', 'Cheque', 'Bank'))
    );

/* ---------- 6. Fee adjustments ---------- */
IF OBJECT_ID('dbo.FeeAdjustments') IS NULL
    CREATE TABLE dbo.FeeAdjustments (
        Id         UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_FeeAdjustments_Id DEFAULT NEWSEQUENTIALID(),
        TenantId   UNIQUEIDENTIFIER NOT NULL,
        StudentId  UNIQUEIDENTIFIER NOT NULL,
        FeeDueId   UNIQUEIDENTIFIER NOT NULL,
        Kind       VARCHAR(10)      NOT NULL,     -- Waive / Charge
        Amount     INT              NOT NULL,
        Reason     NVARCHAR(300)    NOT NULL,
        ByUserId   UNIQUEIDENTIFIER NULL,
        ByName     NVARCHAR(100)    NOT NULL,
        CreatedAt  DATETIME2(0)     NOT NULL CONSTRAINT DF_FeeAdjustments_Created DEFAULT SYSUTCDATETIME(),   -- UTC
        CONSTRAINT PK_FeeAdjustments PRIMARY KEY (Id),
        CONSTRAINT FK_FeeAdjustments_Tenant FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id),
        CONSTRAINT FK_FeeAdjustments_Student FOREIGN KEY (StudentId) REFERENCES dbo.Students (Id),
        CONSTRAINT FK_FeeAdjustments_Due FOREIGN KEY (FeeDueId) REFERENCES dbo.FeeDues (Id),
        CONSTRAINT CK_FeeAdjustments_Kind CHECK (Kind IN ('Waive', 'Charge')),
        CONSTRAINT CK_FeeAdjustments_Amount CHECK (Amount > 0)
    );

COMMIT;
GO

/* ---------- Checks, keys and indexes (separate batch: the new columns must exist first) ---------- */
IF OBJECT_ID('CK_FeeHeads_Kind') IS NULL
    ALTER TABLE dbo.FeeHeads WITH CHECK ADD CONSTRAINT CK_FeeHeads_Kind
        CHECK (Kind IS NULL OR Kind IN ('OpeningBalance', 'LateFee', 'Charge'));

IF OBJECT_ID('CK_FeeDues_Kind') IS NULL
    ALTER TABLE dbo.FeeDues WITH CHECK ADD CONSTRAINT CK_FeeDues_Kind
        CHECK (Kind IS NULL OR Kind IN ('OpeningBalance', 'LateFee', 'Charge'));

IF OBJECT_ID('FK_FeeDues_ParentDue') IS NULL
    ALTER TABLE dbo.FeeDues WITH CHECK ADD CONSTRAINT FK_FeeDues_ParentDue
        FOREIGN KEY (ParentDueId) REFERENCES dbo.FeeDues (Id);

IF OBJECT_ID('CK_FeeSettings_LateFeeType') IS NULL
    ALTER TABLE dbo.FeeSettings WITH CHECK ADD CONSTRAINT CK_FeeSettings_LateFeeType
        CHECK (LateFeeType IN ('None', 'Once', 'PerDay'));

IF OBJECT_ID('CK_FeeSettings_SiblingType') IS NULL
    ALTER TABLE dbo.FeeSettings WITH CHECK ADD CONSTRAINT CK_FeeSettings_SiblingType
        CHECK (SiblingDiscountType IN ('None', 'Percent', 'Amount'));

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FeeDues_ParentDue')
    CREATE INDEX IX_FeeDues_ParentDue ON dbo.FeeDues (ParentDueId) WHERE ParentDueId IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Payments_Group')
    CREATE INDEX IX_Payments_Group ON dbo.Payments (GroupId) WHERE GroupId IS NOT NULL;

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Refunds_Tenant_No')
    CREATE UNIQUE INDEX UX_Refunds_Tenant_No ON dbo.Refunds (TenantId, RefundNo);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Refunds_Student')
    CREATE INDEX IX_Refunds_Student ON dbo.Refunds (StudentId);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FeeAdjustments_Due')
    CREATE INDEX IX_FeeAdjustments_Due ON dbo.FeeAdjustments (FeeDueId);
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_FeeAdjustments_Tenant')
    CREATE INDEX IX_FeeAdjustments_Tenant ON dbo.FeeAdjustments (TenantId, CreatedAt);
GO

/* ---------- Payments.Mode: allow 'Opening' ----------
   If a CHECK constraint limits Payments.Mode and doesn't allow 'Opening' yet, it is replaced by the same rule
   plus 'Opening' — so every mode your database allows today stays allowed. */
SET XACT_ABORT ON;
DECLARE @ck SYSNAME, @def NVARCHAR(MAX), @sql NVARCHAR(MAX);
SELECT TOP 1 @ck = cc.name, @def = cc.definition
FROM sys.check_constraints cc
JOIN sys.columns c ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
WHERE cc.parent_object_id = OBJECT_ID('dbo.Payments') AND c.name = 'Mode'
  AND cc.definition NOT LIKE '%Opening%';
IF @ck IS NOT NULL
BEGIN
    PRINT 'Payments.Mode check ' + @ck + ' was: ' + @def;
    BEGIN TRAN;
    SET @sql = N'ALTER TABLE dbo.Payments DROP CONSTRAINT ' + QUOTENAME(@ck);
    EXEC (@sql);
    SET @sql = N'ALTER TABLE dbo.Payments WITH CHECK ADD CONSTRAINT ' + QUOTENAME(@ck)
             + N' CHECK (' + @def + N' OR [Mode] = ''Opening'')';
    EXEC (@sql);
    COMMIT;
END
GO

/* Check: every value should be NOT NULL (the last one is the Payments.Mode check, which may be NULL if there never was one) */
SELECT COL_LENGTH('dbo.FeeHeads', 'Kind')            AS FeeHeadKind,
       COL_LENGTH('dbo.FeeDues', 'ParentDueId')      AS LateFeeLink,
       COL_LENGTH('dbo.Payments', 'GroupId')         AS PaidTogether,
       COL_LENGTH('dbo.FeeSettings', 'LateFeeType')  AS LateFeeSettings,
       COL_LENGTH('dbo.FeeSettings', 'SiblingDiscountType') AS SiblingSettings,
       OBJECT_ID('dbo.Refunds')                      AS Refunds,
       OBJECT_ID('dbo.FeeAdjustments')               AS FeeAdjustments,
       (SELECT TOP 1 cc.definition FROM sys.check_constraints cc
          JOIN sys.columns c ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
         WHERE cc.parent_object_id = OBJECT_ID('dbo.Payments') AND c.name = 'Mode') AS PaymentModeCheck;

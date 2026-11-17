/* =====================================================================
   ClassKendra — cancelled payments, activity log, students leaving, holidays, 300 m check-in (18 Nov 2026)
   Run once, AFTER 2026-11-17-staff-attendance.sql (SSMS → ClassKendra → Execute). Safe to run again.

   1. Payments.Cancelled…       payments are never deleted any more: they are cancelled with a reason
                                (who, when, why). The receipt number is kept and never reused.
   2. AuditLogs                 activity log: payment cancelled, student left / rejoined, fees waived,
                                holiday added / removed, check-in distance changed
   3. Students.LeftReason, LeftNote, RemindAfterLeaving
                                why a student left; 1 = keep sending fee reminders for dues they still owe
   4. StaffHolidays → Holidays  one holiday list for the whole centre (schools and coaching):
                                no absent alerts, fee reminders wait for the next working day,
                                staff are not marked absent
   5. StaffAttendanceSettings.RadiusMeters   default 300 m (was 200); centres below 300 are raised to 300
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

/* ---------- 1. Cancelled payments ---------- */
IF COL_LENGTH('dbo.Payments', 'CancelledAt') IS NULL
    ALTER TABLE dbo.Payments ADD
        CancelledAt       DATETIME2(0)     NULL,   -- UTC; NULL = a normal payment
        CancelledByUserId UNIQUEIDENTIFIER NULL,
        CancelledByName   NVARCHAR(100)    NULL,
        CancelReason      NVARCHAR(300)    NULL;

/* ---------- 2. Activity log ---------- */
IF OBJECT_ID('dbo.AuditLogs') IS NULL
    CREATE TABLE dbo.AuditLogs (
        Id         BIGINT IDENTITY(1, 1) NOT NULL,
        TenantId   UNIQUEIDENTIFIER NOT NULL,
        [At]       DATETIME2(0)     NOT NULL CONSTRAINT DF_AuditLogs_At DEFAULT SYSUTCDATETIME(),   -- UTC
        UserId     UNIQUEIDENTIFIER NULL,          -- NULL = done by the system (background job)
        UserName   NVARCHAR(100)    NOT NULL,
        UserRole   VARCHAR(10)      NOT NULL,      -- Owner / Staff / Teacher / System
        [Action]   VARCHAR(40)      NOT NULL,      -- PaymentCancelled, StudentLeft, StudentRejoined, FeesWaived, HolidayAdded, HolidayRemoved, CheckInRadiusChanged
        EntityType VARCHAR(20)      NOT NULL,      -- Payment / Student / Holiday / Settings
        EntityId   UNIQUEIDENTIFIER NULL,
        Summary    NVARCHAR(300)    NOT NULL,      -- one line a person can read
        Reason     NVARCHAR(300)    NULL,          -- what the person typed
        [Data]     NVARCHAR(MAX)    NULL,          -- JSON with the details (amounts, bills), for support
        CONSTRAINT PK_AuditLogs PRIMARY KEY (Id),
        CONSTRAINT FK_AuditLogs_Tenant FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id)
    );

/* ---------- 3. Students leaving ---------- */
IF COL_LENGTH('dbo.Students', 'LeftReason') IS NULL
    ALTER TABLE dbo.Students ADD
        LeftReason         VARCHAR(20)   NULL,     -- Left / Transferred / PassedOut / Removed
        LeftNote           NVARCHAR(200) NULL,
        RemindAfterLeaving BIT NOT NULL CONSTRAINT DF_Students_RemindAfterLeaving DEFAULT 0;

/* ---------- 4. One holiday list for the whole centre ---------- */
IF OBJECT_ID('dbo.Holidays') IS NULL AND OBJECT_ID('dbo.StaffHolidays') IS NOT NULL
BEGIN
    EXEC sp_rename 'dbo.StaffHolidays', 'Holidays';   -- the rows are kept; SQL Server prints a caution, which is fine
    /* Tidy the constraint names too (skipped if a name is different on your database) */
    IF OBJECT_ID('dbo.PK_StaffHolidays') IS NOT NULL        EXEC sp_rename 'dbo.PK_StaffHolidays', 'PK_Holidays', 'OBJECT';
    IF OBJECT_ID('dbo.DF_StaffHolidays_Id') IS NOT NULL     EXEC sp_rename 'dbo.DF_StaffHolidays_Id', 'DF_Holidays_Id', 'OBJECT';
    IF OBJECT_ID('dbo.FK_StaffHolidays_Tenant') IS NOT NULL EXEC sp_rename 'dbo.FK_StaffHolidays_Tenant', 'FK_Holidays_Tenant', 'OBJECT';
    IF OBJECT_ID('dbo.CK_StaffHolidays_Range') IS NOT NULL  EXEC sp_rename 'dbo.CK_StaffHolidays_Range', 'CK_Holidays_Range', 'OBJECT';
END

IF OBJECT_ID('dbo.Holidays') IS NULL
    CREATE TABLE dbo.Holidays (
        Id       UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_Holidays_Id DEFAULT NEWSEQUENTIALID(),
        TenantId UNIQUEIDENTIFIER NOT NULL,
        Name     NVARCHAR(60)     NOT NULL,
        FromDate DATE             NOT NULL,
        ToDate   DATE             NOT NULL,
        CONSTRAINT PK_Holidays PRIMARY KEY (Id),
        CONSTRAINT FK_Holidays_Tenant FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id),
        CONSTRAINT CK_Holidays_Range CHECK (ToDate >= FromDate)
    );

COMMIT;
GO

/* Indexes (separate batch: the new columns and tables must exist first) */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_AuditLogs_Tenant_At')
    CREATE INDEX IX_AuditLogs_Tenant_At ON dbo.AuditLogs (TenantId, [At] DESC);

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_Holidays_Tenant_Dates')
    CREATE INDEX IX_Holidays_Tenant_Dates ON dbo.Holidays (TenantId, FromDate, ToDate);

/* Only these reasons are allowed */
IF OBJECT_ID('CK_Students_LeftReason') IS NULL
    ALTER TABLE dbo.Students WITH CHECK ADD CONSTRAINT CK_Students_LeftReason
        CHECK (LeftReason IS NULL OR LeftReason IN ('Left', 'Transferred', 'PassedOut', 'Removed'));
GO

/* ---------- 5. Check-in distance: 300 m ---------- */
IF OBJECT_ID('dbo.StaffAttendanceSettings') IS NOT NULL
BEGIN
    IF EXISTS (SELECT 1 FROM sys.default_constraints
               WHERE name = 'DF_StaffAttSettings_Radius' AND definition LIKE '%200%')
    BEGIN
        ALTER TABLE dbo.StaffAttendanceSettings DROP CONSTRAINT DF_StaffAttSettings_Radius;
        ALTER TABLE dbo.StaffAttendanceSettings ADD CONSTRAINT DF_StaffAttSettings_Radius DEFAULT 300 FOR RadiusMeters;
    END

    UPDATE dbo.StaffAttendanceSettings SET RadiusMeters = 300 WHERE RadiusMeters < 300;
END
GO

/* Check */
SELECT COL_LENGTH('dbo.Payments', 'CancelledAt') AS PaymentCancel,
       OBJECT_ID('dbo.AuditLogs')                AS AuditLogs,
       COL_LENGTH('dbo.Students', 'LeftReason')  AS StudentLeftReason,
       OBJECT_ID('dbo.Holidays')                 AS Holidays,
       OBJECT_ID('dbo.StaffHolidays')            AS OldStaffHolidays_ShouldBeNull,
       (SELECT MIN(RadiusMeters) FROM dbo.StaffAttendanceSettings) AS SmallestRadius;

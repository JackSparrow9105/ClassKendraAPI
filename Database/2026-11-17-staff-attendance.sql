/* =====================================================================
   ClassKendra — staff attendance for schools, leave, app notifications (17 Nov 2026)
   Run once (SSMS → ClassKendra → Execute). Safe to run again.

   StaffAttendanceSettings  one row per school: timing, late grace, check-in location, weekly off,
                            leave notification switches
   Users.StaffStartTime/EndTime  a different timing for one person (empty = the school's)
   StaffHolidays            school holidays (one day or a range)
   StaffAttendance          one row per person per day: check-in/out, Present/Late/Absent/Leave
   StaffLeaves              leave requests: Pending / Approved / Rejected
   PushSubscriptions        phones that allowed ClassKendra app notifications
   Messages.Type            allows 'LeaveRequest' and 'LeaveAnswer' (only when WhatsApp is turned on)
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

IF OBJECT_ID('dbo.StaffAttendanceSettings') IS NULL
    CREATE TABLE dbo.StaffAttendanceSettings (
        TenantId            UNIQUEIDENTIFIER NOT NULL,
        StartTime           TIME(0)          NOT NULL CONSTRAINT DF_StaffAttSettings_Start DEFAULT '07:30',
        EndTime             TIME(0)          NOT NULL CONSTRAINT DF_StaffAttSettings_End DEFAULT '13:30',
        GraceMinutes        INT              NOT NULL CONSTRAINT DF_StaffAttSettings_Grace DEFAULT 10,
        Latitude            DECIMAL(9, 6)    NULL,
        Longitude           DECIMAL(9, 6)    NULL,
        RadiusMeters        INT              NOT NULL CONSTRAINT DF_StaffAttSettings_Radius DEFAULT 200,
        WeeklyOff           VARCHAR(20)      NOT NULL CONSTRAINT DF_StaffAttSettings_Off DEFAULT '0',   -- 0 = Sunday … 6 = Saturday, comma separated
        NotifyLeaveRequests BIT              NOT NULL CONSTRAINT DF_StaffAttSettings_NotifyReq DEFAULT 1,
        NotifyLeaveAnswers  BIT              NOT NULL CONSTRAINT DF_StaffAttSettings_NotifyAns DEFAULT 1,
        LeaveWhatsApp       BIT              NOT NULL CONSTRAINT DF_StaffAttSettings_WhatsApp DEFAULT 0,
        UpdatedAt           DATETIME2(0)     NOT NULL CONSTRAINT DF_StaffAttSettings_Updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_StaffAttendanceSettings PRIMARY KEY (TenantId),
        CONSTRAINT FK_StaffAttendanceSettings_Tenant FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id)
    );

IF COL_LENGTH('dbo.Users', 'StaffStartTime') IS NULL
    ALTER TABLE dbo.Users ADD StaffStartTime TIME(0) NULL, StaffEndTime TIME(0) NULL;

IF OBJECT_ID('dbo.StaffHolidays') IS NULL
    CREATE TABLE dbo.StaffHolidays (
        Id       UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_StaffHolidays_Id DEFAULT NEWSEQUENTIALID(),
        TenantId UNIQUEIDENTIFIER NOT NULL,
        Name     NVARCHAR(60)     NOT NULL,
        FromDate DATE             NOT NULL,
        ToDate   DATE             NOT NULL,
        CONSTRAINT PK_StaffHolidays PRIMARY KEY (Id),
        CONSTRAINT FK_StaffHolidays_Tenant FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id),
        CONSTRAINT CK_StaffHolidays_Range CHECK (ToDate >= FromDate)
    );

IF OBJECT_ID('dbo.StaffAttendance') IS NULL
    CREATE TABLE dbo.StaffAttendance (
        Id             UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_StaffAttendance_Id DEFAULT NEWSEQUENTIALID(),
        TenantId       UNIQUEIDENTIFIER NOT NULL,
        UserId         UNIQUEIDENTIFIER NOT NULL,
        [Date]         DATE             NOT NULL,
        CheckInAt      DATETIME2(0)     NULL,     -- UTC
        CheckOutAt     DATETIME2(0)     NULL,     -- UTC
        [Status]       VARCHAR(10)      NOT NULL, -- Present / Late / Absent / Leave
        LateMinutes    INT              NOT NULL CONSTRAINT DF_StaffAttendance_Late DEFAULT 0,
        DistanceMeters INT              NULL,     -- how far from school at check-in
        MarkedByUserId UNIQUEIDENTIFIER NULL,     -- set when the owner marked or corrected it
        UpdatedAt      DATETIME2(0)     NOT NULL CONSTRAINT DF_StaffAttendance_Updated DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_StaffAttendance PRIMARY KEY (Id),
        CONSTRAINT FK_StaffAttendance_Tenant FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id),
        CONSTRAINT FK_StaffAttendance_User FOREIGN KEY (UserId) REFERENCES dbo.Users (Id),
        CONSTRAINT CK_StaffAttendance_Status CHECK ([Status] IN ('Present', 'Late', 'Absent', 'Leave'))
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_StaffAttendance_User_Date')
    CREATE UNIQUE INDEX UX_StaffAttendance_User_Date ON dbo.StaffAttendance (TenantId, UserId, [Date]);

IF OBJECT_ID('dbo.StaffLeaves') IS NULL
    CREATE TABLE dbo.StaffLeaves (
        Id              UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_StaffLeaves_Id DEFAULT NEWSEQUENTIALID(),
        TenantId        UNIQUEIDENTIFIER NOT NULL,
        UserId          UNIQUEIDENTIFIER NOT NULL,
        FromDate        DATE             NOT NULL,
        ToDate          DATE             NOT NULL,
        Days            INT              NOT NULL,   -- working days in the range
        Reason          NVARCHAR(300)    NOT NULL,
        [Status]        VARCHAR(10)      NOT NULL CONSTRAINT DF_StaffLeaves_Status DEFAULT 'Pending',
        RejectReason    NVARCHAR(200)    NULL,
        CreatedAt       DATETIME2(0)     NOT NULL CONSTRAINT DF_StaffLeaves_Created DEFAULT SYSUTCDATETIME(),
        DecidedAt       DATETIME2(0)     NULL,
        DecidedByUserId UNIQUEIDENTIFIER NULL,
        CONSTRAINT PK_StaffLeaves PRIMARY KEY (Id),
        CONSTRAINT FK_StaffLeaves_Tenant FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id),
        CONSTRAINT FK_StaffLeaves_User FOREIGN KEY (UserId) REFERENCES dbo.Users (Id),
        CONSTRAINT CK_StaffLeaves_Status CHECK ([Status] IN ('Pending', 'Approved', 'Rejected')),
        CONSTRAINT CK_StaffLeaves_Range CHECK (ToDate >= FromDate)
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_StaffLeaves_Tenant_Status')
    CREATE INDEX IX_StaffLeaves_Tenant_Status ON dbo.StaffLeaves (TenantId, [Status], FromDate);

IF OBJECT_ID('dbo.PushSubscriptions') IS NULL
    CREATE TABLE dbo.PushSubscriptions (
        Id         UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_PushSubscriptions_Id DEFAULT NEWSEQUENTIALID(),
        TenantId   UNIQUEIDENTIFIER NOT NULL,
        UserId     UNIQUEIDENTIFIER NOT NULL,
        Endpoint   NVARCHAR(600)    NOT NULL,
        P256dh     VARCHAR(200)     NOT NULL,
        Auth       VARCHAR(100)     NOT NULL,
        CreatedAt  DATETIME2(0)     NOT NULL CONSTRAINT DF_PushSubscriptions_Created DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_PushSubscriptions PRIMARY KEY (Id),
        CONSTRAINT FK_PushSubscriptions_User FOREIGN KEY (UserId) REFERENCES dbo.Users (Id)
    );
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_PushSubscriptions_Endpoint')
    CREATE UNIQUE INDEX UX_PushSubscriptions_Endpoint ON dbo.PushSubscriptions (Endpoint);

COMMIT;
GO

/* Messages.Type: if a CHECK constraint limits the types, allow the two leave messages */
DECLARE @ck SYSNAME, @sql NVARCHAR(400);
SELECT TOP 1 @ck = cc.name
FROM sys.check_constraints cc
JOIN sys.columns c ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
WHERE cc.parent_object_id = OBJECT_ID('dbo.Messages') AND c.name = 'Type'
  AND cc.definition NOT LIKE '%LeaveRequest%';
IF @ck IS NOT NULL
BEGIN
    SET @sql = N'ALTER TABLE dbo.Messages DROP CONSTRAINT ' + QUOTENAME(@ck);
    EXEC (@sql);
    ALTER TABLE dbo.Messages WITH CHECK ADD CONSTRAINT CK_Messages_Type
        CHECK ([Type] IN ('FeeReminder', 'AbsentAlert', 'Receipt', 'StaffInvite', 'Admission', 'LeaveRequest', 'LeaveAnswer'));
END
GO

/* Check */
SELECT OBJECT_ID('dbo.StaffAttendanceSettings') AS Settings, OBJECT_ID('dbo.StaffAttendance') AS Attendance,
       OBJECT_ID('dbo.StaffLeaves') AS Leaves, OBJECT_ID('dbo.StaffHolidays') AS Holidays,
       OBJECT_ID('dbo.PushSubscriptions') AS Push, COL_LENGTH('dbo.Users', 'StaffStartTime') AS UserTiming;

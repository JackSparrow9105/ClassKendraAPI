/* =====================================================================
   ClassKendra — App PIN + remembered devices
   Run once on your database (SSMS → New Query → select ClassKendra → Execute).
   Safe to run again: each change is skipped if it already exists.
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

/* 1. A 4-digit PIN per user (stored only as a salted hash) */
IF COL_LENGTH('dbo.Users', 'PinHash') IS NULL
    ALTER TABLE dbo.Users ADD PinHash VARCHAR(200) NULL;              -- "v1.<iterations>.<salt>.<hash>"

IF COL_LENGTH('dbo.Users', 'PinFailedAttempts') IS NULL
    ALTER TABLE dbo.Users ADD PinFailedAttempts TINYINT NOT NULL
        CONSTRAINT DF_Users_PinFailedAttempts DEFAULT 0;                -- 5 = locked until the next OTP login

IF COL_LENGTH('dbo.Users', 'PinSetAt') IS NULL
    ALTER TABLE dbo.Users ADD PinSetAt DATETIME2(0) NULL;

/* 2. Devices that logged in with OTP. The app keeps a random key; we keep only its SHA-256 hash.
      PIN unlock and silent renewal only work from a device listed here, so a new phone always needs OTP. */
IF OBJECT_ID('dbo.UserDevices') IS NULL
BEGIN
    CREATE TABLE dbo.UserDevices (
        Id          UNIQUEIDENTIFIER NOT NULL CONSTRAINT DF_UserDevices_Id DEFAULT NEWSEQUENTIALID(),
        UserId      UNIQUEIDENTIFIER NOT NULL,
        KeyHash     BINARY(32)       NOT NULL,
        UserAgent   NVARCHAR(200)    NOT NULL CONSTRAINT DF_UserDevices_UserAgent DEFAULT '',
        CreatedAt   DATETIME2(0)     NOT NULL CONSTRAINT DF_UserDevices_CreatedAt DEFAULT SYSUTCDATETIME(),
        LastUsedAt  DATETIME2(0)     NOT NULL CONSTRAINT DF_UserDevices_LastUsedAt DEFAULT SYSUTCDATETIME(),
        ExpiresAt   DATETIME2(0)     NOT NULL,                          -- pushed forward 30 days on every use
        CONSTRAINT PK_UserDevices PRIMARY KEY (Id),
        CONSTRAINT FK_UserDevices_User FOREIGN KEY (UserId) REFERENCES dbo.Users(Id) ON DELETE CASCADE
    );
    CREATE UNIQUE INDEX UX_UserDevices_KeyHash ON dbo.UserDevices (KeyHash);
    CREATE INDEX IX_UserDevices_User ON dbo.UserDevices (UserId, ExpiresAt);
END

COMMIT;
PRINT 'App PIN tables are ready.';

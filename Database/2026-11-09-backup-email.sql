/* =====================================================================
   ClassKendra — Backup email (log in with an email code if the mobile number stops working)
   Run once on your database (Azure portal → SQL database → Query editor, or SSMS).
   Safe to run again: each change is skipped if it already exists.
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

/* 1. The verified backup email of each user. Only saved after the user typed the code sent to it. */
IF COL_LENGTH('dbo.Users', 'Email') IS NULL
    ALTER TABLE dbo.Users ADD Email NVARCHAR(254) NULL;

IF COL_LENGTH('dbo.Users', 'EmailVerifiedAt') IS NULL
    ALTER TABLE dbo.Users ADD EmailVerifiedAt DATETIME2(0) NULL;

/* 2. One-time codes sent by email: "Verify" (adding a backup email) and "Login" (number not working).
      Only a hash of the code is kept, like the phone OTPs. */
IF OBJECT_ID('dbo.EmailCodes') IS NULL
BEGIN
    CREATE TABLE dbo.EmailCodes (
        Id          BIGINT IDENTITY(1,1) NOT NULL,
        UserId      UNIQUEIDENTIFIER NOT NULL,
        Email       NVARCHAR(254)    NOT NULL,
        Purpose     VARCHAR(10)      NOT NULL,                          -- Verify | Login
        CodeHash    VARBINARY(64)    NOT NULL,
        Attempts    TINYINT          NOT NULL CONSTRAINT DF_EmailCodes_Attempts DEFAULT 0,
        ExpiresAt   DATETIME2(0)     NOT NULL,
        ConsumedAt  DATETIME2(0)     NULL,
        CreatedAt   DATETIME2(0)     NOT NULL CONSTRAINT DF_EmailCodes_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_EmailCodes PRIMARY KEY (Id),
        CONSTRAINT FK_EmailCodes_User FOREIGN KEY (UserId) REFERENCES dbo.Users(Id) ON DELETE CASCADE
    );
    CREATE INDEX IX_EmailCodes_User ON dbo.EmailCodes (UserId, Purpose, CreatedAt DESC);
END

COMMIT;

/* 3. An email can be the backup of only one account. (Separate step: the column must exist first.) */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Users_Email' AND object_id = OBJECT_ID('dbo.Users'))
    EXEC('CREATE UNIQUE INDEX UX_Users_Email ON dbo.Users (Email) WHERE Email IS NOT NULL');

PRINT 'Backup email is ready.';

/* =====================================================================
   ClassKendra — Centre address, phone and logo + frozen receipts
   Run once on your database (Azure portal → SQL database → Query editor, or SSMS).
   Safe to run again: each change is skipped if it already exists.
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

/* 1. Contact details printed on receipts */
IF COL_LENGTH('dbo.Tenants', 'Address') IS NULL
    ALTER TABLE dbo.Tenants ADD Address NVARCHAR(300) NULL;

IF COL_LENGTH('dbo.Tenants', 'Phone') IS NULL
    ALTER TABLE dbo.Tenants ADD Phone VARCHAR(20) NULL;

/* 2. Logos. Every upload is a new row and rows are never changed or deleted,
      so an old receipt keeps showing the logo it was printed with. */
IF OBJECT_ID('dbo.CentreLogos') IS NULL
BEGIN
    CREATE TABLE dbo.CentreLogos (
        Id          UNIQUEIDENTIFIER NOT NULL,
        TenantId    UNIQUEIDENTIFIER NOT NULL,
        ContentType VARCHAR(30)      NOT NULL,                          -- image/png, image/jpeg, image/webp
        Data        VARBINARY(MAX)   NOT NULL,
        CreatedAt   DATETIME2(0)     NOT NULL CONSTRAINT DF_CentreLogos_CreatedAt DEFAULT SYSUTCDATETIME(),
        CONSTRAINT PK_CentreLogos PRIMARY KEY (Id),
        CONSTRAINT FK_CentreLogos_Tenant FOREIGN KEY (TenantId) REFERENCES dbo.Tenants(Id)
    );
    CREATE INDEX IX_CentreLogos_Tenant ON dbo.CentreLogos (TenantId);
END

IF COL_LENGTH('dbo.Tenants', 'LogoId') IS NULL
    ALTER TABLE dbo.Tenants ADD LogoId UNIQUEIDENTIFIER NULL;           -- the current logo; NULL = none

/* 3. Everything printed on a receipt, saved as JSON when the payment is recorded.
      Old receipts are filled in the first time they are opened (marked "rebuilt"). */
IF COL_LENGTH('dbo.Payments', 'ReceiptSnapshot') IS NULL
    ALTER TABLE dbo.Payments ADD ReceiptSnapshot NVARCHAR(MAX) NULL;

COMMIT;
PRINT 'Receipt details are ready.';

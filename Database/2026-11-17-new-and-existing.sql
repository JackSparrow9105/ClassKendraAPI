/* =====================================================================
   ClassKendra — new admissions vs existing students, admission fee (17 Nov 2026)
   Run once, AFTER 2026-11-16-admissions.sql. Safe to run again.

   1. Students.IsNewAdmission   1 = admitted through ClassKendra, 0 = was already studying here
                                (every student already in the database counts as existing)
   2. FeeHeads.IsAdmissionFee   the centre's admission fee (one-time, only for new admissions)
   3. Tenants.AdmissionNoStyle  'Session' = A-2026-0045, 'Serial' = 1251, 1252… (the school's own register)
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

IF COL_LENGTH('dbo.Students', 'IsNewAdmission') IS NULL
    ALTER TABLE dbo.Students ADD IsNewAdmission BIT NOT NULL
        CONSTRAINT DF_Students_IsNewAdmission DEFAULT 0;

IF COL_LENGTH('dbo.FeeHeads', 'IsAdmissionFee') IS NULL
    ALTER TABLE dbo.FeeHeads ADD IsAdmissionFee BIT NOT NULL
        CONSTRAINT DF_FeeHeads_IsAdmissionFee DEFAULT 0;

IF COL_LENGTH('dbo.Tenants', 'AdmissionNoStyle') IS NULL
    ALTER TABLE dbo.Tenants ADD AdmissionNoStyle VARCHAR(10) NOT NULL
        CONSTRAINT DF_Tenants_AdmissionNoStyle DEFAULT 'Session';

COMMIT;
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints WHERE name = 'CK_Tenants_AdmissionNoStyle')
    ALTER TABLE dbo.Tenants WITH CHECK ADD CONSTRAINT CK_Tenants_AdmissionNoStyle
        CHECK (AdmissionNoStyle IN ('Session', 'Serial'));
GO

/* A one-time fee already called "Admission fee" becomes the admission fee (only one per centre). */
WITH f AS (
    SELECT Id, ROW_NUMBER() OVER (PARTITION BY TenantId ORDER BY CreatedAt) AS n
    FROM dbo.FeeHeads
    WHERE IsDeleted = 0 AND Frequency = 'OneTime' AND IsAdmissionFee = 0
      AND LOWER(LTRIM(RTRIM(Name))) IN ('admission fee', 'admission fees', 'admission')
      AND TenantId NOT IN (SELECT TenantId FROM dbo.FeeHeads WHERE IsAdmissionFee = 1 AND IsDeleted = 0)
)
UPDATE h SET IsAdmissionFee = 1, IsOptional = 0
FROM dbo.FeeHeads h JOIN f ON f.Id = h.Id AND f.n = 1;
GO

/* Check */
SELECT COL_LENGTH('dbo.Students', 'IsNewAdmission') AS IsNewAdmission,
       COL_LENGTH('dbo.FeeHeads', 'IsAdmissionFee') AS IsAdmissionFee,
       COL_LENGTH('dbo.Tenants', 'AdmissionNoStyle') AS AdmissionNoStyle;
SELECT Name, Frequency, IsAdmissionFee FROM dbo.FeeHeads WHERE IsDeleted = 0 ORDER BY TenantId, CreatedAt;

/* =====================================================================
   ClassKendra — admissions (16 Nov 2026)
   Run once on your database (SSMS → New Query → select ClassKendra → Execute).
   Safe to run again: each change is skipped if it already exists.

   1. Students.AdmissionNo         "A-2026-0045", restarting each session
   2. AdmissionSequences           last number used per centre per session
   3. Tenants.SendAdmissionMessages  1 = WhatsApp admission confirmation to the parent (default)
   4. Messages.Type                 allows the new type 'Admission'
   5. Existing students get admission numbers too (by joining date), so their slip works
   ===================================================================== */

SET XACT_ABORT ON;
BEGIN TRAN;

IF COL_LENGTH('dbo.Students', 'AdmissionNo') IS NULL
    ALTER TABLE dbo.Students ADD AdmissionNo VARCHAR(20) NULL;

IF OBJECT_ID('dbo.AdmissionSequences') IS NULL
    CREATE TABLE dbo.AdmissionSequences (
        TenantId    UNIQUEIDENTIFIER NOT NULL,
        SessionYear SMALLINT         NOT NULL,
        LastNumber  INT              NOT NULL,
        CONSTRAINT PK_AdmissionSequences PRIMARY KEY (TenantId, SessionYear),
        CONSTRAINT FK_AdmissionSequences_Tenant FOREIGN KEY (TenantId) REFERENCES dbo.Tenants (Id)
    );

IF COL_LENGTH('dbo.Tenants', 'SendAdmissionMessages') IS NULL
    ALTER TABLE dbo.Tenants ADD SendAdmissionMessages BIT NOT NULL
        CONSTRAINT DF_Tenants_SendAdmissionMessages DEFAULT 1;

COMMIT;
GO

/* One admission number per centre */
IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_Students_Tenant_AdmissionNo')
    CREATE UNIQUE INDEX UX_Students_Tenant_AdmissionNo ON dbo.Students (TenantId, AdmissionNo)
        WHERE AdmissionNo IS NOT NULL;
GO

/* Messages.Type: if a CHECK constraint limits the types, add 'Admission' to it */
DECLARE @ck SYSNAME, @sql NVARCHAR(400);
SELECT TOP 1 @ck = cc.name
FROM sys.check_constraints cc
JOIN sys.columns c ON c.object_id = cc.parent_object_id AND c.column_id = cc.parent_column_id
WHERE cc.parent_object_id = OBJECT_ID('dbo.Messages') AND c.name = 'Type'
  AND cc.definition NOT LIKE '%Admission%';
IF @ck IS NOT NULL
BEGIN
    SET @sql = N'ALTER TABLE dbo.Messages DROP CONSTRAINT ' + QUOTENAME(@ck);
    EXEC (@sql);
    ALTER TABLE dbo.Messages WITH CHECK ADD CONSTRAINT CK_Messages_Type
        CHECK ([Type] IN ('FeeReminder', 'AbsentAlert', 'Receipt', 'StaffInvite', 'Admission'));
END
GO

/* Existing students without a number: number them per centre and session by joining date */
SET XACT_ABORT ON;
BEGIN TRAN;

WITH s AS (
    SELECT st.Id, st.TenantId,
           CAST(CASE WHEN MONTH(st.JoinedOn) >= ISNULL(fs.SessionStartMonth, 4)
                     THEN YEAR(st.JoinedOn) ELSE YEAR(st.JoinedOn) - 1 END AS SMALLINT) AS SessionYear,
           st.JoinedOn, st.CreatedAt
    FROM dbo.Students st
    LEFT JOIN dbo.FeeSettings fs ON fs.TenantId = st.TenantId
    WHERE st.AdmissionNo IS NULL
),
n AS (
    SELECT s.Id, s.TenantId, s.SessionYear,
           ISNULL(q.LastNumber, 0)
             + ROW_NUMBER() OVER (PARTITION BY s.TenantId, s.SessionYear ORDER BY s.JoinedOn, s.CreatedAt) AS Num
    FROM s
    LEFT JOIN dbo.AdmissionSequences q ON q.TenantId = s.TenantId AND q.SessionYear = s.SessionYear
)
UPDATE st
   SET AdmissionNo = CONCAT('A-', n.SessionYear, '-', RIGHT(CONCAT('0000', n.Num), 4))
FROM dbo.Students st
JOIN n ON n.Id = st.Id;

/* Keep the counters ahead of the numbers just given */
MERGE dbo.AdmissionSequences AS t
USING (
    SELECT TenantId,
           CAST(SUBSTRING(AdmissionNo, 3, 4) AS SMALLINT) AS SessionYear,
           MAX(CAST(SUBSTRING(AdmissionNo, 8, 10) AS INT)) AS LastNumber
    FROM dbo.Students
    WHERE AdmissionNo LIKE 'A-____-%'
    GROUP BY TenantId, SUBSTRING(AdmissionNo, 3, 4)
) AS x
ON t.TenantId = x.TenantId AND t.SessionYear = x.SessionYear
WHEN MATCHED AND t.LastNumber < x.LastNumber THEN UPDATE SET LastNumber = x.LastNumber
WHEN NOT MATCHED THEN INSERT (TenantId, SessionYear, LastNumber) VALUES (x.TenantId, x.SessionYear, x.LastNumber);

COMMIT;
GO

/* Check */
SELECT TOP 20 Name, JoinedOn, AdmissionNo FROM dbo.Students ORDER BY CreatedAt DESC;
SELECT * FROM dbo.AdmissionSequences;

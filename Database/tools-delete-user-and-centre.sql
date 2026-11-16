/* =====================================================================
   ClassKendra — delete a phone number (and its centre) to test from the start
   !! Deletes data for good. Use only for test numbers. !!

   - If the number is a centre OWNER: deletes the whole centre — students, fees, payments,
     attendance, messages, staff logins, plan history — and the owner.
   - If it is office staff / a teacher: deletes only that login.
   - If it never created a centre: deletes only that login.

   How to run: change @Phone below (10 digits), select ClassKendra, press F5.
   Step 1 shows what will be deleted. To actually delete, set @ReallyDelete = 1 and run again.
   ===================================================================== */

DECLARE @Phone        VARCHAR(15) = '9161495113';
DECLARE @ReallyDelete BIT         = 0;          -- 0 = only show, 1 = delete

SET NOCOUNT ON;
SET XACT_ABORT ON;

DECLARE @UserId UNIQUEIDENTIFIER, @Role NVARCHAR(20), @TenantId UNIQUEIDENTIFIER;
SELECT @UserId = Id, @Role = Role, @TenantId = TenantId FROM dbo.Users WHERE Phone = @Phone;

IF @UserId IS NULL
BEGIN
    PRINT 'No user with phone ' + @Phone + '. Only old login codes (if any) will be removed.';
    IF @ReallyDelete = 1 DELETE FROM dbo.OtpCodes WHERE Phone = @Phone;
    RETURN;
END

DECLARE @WholeCentre BIT = CASE WHEN @Role = 'Owner' AND @TenantId IS NOT NULL THEN 1 ELSE 0 END;

/* ---------- Step 1: what will be deleted ---------- */
SELECT @Phone AS Phone, @Role AS Role,
       CASE WHEN @WholeCentre = 1 THEN 'Whole centre + all its users' ELSE 'Only this login' END AS WillDelete,
       (SELECT Name FROM dbo.Tenants WHERE Id = @TenantId) AS Centre,
       (SELECT COUNT(*) FROM dbo.Users    WHERE TenantId = @TenantId) AS Users,
       (SELECT COUNT(*) FROM dbo.Students WHERE TenantId = @TenantId) AS Students,
       (SELECT COUNT(*) FROM dbo.Payments WHERE TenantId = @TenantId) AS Payments,
       (SELECT COUNT(*) FROM dbo.Messages WHERE TenantId = @TenantId) AS Messages;

IF @ReallyDelete = 0
BEGIN
    PRINT 'Nothing deleted. Set @ReallyDelete = 1 and run again to delete.';
    RETURN;
END

/* ---------- Step 2: delete ---------- */
BEGIN TRAN;

-- The users to remove: the whole centre's logins, or just this one.
DECLARE @Users TABLE (Id UNIQUEIDENTIFIER PRIMARY KEY, Phone VARCHAR(15));
IF @WholeCentre = 1
    INSERT @Users SELECT Id, Phone FROM dbo.Users WHERE TenantId = @TenantId OR Id = @UserId;
ELSE
    INSERT @Users SELECT Id, Phone FROM dbo.Users WHERE Id = @UserId;

IF @WholeCentre = 1
BEGIN
    -- Attendance
    DELETE x FROM dbo.AttendanceAbsences x JOIN dbo.AttendanceSheets s ON s.Id = x.SheetId WHERE s.TenantId = @TenantId;
    DELETE FROM dbo.AttendanceSheets WHERE TenantId = @TenantId;

    -- Online payment orders and payments
    DELETE x FROM dbo.PaymentOrderDues x JOIN dbo.PaymentOrders o ON o.Id = x.PaymentOrderId WHERE o.TenantId = @TenantId;
    DELETE FROM dbo.PaymentOrders WHERE TenantId = @TenantId;
    DELETE x FROM dbo.PaymentAllocations x JOIN dbo.Payments p ON p.Id = x.PaymentId WHERE p.TenantId = @TenantId;
    DELETE FROM dbo.Payments WHERE TenantId = @TenantId;
    DELETE FROM dbo.FeeDues  WHERE TenantId = @TenantId;

    -- Students and everything hanging off them
    DELETE x FROM dbo.StudentOptionalFeeHeads x JOIN dbo.Students s ON s.Id = x.StudentId WHERE s.TenantId = @TenantId;
    DELETE FROM dbo.StudentConcessions WHERE TenantId = @TenantId;
    DELETE x FROM dbo.StudentBatchHistory x JOIN dbo.Students s ON s.Id = x.StudentId WHERE s.TenantId = @TenantId;
    DELETE FROM dbo.Messages WHERE TenantId = @TenantId;
    DELETE FROM dbo.Students WHERE TenantId = @TenantId;
    DELETE FROM dbo.PromotionRuns WHERE TenantId = @TenantId;

    -- Fee setup, batches, teacher-batch links
    DELETE x FROM dbo.FeeHeadAmounts x JOIN dbo.FeeHeads h ON h.Id = x.FeeHeadId WHERE h.TenantId = @TenantId;
    DELETE FROM dbo.FeeHeads    WHERE TenantId = @TenantId;
    DELETE FROM dbo.FeeSettings WHERE TenantId = @TenantId;
    DELETE x FROM dbo.UserBatches x JOIN dbo.Batches b ON b.Id = x.BatchId WHERE b.TenantId = @TenantId;
    DELETE FROM dbo.Batches WHERE TenantId = @TenantId;

    -- Receipts numbering, plans, logo
    DELETE FROM dbo.ReceiptSequences WHERE TenantId = @TenantId;
    IF OBJECT_ID('dbo.AdmissionSequences') IS NOT NULL
        EXEC sp_executesql N'DELETE FROM dbo.AdmissionSequences WHERE TenantId = @t', N'@t UNIQUEIDENTIFIER', @t = @TenantId;
    DELETE FROM dbo.Subscriptions    WHERE TenantId = @TenantId;
    UPDATE dbo.Tenants SET LogoId = NULL WHERE Id = @TenantId;
    DELETE FROM dbo.CentreLogos      WHERE TenantId = @TenantId;
END
ELSE
BEGIN
    -- Staff / teacher: their own links only (payments and attendance they recorded stay, without the link).
    DELETE FROM dbo.UserBatches WHERE UserId = @UserId;
    UPDATE dbo.Payments         SET ReceivedByUserId = NULL WHERE ReceivedByUserId = @UserId;
    UPDATE dbo.AttendanceSheets SET MarkedByUserId   = NULL WHERE MarkedByUserId   = @UserId;
END

-- The logins themselves
DELETE x FROM dbo.UserDevices x JOIN @Users u ON u.Id = x.UserId;
DELETE x FROM dbo.EmailCodes  x JOIN @Users u ON u.Id = x.UserId;
DELETE x FROM dbo.UserBatches x JOIN @Users u ON u.Id = x.UserId;
DELETE x FROM dbo.OtpCodes    x JOIN @Users u ON u.Phone = x.Phone;
DELETE x FROM dbo.Users       x JOIN @Users u ON u.Id = x.Id;

IF @WholeCentre = 1
    DELETE FROM dbo.Tenants WHERE Id = @TenantId;

COMMIT;
PRINT 'Deleted. You can now sign up again with ' + @Phone + '.';

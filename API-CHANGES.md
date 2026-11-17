# ClassKendra API — notes

The Angular app (`classkendra-web`, `src/app/core/models/models.ts` + README "Endpoints the app calls")
is the contract. Every endpoint below returns exactly those shapes.

## Fixed first (important)
`Data/ClassKendraContext.Tenant.cs` was excluded from the build in the .csproj, so **no tenant filter was running** —
every centre could see every other centre's data. It is included again.
Background jobs and the parent pay page switch to one centre with `CurrentUser.ActAsTenant(...)`.

## Endpoints (all under /api/v1)

| Method & path | Returns | Who |
|---|---|---|
| `POST auth/otp`, `POST auth/verify`, `GET auth/me` | `AuthResponse` / `AuthUser` | anyone / signed in |
| `GET/POST/PUT centre`, `PUT centre/setup-complete`, `PUT centre/message-language` | `Centre` | owner (GET: all) |
| `GET/POST batches`, `PUT/DELETE batches/{id}` | `Batch` | owner, staff (GET: teachers get theirs) |
| `GET/POST students`, `GET/PUT/DELETE students/{id}` | `Student` | owner, staff (GET: teachers) |
| `POST students/import`, `POST students/promote` | `ImportResult`, `PromotionResult` | owner, staff / owner |
| `POST/DELETE students/{id}/concessions[/{cid}]` | `Student` | owner, staff |
| `GET students/{id}/fees` | `StudentFeeHistory` | owner, staff |
| `GET/PUT fees/setup` | `{ settings, heads }` | owner, staff (PUT: owner) |
| `GET fees?month=&status=Pending\|Paid\|All&search=&batchId=` | `StudentFees[]` | owner, staff |
| `POST fees/remind { studentIds }`, `POST fees/remind-all?month=` | `{ sent }` | owner, staff |
| `POST fees/raise` | `{ created }` | owner |
| `POST payments`, `GET payments/{id}`, `GET payments?from=&to=`, `DELETE payments/{id}` | `PaymentView` | owner, staff (DELETE: owner) |
| `GET reports/collection?month=` | `CollectionReport` | owner, staff |
| `GET/POST attendance/{batchId}?date=` | `AttendanceSheet` / `{ alerted }` | all (teachers: own batches) |
| `GET/POST staff`, `PUT/DELETE staff/{id}` | `StaffMember` | owner |
| `GET messages?type=&status=&studentId=&search=`, `GET messages/summary`, `POST messages/{id}/retry` | `MessageLog[]`, `MessageSummary`, `MessageLog` | owner, staff |
| `GET dashboard` | `DashboardSummary` | owner, staff |
| `GET billing/plans`, `POST billing/checkout { plan, cycle }` | `Plan[]`, `{ paymentUrl }` | owner |
| `GET public/pay/{token}`, `POST …/order`, `POST …/verify`, `GET …/orders/{orderId}` | `ParentPayPage`, `ParentPayOrder`, `PaymentView` | no login |
| `POST webhooks/razorpay`, `GET/POST webhooks/whatsapp` | — | Razorpay / Meta |

## Billing rules — same as the app's `core/fees/schedule.ts` and `concessions.ts`
- Monthly every month; Quarterly at session start +0/+3/+6/+9; HalfYearly ("term-wise") at +0/+6;
  Yearly at session start; Quarterly/HalfYearly/Yearly also in a student's joining month; OneTime only in the joining month.
- Periods: `2026-11`, `2026-Q3` "Q3 · Oct–Dec", `2026-T2` "Term 2 · 2026–27", `2026-Y` "2026–27", `OT` "One-time".
- Due date = the due day of the month the bill is raised. Earlier months are not back-filled.
- Discounts: percentages on the gross, then fixed amounts, never more than the bill. 100% discounted bills are not raised.
  Changing a concession re-prices bills with no money against them yet.
- Bills are raised when fees are saved, students are added/imported/promoted/opt in, when the dashboard or Fees screen
  opens, and by the daily job.

## Payments
- Counter payments go to the ticked bills, oldest first, never more than they owe. Modes: Cash, UPI, Cheque (Online = parents).
- Receipt numbers `R-2026-0001`, per session, safe with two counters at once.
- Parent online payments: the page opens Razorpay Checkout, then `/verify` records it. The webhook does the same if the
  parent closes the page. Whichever comes first wins; the other finds it done.
- Plan payments: a Razorpay Payment Link; `payment_link.paid` on the webhook switches the plan on.

## Plan rules
- Trial or paid plan ended (+3 grace days): read-only, every change gets **402** with a message (except login,
  centre settings and billing).
- Student limit = the plan's MaxStudents. WhatsApp messages count against `Tenants.MessageLimit`, reset monthly.

## Settings (user secrets / Azure App Settings)
```
Jwt:Key, Otp:Secret                      (already used)
App:PublicUrl                            the Angular site, e.g. https://app.classkendra.in (pay links, invite link, plan return)
Cors:Origins                             add the Angular site's address
WhatsApp:Provider = Meta                 (anything else = log only, for development)
WhatsApp:PhoneNumberId, WhatsApp:AccessToken, WhatsApp:AppSecret, WhatsApp:VerifyToken
Razorpay:KeyId, Razorpay:KeySecret, Razorpay:WebhookSecret
Jobs:Enabled = false                     (switch off background jobs, e.g. on a second instance)
```
Razorpay webhook events: `payment.captured`, `order.paid`, `payment.failed`, `payment_link.paid`.

WhatsApp templates (Meta, English `en` + Hindi `hi`) — wording is in the app's `core/messages/templates.ts`,
parameter order in `Features/Messages/MessageService.cs`:
`fee_reminder`, `absent_alert`, `payment_receipt`, `payment_receipt_due`, `staff_invite` (en), and an Authentication template `login_otp`.

## Values written to the database (check any CHECK constraints allow them)
- FeeDues.Status: Pending, Partial, Paid, Cancelled
- Payments.Mode: Cash, UPI, Cheque, Online · Payments.ReceiptNo: R-2026-0001
- PaymentOrders.Status: Created, Paid, Failed
- Messages.Type: FeeReminder, AbsentAlert, Receipt, StaffInvite · Messages.Status: Queued, Sent, Delivered, Read, Failed
- Subscriptions.Status: Created, Active · Subscriptions.Cycle: Monthly, Yearly
- Users.Role: Owner, Staff, Teacher

## Backup email (2026-11-09)
Lets a user get their login code by email when their mobile number stops working.
Database: run `Database/2026-11-09-backup-email.sql` once.

Endpoints (all under /api/v1/auth):
- `POST /email { email, pin? }` → `{ sentTo }` — signed in; sends a code to the new email. Users with a PIN must send it.
- `POST /email/verify { code }` → user — saves the email as the verified backup email; the previous one is told.
- `DELETE /email` → user — removes it; the removed address is told.
- `POST /email-login { phone }` → `{ sentTo: "a•••@gmail.com" }` — 404 if the number has no backup email.
- `POST /email-login/verify { phone, code }` → `{ token, user, deviceToken }` — same as a phone OTP login.
`AuthUserDto` now also has `email` (null = none).

Settings:
```
Email:Provider = Smtp                    (anything else = log only: codes show in Log stream as "DEV EMAIL")
Email:Host, Email:Port (587), Email:User, Email:Password, Email:From, Email:FromName
```
Brevo: Host smtp-relay.brevo.com, Port 587, User = the SMTP login shown in Brevo, Password = an SMTP key,
From = a sender address verified in Brevo. Gmail: smtp.gmail.com, 587, your Gmail + an app password.

## Demo login codes on screen + new plans (2026-11-16)

### Login code on screen (demo mode)
`Otp:ShowOnScreen = true` (on in appsettings.json for now): `POST /auth/otp` returns **200 `{ demoCode }`**
instead of 204, and nothing is sent on WhatsApp. The code is still saved (hashed) in `OtpCodes` and checked by
`POST /auth/verify` exactly as before. The app shows it on the OTP screen with a "Use this code" button.
**Anyone can sign in to any number while this is on** — set it to `false` (Azure App Setting `Otp__ShowOnScreen`)
before real customers use the app.

### New plans
Database: run `Database/2026-11-16-new-plans.sql` once.
- Tuition & coaching: Free (25 students, 100 messages, ₹0) · TuitionLite ₹399 · Starter ₹799 · Growth ₹1,999 · Pro ₹3,499
- Schools: School100 ₹749 · School200 ₹999 · School300 ₹1,299 · School500 ₹1,899 · School1000 ₹2,999
- Old School / SchoolPlus / SchoolMax are switched off (IsActive = 0), not deleted.
- `GET billing/plans` now also returns `maxStudents` (for the price per student). The Free plan can't be bought.

### Free plan rules (tuition / coaching only)
- When a coaching centre's trial or paid plan ends (+3 grace days) it moves to **Free** instead of becoming read-only:
  `Tenants.PlanCode = 'Free'`, `MessageLimit` = the Free plan's limit. Schools still become read-only.
- **Owner login only:** staff and teachers can't sign in (403 `{ message, freePlan: true }`), every API call by them gets 403,
  and the owner can't add staff.
- **Up to 25 students:** with more, changes get 402 (removing students is still allowed).
- **No UPI payment link:** `GET/POST public/pay/{token}` say online payment isn't available; fee reminders have no link.
- **"Powered by ClassKendra":** messages use templates named `<template>_free` (fee_reminder_free has no link
  parameter) and the saved text ends with the footer. Create these in Meta when WhatsApp goes live.

### WhatsApp message settings per centre
Database: run `Database/2026-11-16-message-settings.sql` once (adds `Tenants.SendReceipts`, `Tenants.AbsentAlerts`).
- `PUT centre/message-settings { sendReceipts, absentAlerts }` (owner). `Centre` now has `sendReceipts` and `absentAlerts`.
- Receipts: sent after every payment when `SendReceipts` is on. `POST payments` `sendReceipt: true/false` still overrides it;
  missing = the setting. Online (parent) payments follow the setting.
- Absent alerts: `Every` (each absence) · `TwoDays` (only when the student was also absent at the batch's previous class) · `Off`.
- Fee reminder timing is unchanged: FeeSettings RemindBefore / RemindOnDue / RemindAfter (Fees › settings in the app).

### Admissions
Database: run `Database/2026-11-16-admissions.sql` once (Students.AdmissionNo, AdmissionSequences,
Tenants.SendAdmissionMessages, 'Admission' message type; numbers existing students too).
- `POST students` gives the student an admission number (`A-2026-0045`, per session, safe with two at once) and,
  after this month's bills are raised, queues a WhatsApp **admission confirmation** with the fees due now.
  Body `sendConfirmation: false` skips the message for that student. Response adds `admissionNo` and `admissionMessageSent`.
- `POST students/import` numbers imported students but sends no confirmations.
- `POST students/{id}/admission-message` sends the confirmation again (even when turned off) → `{ sent }`.
- `Student` has `admissionNo`; `Centre` has `sendAdmissionMessages`; `PUT centre/message-settings` takes it (optional).
- Template `admission_confirmation` (en + hi), parameters: centre, student, batch/class, admission no, fees text
  ("Fees due: ₹2,500 by 10/12" / "Welcome aboard!"). Free plan: `admission_confirmation_free`.

### New admissions vs existing students, admission fee (17 Nov)
Database: run `Database/2026-11-17-new-and-existing.sql` once, after the admissions script
(Students.IsNewAdmission, FeeHeads.IsAdmissionFee, Tenants.AdmissionNoStyle; everyone already in the database = existing).
- `Student.isNewAdmission`: true = admitted through ClassKendra, false = already studying here (and every Excel import).
- `POST students` takes `isNewAdmission` (default true), `admissionNo` (typed from the register; empty = automatic)
  and `waiveAdmissionFee` (new admissions only; saved as a 100% "Other · Admission fee waived" discount on the admission fee).
  Existing students get no admission fee, no one-time fees, no catch-up quarterly/term/yearly bill and no WhatsApp message.
- `PUT students/{id}` takes `admissionNo` (empty = unchanged) and `isNewAdmission`
  (new → existing cancels the unpaid admission fee).
- Typed admission numbers: up to 20 letters/digits/- / . ; unique per centre ("Admission no. 1250 is already given to …").
- `POST students/import` rows take an optional `admissionNo`; imported students are existing students.
- Fee setup: `FeeHead.admissionFee` (at most one; always one-time, everyone admitted pays).
- `GET centre/admission-numbering` → `{ style: "Session"|"Serial", nextNumber, example }`;
  `PUT centre/admission-numbering { style, nextNumber? }`. "Serial" continues the school's register (1251, 1252…);
  without nextNumber it starts after the highest plain number already in use. Automatic numbers skip numbers already taken.
- Dashboard `insights.newThisMonth` counts new admissions only. Admission confirmations go to new admissions only.

### Staff attendance for schools, leave, app notifications (17 Nov)
Database: run `Database/2026-11-17-staff-attendance.sql` once. NuGet: `WebPush` 1.0.12 (restored on build).
Config: `Push:PublicKey` is in appsettings.json; put `Push:PrivateKey` in Azure → App Service → Configuration
(`Push__PrivateKey`). Without it everything works except app notifications. `App:PublicUrl` must be the app's address.

Only for schools on School 200 / 300 / 500 / 1000 (and trials). Others get 403 `{ reason: "NotSchool" | "Plan" }`.
- `GET staff-attendance/status` → `{ enabled, reason }`
- Teacher / office staff: `GET me/today`, `POST me/check-in { latitude, longitude, accuracy }` (within the radius, 200 m;
  up to 50 m of GPS accuracy allowed), `POST me/check-out`, `GET me/month?month=yyyy-MM`,
  `GET me/leaves`, `POST me/leaves { from, to, reason }`, `DELETE me/leaves/{id}` (pending only).
- Owner: `GET today?date=`, `PUT mark { userId, date, status: Present|Late|Absent|Leave|Clear, checkIn: "08:05" }`,
  `GET leaves`, `POST leaves/{id}/approve`, `POST leaves/{id}/reject { reason }`, `GET report?month=`,
  `GET/PUT settings { start, end, weeklyOff }`, `PUT settings/location { latitude, longitude }`,
  `PUT settings/notifications { notifyLeaveRequests, notifyLeaveAnswers, leaveWhatsApp }`,
  `POST holidays { name, from, to }`, `DELETE holidays/{id}`, `PUT timings/{userId} { start, end }` (nulls = school timing).
- Rules: Late = more than 10 min after the person's start; no half days; holidays and weekly offs never count;
  approved leave = Leave; a working day without check-in = Absent once the school day is over (NotYet before that).
- Leave notifications: app notification (free) to the owner on a request and to the teacher on approve/reject;
  WhatsApp only when "Also send on WhatsApp" is on, and only to people the app notification didn't reach.
  Message types `LeaveRequest`, `LeaveAnswer`; Meta templates `leave_request` [name, dates, days, reason],
  `leave_approved` [dates, centre], `leave_rejected` [dates, reason, centre] (+ `_free` variants unused: schools only).
- App notifications: `GET push/key`, `POST push/subscriptions { endpoint, keys: { p256dh, auth } }`, `DELETE push/subscriptions?endpoint=`.


---

# 18 Nov 2026 — safety changes (paging, cancelled payments, activity log, leaving, holidays, 300 m)

**Database first:** run `Database/2026-11-18-leaving-holidays-activity-log.sql` (after `2026-11-17-staff-attendance.sql`). Safe to run again.
The Angular app already has the matching types in `core/models/models.ts` and calls these endpoints.

## Long lists come in pages
All take `page` (starts at 1) and `pageSize` (default 50, at most 200) and return `{ items, total, page, pageSize }`.
The old list endpoints still work and still return everything.

| Endpoint | Extra filters | Extra fields in the reply |
|---|---|---|
| `GET students/page` | `batchId, search, kind=new\|existing, status=Active\|Left` | `newCount` |
| `GET fees/page` | `month, status, search, batchId` (as `GET fees`) | `pendingTotal, pendingCount, paidTotal` — for everyone who matches, not only the page |
| `GET payments/page` | `from, to, mode, batchId, studentId, search, includeCancelled` | `totalAmount` (cancelled never counted) |
| `GET messages/page` | `type, status, studentId, search` | |
| `GET activity` (owner) | `action, search` | |

## Payments are cancelled, never deleted
- `DELETE payments/{id}` is **gone** (405). Use `POST payments/{id}/cancel { reason }` → `PaymentView` with `cancelled: true`.
- Owner **and** office staff. The reason is required (10–300 letters). When staff cancel, every owner gets an app notification.
- The row stays (`CancelledAt, CancelledByUserId, CancelledByName, CancelReason`), its money goes back on the bills, and the
  receipt number is never reused. Online (Razorpay) payments can't be cancelled here.
- **Every query on `db.Payments` skips cancelled payments automatically** (query filter `NotCancelled` in
  `ClassKendraContext.Tenant.cs`), so totals, reports and balances need no change. To include them:
  `db.Payments.IgnoreQueryFilters([ClassKendraContext.NotCancelled])` — the centre filter stays on.
  Don't call the plain `IgnoreQueryFilters()` on Payments unless you also want other centres' rows.
- `GET payments/{id}` and `GET students/{id}/fees` show cancelled receipts too, marked `cancelled`.

## Activity log (`AuditLogs`)
`AuditService.AddAsync(action, entityType, entityId, summary, reason, data, ct)` adds a line that is saved by the caller's
next `SaveChanges`, so the line and the change are written together. Actions so far: `PaymentCancelled, StudentLeft,
StudentRejoined, FeesWaived, HolidayAdded, HolidayRemoved, CheckInRadiusChanged`. Nothing edits or deletes these rows.

## Students leaving
- `POST students/{id}/leave { leftOn, reason, note, dues, waiveReason, keepReminding }` → `{ student, billsCancelled, stillOwed, waived }`
  - `reason`: `Left | Transferred | PassedOut | Removed`
  - Bills for months **after** the leaving month with no money against them are cancelled.
  - `dues: "Keep"` (default): earlier unpaid bills stay owed. `keepReminding` (default true) keeps fee reminders going.
  - `dues: "Waive"` + `waiveReason`: unpaid bills are cancelled; part-paid bills close at what was paid (the rest becomes discount).
- `POST students/{id}/rejoin { batchId? }` → `Student`. Bills start again from this month.
- `DELETE students/{id}` still works for old app versions = leave today, reason `Left`, dues kept.
  **Bug fixed:** it used to cancel every unpaid bill, which wiped real arrears.
- `Student` has `leftOn, leftReason, leftNote, remindAfterLeaving`.
- The parent pay link keeps working for a student who left while they still owe something.

## Holidays — one list for the whole centre
- Table `StaffHolidays` is renamed `Holidays` (rows kept); entity `Holiday`, `db.Holidays`.
- `GET holidays?from=&to=` (everyone), `GET holidays/on?date=`, `POST holidays { name, from, to }`, `DELETE holidays/{id}` (owner).
  Works for coaching centres and every plan. The old `staff-attendance/holidays` endpoints still work on the same table.
- On a holiday: no absent alerts (`GET attendance/{batchId}` returns `holidayName`; the register can still be saved),
  automatic fee reminders are skipped and sent on the next working day, dashboards show no classes, staff are not marked absent.

## Staff check-in distance
- Default 300 m (was 200); the script raises existing schools below 300 to 300.
- `PUT staff-attendance/settings/radius { radius }` — owner, 50 to 1,000 m.


---

# 19 Nov 2026 — advance, refunds, waivers and charges, opening balance, late fee, batch change, siblings

**Database first:** run `Database/2026-11-19-advance-refunds-late-fees-siblings.sql` (after the 18 Nov script). Safe to run again.

## Advance (money paid that isn't on any fee)
- No column: `AdvanceService` works it out as payments − what they put on fees − refunds (cancelled payments never count).
- New bills use it automatically, oldest bill first, from the oldest payment (`AdvanceService.ApplyAsync`, called after
  every billing run, late fee, charge, waiver and opening balance).
- `POST payments` takes `keepExtra: true`: money beyond the ticked fees goes on the student's other unpaid fees, then
  stays as advance (the receipt shows "Kept as advance"). Without it, paying more than the ticked fees is still refused.
- `StudentFeeHistory.advance`. Cancelling a payment whose advance was already refunded is refused.

## Refunds
- `POST students/{id}/refunds { amount, mode: Cash|UPI|Cheque|Bank, refundedOn, reason }` → `RF-2026-0001`.
- Only out of the advance. To refund money already on a fee: waive that much of the fee first (the paid part above the
  new amount becomes advance), then refund. Refunds can't be undone. Activity log.
- Collection report: `refunded` and `waived` for the month.

## Waive part of a fee / extra charges
- `POST students/{id}/dues/{dueId}/waive { amount, reason }` — lowers one bill (stored as discount + a `FeeAdjustments`
  row, so re-pricing keeps it). Waived to ₹0 = closed, and never raised again (a fully waived late fee stays gone).
- `POST students/{id}/charges { name, amount, dueDate, reason }` — a bill for one student under the hidden `Charge` type.

## Opening balance (centres starting mid-year)
- `GET/PUT students/{id}/opening-balance { previousDues, advancePaid, asOn }`.
- Previous dues = one bill "Previous dues" (period `OB`). Advance paid = one payment of mode `Opening` (number `OB-…`),
  excluded from every collection total (reports, dashboard, receipts list, "paid this session").
- Changeable until money is paid on the previous dues or the advance is used.
- Excel import: optional columns **Previous dues** and **Advance paid**.

## Late fee (Fees and reminders screen)
- `FeeSettings.lateFeeType` None | Once | PerDay, `lateFeeAmount`, `lateFeeAfterDays`, `lateFeeMax` (PerDay; 0 = no limit).
- `LateFeeService` runs in the daily job (after billing, before reminders) and right after fee settings are saved.
  One late-fee bill per overdue bill of the centre's own fees (`ParentDueId`), labelled with the fee and period.
  Per-day fees grow until the bill is paid and never go down by themselves. Students who left get none.
  Late fees count in what's owed but never trigger a reminder on their own.

## Hidden fee types
`FeeHeads.Kind` / `FeeDues.Kind`: `OpeningBalance`, `LateFee`, `Charge`. One hidden fee type per kind per centre
(made when first needed). They are excluded from fee setup, the billing run, concessions and re-pricing.

## Batch change mid-month
`PUT students/{id}` takes `chargeNewBatchThisMonth`. Default: this month's fees stay at the old batch's amounts, the new
batch's fees start next month. `true`: this month's fees with nothing paid on them are re-priced at the new batch's
amounts. Every batch change is recorded in `StudentBatchHistories` and the activity log.

## Siblings (same parent phone)
- Sibling discount in fee settings: `siblingDiscountType` None | Percent | Amount, `siblingDiscountValue`,
  `siblingDiscountHeadId` (null = every fee). The child who joined first pays full fees; every other child still
  studying here gets it, unless they already have a manual "Sibling" discount. Applied to unpaid fees when the setting
  changes and whenever a family changes (a child joins, leaves, rejoins, or a phone number changes).
- **One WhatsApp reminder per family**: "Aarav & Diya's fees of ₹9,000…" with one pay link. Same approved template
  (`fee_reminder`), the names go in the student-name parameter. At most one reminder per phone per day.
- `GET students/{id}/family` → brothers and sisters with their unpaid fees.
- `POST payments/family { items: [{ studentId, dueIds }], amount, mode, paidOn, note }` → one receipt per child, spread
  over all ticked fees oldest first, linked by `Payments.GroupId` (`PaymentView.together`).
- Parent pay page shows every child (`children`), and one online payment is split into a receipt per child. The Razorpay
  payment id is kept on the first receipt only (it must be unique).


---

# 20 Nov 2026 — Excel downloads, all my data, undo, offline attendance

**Database:** run `Database/2026-11-20-undo-and-offline.sql` (adds `PromotionRuns.UndoneAt`, `UndoneByName`).

## Excel downloads (`Features/Exports`)
- `Common/Xlsx.cs` — a small .xlsx writer with no NuGet package: frozen bold header, filters, column widths, real
  numbers/money/dates. Checked with openpyxl; opens in Excel, Google Sheets, LibreOffice.
- `GET exports/{students|fees|receipts|refunds|daybook}?from=&to=&status=` — owner and office staff. Dates default to
  this session's start → today, at most 400 days. Day-book = every receipt and refund, plus daily totals by mode.
- `GET exports/all` — owner only: a .zip of 9 Excel files (students, fees + fee changes, receipts + refunds, day-book,
  attendance, staff + staff attendance + leave, setup + holidays, WhatsApp messages, activity log) and a README.
- Every download is written to the activity log (`DataExported`).

## Undo
- `GET students/promotions` (owner) — promotions of the last 60 days with `canUndo`.
- `POST students/promotions/{id}/undo` — within 7 days, once. Students go back to their old batch/class, students
  marked as leaving come back, fees raised since the promotion are removed. Left as they are (and listed with the reason):
  students paid for since the promotion, students whose new fees already have money on them, students changed by hand since.
- `POST payments/{id}/restore { reason }` (owner) — a cancelled payment counts again with its receipt number; the money
  goes on unpaid fees oldest first, the rest is advance.
- Student who left: `POST students/{id}/rejoin` (Phase 1) is the undo.

## Offline attendance
- `POST attendance/{batchId}` takes `clientSavedAt`. A register saved offline and sent later is refused (409, `conflict: true`)
  if someone else saved that register after it, instead of overwriting the newer one.
- The app keeps unsent registers on the phone (`OfflineAttendanceService`, localStorage) and sends them when online.
  The service worker caches `batches`, `students?…`, `attendance/…`, `dashboard/teacher` and `holidays/on` (ngsw-config
  "attendance-offline", network first with 5 s timeout) so the attendance screen opens without internet. That cache is
  cleared on sign-out. Payments and staff check-in stay online only.
- **If the API address changes**, update the URLs in `ngsw-config.json` › `attendance-offline`.


---

# Student profile and search

- `GET students/{id}/attendance?month=yyyy-MM` — teachers too (own classes): session %, present/absent counts since the
  student joined or moved to this batch, every register of the month, last 10 absences.
- `GET students/{id}/fees/upcoming` (office) — fees still to be raised this session from today's fee setup, with due
  dates and amounts after discounts (sibling discount included).
- `GET students/page?status=All` (office) — students studying here and those who left, studying first. Used by the
  search bar on every page (name, parent phone, admission number).
- The profile page (`/students/{id}`) is open to teachers: details and attendance only, no fees.
- Slips (fee statement, fee due slip, no-dues certificate) are drawn in the app from `GET students/{id}/fees`.

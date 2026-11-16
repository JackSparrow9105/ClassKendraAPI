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

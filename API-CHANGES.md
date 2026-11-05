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

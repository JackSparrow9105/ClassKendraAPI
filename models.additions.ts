// Add these to the Angular app's models.ts — they match the new API responses exactly.

export interface NeedsAttentionItem {
  studentId: string; studentName: string; batchName: string; parentPhone: string;
  balance: number; oldestDueDate: string; daysLate: number;
}
// DashboardSummary.needsAttention is now NeedsAttentionItem[]

export interface PendingStudent {
  studentId: string; studentName: string; batchId: string; batchName: string; parentPhone: string;
  balance: number; duesCount: number; oldestDueDate: string; daysLate: number; remindersSent: number;
}
export interface RaiseResult { created: number; }
export interface RemindResult { sent: number; skipped: number; limitReached: boolean; }

export type PaymentMode = 'Cash' | 'UPI' | 'Cheque' | 'Bank' | 'Online';
export interface RecordPaymentInput {
  studentId: string; amount: number; mode: Exclude<PaymentMode, 'Online'>;
  paidOn?: string; note?: string; dueIds?: string[]; sendReceipt?: boolean;
}

export interface AttendanceStudent { id: string; name: string; }
export interface AttendanceSheet {
  batchId: string; date: string; marked: boolean; students: AttendanceStudent[];
  absentStudentIds: string[]; presentCount: number; savedAt: string | null; markedBy: string | null;
}
export interface SaveAttendanceInput { batchId: string; date: string; absentStudentIds: string[]; notifyParents?: boolean; }
export interface SaveAttendanceResult { sheet: AttendanceSheet; alertsSent: number; limitReached: boolean; }
export interface DayBatch { batchId: string; batchName: string; studentCount: number; marked: boolean; present: number; absent: number; }
export interface StudentAttendance { month: string; daysMarked: number; daysAbsent: number; absentDates: string[]; }

export type StaffRole = 'Staff' | 'Teacher';
export interface StaffMember { id: string; name: string; phone: string; role: 'Owner' | StaffRole; batchIds: string[]; lastLoginAt: string | null; }
export interface StaffInput { name: string; phone: string; role: StaffRole; batchIds: string[]; }

export interface MessageItem {
  id: string; studentId: string | null; recipientName: string; phone: string;
  type: 'Receipt' | 'FeeReminder' | 'Absence'; text: string;
  status: 'Queued' | 'Sent' | 'Delivered' | 'Read' | 'Failed'; error: string | null; createdAt: string;
}
export interface MessagePage { items: MessageItem[]; total: number; used: number; limit: number; }

export interface AmountByKey { key: string; label: string; amount: number; count: number; }
export interface CollectionReport {
  from: string; to: string; total: number; count: number;
  byMode: AmountByKey[]; byDay: AmountByKey[]; byBatch: AmountByKey[]; byFee: AmountByKey[]; unallocated: number;
}
export interface BatchDues { batchId: string; batchName: string; students: number; expected: number; collected: number; pending: number; }
export interface DuesReport { month: string; expected: number; collected: number; pending: number; batches: BatchDues[]; }

export interface PlanOption {
  code: string; name: string; institutionType: string; studentsLabel: string; maxStudents: number;
  monthlyPrice: number; yearlyPrice: number; messageLimit: number; isPopular: boolean;
}
export interface SubscriptionStatus {
  state: 'Trial' | 'Active' | 'Expired'; planCode: string; planName: string | null; endsOn: string | null;
  daysLeft: number; cycle: 'Monthly' | 'Yearly' | null; studentCount: number; maxStudents: number | null;
  messagesUsed: number; messageLimit: number;
}

/** Pass to Razorpay Checkout: { key: keyId, order_id: orderId, amount: amountPaise, currency, name, description, prefill: { contact: phone } } */
export interface CheckoutOrder { orderId: string; amountPaise: number; currency: string; keyId: string; name: string; description: string; phone: string | null; }
/** From Razorpay's success handler. */
export interface VerifyInput { razorpayOrderId: string; razorpayPaymentId: string; razorpaySignature: string; }

export interface PublicDue { id: string; headName: string; periodLabel: string; dueDate: string; balance: number; overdue: boolean; }
export interface PublicReceipt { receiptNo: string; amount: number; paidOn: string; mode: string; }
export interface PublicPayView {
  centreName: string; studentName: string; batchName: string; dues: PublicDue[]; total: number;
  onlinePaymentsEnabled: boolean; recentPayments: PublicReceipt[];
}
export interface PublicPaid { receiptNo: string; amount: number; paidOn: string; balanceLeft: number; }

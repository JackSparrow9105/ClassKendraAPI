namespace ClassKendraAPI.Data.Entities;

// Our additions to generated entities (separate file so "Reverse Engineer" doesn't overwrite them).
// Database/2026-11-19-advance-refunds-late-fees-siblings.sql

public partial class FeeHead
{
    /// <summary>
    /// Null = a normal fee the centre set up. Otherwise a hidden fee type ClassKendra uses for its own bills:
    /// OpeningBalance ("Previous dues"), LateFee, Charge (an extra charge on one student). Never shown in fee setup.
    /// </summary>
    public string? Kind { get; set; }
}

public partial class FeeDue
{
    /// <summary>Null = a normal bill. OpeningBalance, LateFee or Charge otherwise (copied from the fee type).</summary>
    public string? Kind { get; set; }

    /// <summary>Late fee bills: the bill it is the late fee for.</summary>
    public Guid? ParentDueId { get; set; }
}

public partial class Payment
{
    /// <summary>Payments made together (a family paying for two children, one online payment for siblings) share this.</summary>
    public Guid? GroupId { get; set; }
}

public partial class FeeSetting
{
    /// <summary>None, Once (a fixed fine) or PerDay (₹ per day, up to LateFeeMax).</summary>
    public string LateFeeType { get; set; } = "None";
    public int LateFeeAmount { get; set; }
    /// <summary>Days after the due date before a late fee starts.</summary>
    public int LateFeeAfterDays { get; set; }
    /// <summary>PerDay only: the most a late fee can reach for one bill. 0 = no limit.</summary>
    public int LateFeeMax { get; set; }

    /// <summary>None, Percent or Amount: discount for the 2nd and later child of a family (same parent phone).</summary>
    public string SiblingDiscountType { get; set; } = "None";
    public int SiblingDiscountValue { get; set; }
    /// <summary>Null = every fee; otherwise only this fee type (e.g. tuition).</summary>
    public Guid? SiblingDiscountHeadId { get; set; }
}

/// <summary>Money given back to a parent. Always comes out of the student's advance (money not used on any fee).</summary>
public class Refund
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentId { get; set; }
    /// <summary>"RF-2026-0001", numbered per session.</summary>
    public string RefundNo { get; set; } = "";
    public int Amount { get; set; }
    public string Mode { get; set; } = "";
    public DateOnly RefundedOn { get; set; }
    public string Reason { get; set; } = "";
    public Guid? ByUserId { get; set; }
    public string ByName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>A change to one bill after it was raised: part of it waived, or an extra charge added. Always with a reason.</summary>
public class FeeAdjustment
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid StudentId { get; set; }
    public Guid FeeDueId { get; set; }
    /// <summary>Waive (the bill goes down) or Charge (a new bill for an extra charge).</summary>
    public string Kind { get; set; } = "";
    public int Amount { get; set; }
    public string Reason { get; set; } = "";
    public Guid? ByUserId { get; set; }
    public string ByName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

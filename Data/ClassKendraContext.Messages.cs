using ClassKendraAPI.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClassKendraAPI.Data;

/// <summary>Mapping for the centre's WhatsApp message settings (Database/2026-11-16-message-settings.sql).</summary>
public partial class ClassKendraContext
{
    private void ConfigureMessageSettings(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(entity =>
        {
            // No HasDefaultValue here on purpose: EF always writes the value set in code (defaults are in Tenant.Messages.cs).
            entity.Property(e => e.SendReceipts);
            entity.Property(e => e.AbsentAlerts).IsRequired().HasMaxLength(10).IsUnicode(false);
        });
    }
}

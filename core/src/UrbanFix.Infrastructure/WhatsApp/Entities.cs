namespace UrbanFix.Infrastructure.WhatsApp;

/// <summary>A WhatsApp business number and the organization it serves.</summary>
public class WhatsAppChannel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    /// <summary>The Cloud API's id for the number (not the number itself); every webhook names it.</summary>
    public string PhoneNumberId { get; set; } = "";
    public string DisplayNumber { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

public static class MessageStatus
{
    public const string Received = "received";
    public const string Processed = "processed";
    public const string Ignored = "ignored";
    public const string Failed = "failed";
    public const string Sent = "sent";
    public const string Delivered = "delivered";
    public const string Read = "read";
}

/// <summary>A message in or out. For inbound ones the pair (direction, WhatsApp message id) is unique, so a replayed webhook changes nothing.</summary>
public class WhatsAppMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    public string Direction { get; set; } = "in";
    public string? WaMessageId { get; set; }
    /// <summary>The other person's number, international form without a plus sign, as WhatsApp writes it.</summary>
    public string WaId { get; set; } = "";
    public string Type { get; set; } = "text";
    public string? Body { get; set; }
    public string? MediaId { get; set; }
    public string? Raw { get; set; }
    public string Status { get; set; } = MessageStatus.Received;
    public string? Error { get; set; }
    public Guid? ComplaintId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Where one person's chat has got to, and what they have agreed to (WA-04, WA-06).</summary>
public class WhatsAppConversation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrganizationId { get; set; }
    /// <summary>International form with a plus sign, the same as the users table.</summary>
    public string Phone { get; set; } = "";
    public string State { get; set; } = "idle";
    public string StateData { get; set; } = "{}";
    public string? Language { get; set; }
    public DateTimeOffset? LastInboundAt { get; set; }
    public DateTimeOffset? LastOutboundAt { get; set; }
    public DateTimeOffset? OptedInAt { get; set; }
    public DateTimeOffset? OptedOutAt { get; set; }
    public Guid? ComplaintId { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>WhatsApp allows free-form replies only within 24 hours of the person's last message (WA-06).</summary>
    public bool InServiceWindow(DateTimeOffset now) => LastInboundAt is { } last && now - last < TimeSpan.FromHours(24);
}

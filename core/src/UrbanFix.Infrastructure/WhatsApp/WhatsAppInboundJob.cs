namespace UrbanFix.Infrastructure.WhatsApp;

/// <summary>Background work for one stored WhatsApp message. The conversation service decides what to do; this only runs it.</summary>
public sealed class WhatsAppInboundJob(WhatsAppConversationService conversation)
{
    public Task RunAsync(Guid organizationId, Guid messageId, CancellationToken ct) => conversation.ProcessAsync(organizationId, messageId, ct);
}

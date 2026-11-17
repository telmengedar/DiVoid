using System.Net;
using System.Security.Cryptography;
using System.Text;
using Backend.Errors.Exceptions;
using Backend.Models.Messages;
using Backend.Models.Users;
using Backend.Services.Messages;
using Microsoft.Data.Sqlite;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Core;
using Microsoft.Teams.Core.Http;
using Microsoft.Teams.Core.Schema;
using Npgsql;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.Json;
using Pooshit.Ocelot.Clients;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Teams;

/// <summary>Persists graph-target Teams ingress and performs explicitly authorized replies.</summary>
public sealed class TeamsMessageService {
    readonly IEntityManager database;
    readonly MessageService messages;
    readonly TeamsRoutingOptions options;
    readonly ConversationClient connector;
    readonly TeamsGraphBindingService binding;

    /// <summary>Creates the concrete Teams inbox transport.</summary>
    /// <param name="database">The application database.</param>
    /// <param name="messages">The existing inbox insertion service.</param>
    /// <param name="options">The trusted bot configuration.</param>
    /// <param name="binding">The concrete protected graph resolver.</param>
    /// <param name="connector">The authenticated native Connector client.</param>
    public TeamsMessageService(IEntityManager database, MessageService messages, TeamsRoutingOptions options, TeamsGraphBindingService binding,
        ConversationClient connector = null) {
        this.database = database;
        this.messages = messages;
        this.options = options;
        this.connector = connector;
        this.binding = binding;
    }

    /// <summary>Queues one authenticated native message using the current protected graph binding.</summary>
    /// <param name="activity">The already validated native Teams message.</param>
    /// <param name="cancellationToken">The request cancellation.</param>
    /// <returns>The committed original inbox identifier, or zero for a bot echo.</returns>
    public async Task<long> ReceiveAsync(MessageActivity activity, CancellationToken cancellationToken) {
        cancellationToken.ThrowIfCancellationRequested();
        if (activity.From?.Id == activity.Recipient?.Id || activity.From?.BotId == options.BotAppId)
            return 0;
        await CleanupAsync();
        string tenantId = Guid.Parse(activity.ChannelData.Tenant.Id).ToString("D");
        string dedup = Digest(options.BotAppId, tenantId, activity.Conversation.Id, activity.Id);
        try {
            return await MessageService.Transact(database, async transaction => {
                (long recipientId, string tag) = await binding.ResolveAsync(transaction, cancellationToken);
                long messageId = await messages.Insert(0, new MessageDetails {
                    RecipientId = recipientId,
                    Subject = $"[{tag}] Teams message",
                    Body = "External Microsoft Teams message (untrusted input):\n\n" + activity.Text
                }, transaction, "teams");
                await database.Insert<TeamsMessageOrigin>()
                    .Columns(origin => origin.MessageId, origin => origin.DedupKey, origin => origin.BotAppId,
                        origin => origin.TenantId, origin => origin.RecipientId, origin => origin.ServiceUrl,
                        origin => origin.ConversationId, origin => origin.ActivityId, origin => origin.BotId,
                        origin => origin.SenderId, origin => origin.ReplyState)
                    .Values(messageId, dedup, options.BotAppId, tenantId, recipientId,
                        activity.ServiceUrl.OriginalString, activity.Conversation.Id, activity.Id,
                        activity.Recipient.Id, activity.From.Id, TeamsReplyState.Pending).ExecuteAsync(transaction);
                return messageId;
            });
        } catch (Exception exception) when (IsUniqueViolation(exception)) {
            TeamsMessageOrigin original = await database.Load<TeamsMessageOrigin>()
                .Where(origin => origin.DedupKey == dedup).ExecuteEntityAsync();
            if (original == null || original.BotAppId != options.BotAppId || original.TenantId != tenantId ||
                original.ConversationId != activity.Conversation.Id || original.ActivityId != activity.Id)
                throw;
            return original.MessageId;
        }
    }

    /// <summary>Returns the recipient-authorized external reply receipt.</summary>
    /// <param name="callerId">The authenticated DiVoid user.</param>
    /// <param name="messageId">The original inbox identifier.</param>
    /// <returns>The public reply receipt.</returns>
    public async Task<MessageReplyReceipt> GetAsync(long callerId, long messageId) => Receipt(await AuthorizedOrigin(callerId, messageId));

    /// <summary>Attempts one explicit final reply to the original Teams conversation.</summary>
    /// <param name="callerId">The authenticated DiVoid recipient.</param>
    /// <param name="messageId">The original inbox identifier.</param>
    /// <param name="body">The explicit response text.</param>
    /// <param name="cancellationToken">The requesting caller cancellation.</param>
    /// <returns>The Connector-confirmed public receipt.</returns>
    public async Task<MessageReplyReceipt> ReplyAsync(long callerId, long messageId, string body, CancellationToken cancellationToken) {
        TeamsMessageOrigin origin = await AuthorizedOrigin(callerId, messageId);
        if (string.IsNullOrWhiteSpace(body) || new UTF8Encoding(false, true).GetByteCount(body) > 16 * 1024)
            throw new ArgumentException("A reply must contain nonblank text of at most 16 KiB UTF-8.", nameof(body));
        string hash = Digest(body);
        if (origin.ReplyState == TeamsReplyState.Sent) {
            if (origin.ReplyBodyHash != hash)
                throw new MessageReplyException(HttpStatusCode.Conflict, "reply_conflict", "A different final reply has already been accepted.");
            return Receipt(origin);
        }
        if (origin.ReplyState is TeamsReplyState.Sending or TeamsReplyState.Unknown)
            throw new MessageReplyException(HttpStatusCode.Conflict, "reply_outcome_unknown",
                "A reply is in progress or its external outcome is unknown. Inspect reply status; do not resend.");
        cancellationToken.ThrowIfCancellationRequested();
        long? started = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        TeamsReplyState sending = TeamsReplyState.Sending;
        long claimed = await database.Update<TeamsMessageOrigin>()
            .Set(route => route.ReplyState == sending, route => route.ReplyBodyHash == hash,
                route => route.ReplyAttemptStartedAt == started, route => route.ErrorCategory == null)
            .Where(route => route.MessageId == messageId && route.RecipientId == callerId &&
                (route.ReplyState == TeamsReplyState.Pending || route.ReplyState == TeamsReplyState.Failed)).ExecuteAsync();
        if (claimed == 0)
            throw new MessageReplyException(HttpStatusCode.Conflict, "reply_in_progress", "Another reply attempt owns this message. Inspect reply status.");
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        SendActivityResponse sent;
        try {
            MessageActivityInput reply = new() {
                Text = body,
                ReplyToId = origin.ActivityId,
                Recipient = new ChannelAccount { Id = origin.SenderId }
            };
            sent = await connector.SendActivityAsync(origin.ConversationId, reply, new Uri(origin.ServiceUrl),
                requestContext: new BotRequestContext { BotAppId = options.BotAppId }, cancellationToken: timeout.Token);
        } catch (Exception exception) {
            bool definitive = exception is ArgumentException ||
                exception is HttpRequestException { StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError };
            await SetState(messageId, definitive ? TeamsReplyState.Failed : TeamsReplyState.Unknown,
                definitive ? "connector_rejected" : "outcome_unknown");
            throw new MessageReplyException(HttpStatusCode.BadGateway,
                definitive ? "reply_failed" : "reply_outcome_unknown",
                definitive ? "The Connector definitively rejected the reply; an explicit retry is permitted." :
                    "The external reply outcome is unknown. Inspect reply status and reconcile externally; do not resend.");
        }
        if (string.IsNullOrWhiteSpace(sent?.Id)) {
            await SetState(messageId, TeamsReplyState.Unknown, "outcome_unknown");
            throw new MessageReplyException(HttpStatusCode.BadGateway, "reply_outcome_unknown", "Connector confirmation is incomplete; do not resend.");
        }
        try {
            TeamsReplyState accepted = TeamsReplyState.Sent;
            await database.Update<TeamsMessageOrigin>()
                .Set(route => route.ReplyState == accepted, route => route.OutboundActivityId == sent.Id,
                    route => route.ErrorCategory == null).Where(route => route.MessageId == messageId).ExecuteAsync();
        } catch {
            throw new MessageReplyException(HttpStatusCode.BadGateway, "reply_outcome_unknown",
                "The Connector accepted the reply but its local receipt could not be confirmed. Do not resend.");
        }
        origin.ReplyState = TeamsReplyState.Sent;
        origin.OutboundActivityId = sent.Id;
        return Receipt(origin);
    }

    async Task<TeamsMessageOrigin> AuthorizedOrigin(long callerId, long messageId) {
        if (!options.Enabled || callerId <= 0)
            throw new NotFoundException<Message>(messageId);
        await CleanupAsync();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        TeamsMessageOrigin origin = await database.Load<TeamsMessageOrigin>()
            .Where(route => route.MessageId == messageId && route.RecipientId == callerId &&
                (route.ExpiresAt == null || route.ExpiresAt > now)).ExecuteEntityAsync();
        User recipient = origin == null ? null : await database.Load<User>()
            .Where(user => user.Id == callerId && user.Enabled).ExecuteEntityAsync();
        if (origin == null || recipient == null || !options.Enabled ||
            origin.BotAppId != options.BotAppId ||
            !Guid.TryParse(origin.TenantId, out Guid tenant) || tenant == Guid.Empty ||
            (options.AllowedTenantIds.Count > 0 && !options.AllowedTenantIds.Contains(tenant)) ||
            string.IsNullOrWhiteSpace(origin.ConversationId) || string.IsNullOrWhiteSpace(origin.ActivityId) ||
            string.IsNullOrWhiteSpace(origin.BotId) || string.IsNullOrWhiteSpace(origin.SenderId) ||
            !Uri.TryCreate(origin.ServiceUrl, UriKind.Absolute, out Uri url) || url.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(url.UserInfo) || !string.IsNullOrEmpty(url.Query) || !string.IsNullOrEmpty(url.Fragment))
            throw new NotFoundException<Message>(messageId);
        return origin;
    }

    Task<long> SetState(long messageId, TeamsReplyState state, string category) => database.Update<TeamsMessageOrigin>()
        .Set(origin => origin.ReplyState == state, origin => origin.ErrorCategory == category)
        .Where(origin => origin.MessageId == messageId).ExecuteAsync();

    async Task CleanupAsync() {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        List<long> ids = new();
        await foreach (TeamsMessageOrigin origin in database.Load<TeamsMessageOrigin>()
            .Where(route => route.ExpiresAt != null && route.ExpiresAt <= now).Limit(64).ExecuteEntitiesAsync())
            ids.Add(origin.MessageId);
        if (ids.Count > 0)
            await database.Delete<TeamsMessageOrigin>().Where(origin => origin.MessageId.In(ids.ToArray())).ExecuteAsync();
    }

    static MessageReplyReceipt Receipt(TeamsMessageOrigin origin) => new() {
        MessageId = origin.MessageId,
        Transport = "teams",
        State = origin.ReplyState == TeamsReplyState.Sending && origin.ReplyAttemptStartedAt <= DateTimeOffset.UtcNow.AddSeconds(-15).ToUnixTimeSeconds()
            ? "unknown" : origin.ReplyState.ToString().ToLowerInvariant(),
        OutboundActivityId = origin.OutboundActivityId
    };

    static string Digest(params string[] values) {
        using MemoryStream bytes = new();
        using (BinaryWriter writer = new(bytes, Encoding.UTF8, true))
            foreach (string value in values)
                writer.Write(value);
        return Convert.ToHexString(SHA256.HashData(bytes.ToArray()));
    }

    static bool IsUniqueViolation(Exception exception) {
        for (Exception current = exception; current != null; current = current.InnerException)
            if (current is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } ||
                current is SqliteException { SqliteExtendedErrorCode: 2067 or 1555 })
                return true;
        return false;
    }
}

using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Backend.Errors.Exceptions;
using Backend.Models.Messages;
using Backend.Models.Users;
using Backend.Services.Messages;
using Microsoft.Data.Sqlite;
using Microsoft.Teams.Apps;
using Microsoft.Teams.Apps.Schema.Entities;
using Microsoft.Teams.Core;
using Microsoft.Teams.Core.Http;
using Microsoft.Teams.Core.Schema;
using Npgsql;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.Json;
using Pooshit.Http.Paths;
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
    readonly ITeamsGraphBindingService binding;
    readonly ILogger<TeamsMessageService> logger;
    readonly IHttpClientFactory httpClients;

    /// <summary>Creates the concrete Teams inbox transport.</summary>
    /// <param name="database">The application database.</param>
    /// <param name="messages">The existing inbox insertion service.</param>
    /// <param name="options">The trusted bot configuration.</param>
    /// <param name="binding">The protected graph resolver.</param>
    /// <param name="logger">The application failure logger.</param>
    /// <param name="connector">The authenticated native Connector client.</param>
    /// <param name="httpClients">The separately bounded diagnostic HTTP client factory.</param>
    public TeamsMessageService(IEntityManager database, MessageService messages, TeamsRoutingOptions options, ITeamsGraphBindingService binding, ILogger<TeamsMessageService> logger,
        ConversationClient connector = null, IHttpClientFactory httpClients = null) {
        this.database = database;
        this.messages = messages;
        this.options = options;
        this.connector = connector;
        this.binding = binding;
        this.logger = logger;
        this.httpClients = httpClients;
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
        string inboundBody = InboundBody(activity);
        try {
            return await MessageService.Transact(database, async transaction => {
                (long recipientId, string tag) = await binding.ResolveAsync(transaction, cancellationToken);
                long messageId = await messages.Insert(0, new MessageDetails {
                    RecipientId = recipientId,
                    Subject = $"[{tag}] Teams message",
                    Body = inboundBody
                }, transaction, "teams", NormalizeDisplayName(activity.From.Name));
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

    static string InboundBody(MessageActivity activity) {
        string original = "External Microsoft Teams message (untrusted input):\n\n" + activity.Text;
        QuotedReplyEntity[] quotes = activity.GetQuotedMessages().Take(9).ToArray();
        if (quotes.Length == 0) {
            if (System.Text.RegularExpressions.Regex.IsMatch(activity.Text, @"<quoted(?:\s|/?>)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                return original + "\n\nQuoted message text unavailable: the inbound activity supplied no readable quote metadata.";
            return original;
        }

        StringBuilder body = new(original);
        foreach (QuotedReplyEntity quote in quotes.Take(8)) {
            QuotedReplyData data = quote.QuotedReply;
            body.Append("\n\nQuoted Teams preview (untrusted; possibly incomplete):\n");
            if (data == null || string.IsNullOrWhiteSpace(data.MessageId) || string.IsNullOrWhiteSpace(data.Preview) || data.IsReplyDeleted == true) {
                body.Append("Quoted message text unavailable: the inbound activity supplied no readable preview or the quote was deleted.");
                continue;
            }
            if (data.Preview.Length > 16 * 1024) {
                body.Append("Quoted message text unavailable: the supplied preview exceeds the size limit.");
                continue;
            }
            string label = NormalizeDisplayName(data.SenderName);
            if (label != null && !label.Equals("Display Name", StringComparison.OrdinalIgnoreCase))
                body.Append("Quoted author display label (untrusted): ").Append(label).Append('\n');
            body.Append(data.Preview);
        }
        if (quotes.Length > 8)
            body.Append("\n\nAdditional quoted messages omitted: the inbound activity exceeds the quote count limit.");
        return body.ToString();
    }

    static string NormalizeDisplayName(string label) {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        for (int index = 0; index < label.Length; ++index) {
            char character = label[index];
            if (char.IsControl(character) || character is '\u2028' or '\u2029')
                return null;
            if (char.IsSurrogate(character)) {
                if (!char.IsSurrogatePair(label, index))
                    return null;
                ++index;
            }
        }

        string trimmed = label.Trim();
        return trimmed.Length <= 256 ? trimmed : null;
    }

    /// <summary>Returns the recipient-authorized external reply receipt.</summary>
    /// <param name="callerId">The authenticated DiVoid user.</param>
    /// <param name="messageId">The original inbox identifier.</param>
    /// <returns>The public reply receipt.</returns>
    public async Task<MessageReplyReceipt> GetAsync(long callerId, long messageId) => Receipt(await AuthorizedOrigin(callerId, messageId));

    /// <summary>Reads the original Connector conversation without changing its reply state.</summary>
    /// <param name="callerId">The authenticated original DiVoid recipient.</param>
    /// <param name="messageId">The original inbox identifier.</param>
    /// <param name="cancellationToken">The requesting caller cancellation.</param>
    /// <returns>The safe native Connector read outcome without member data.</returns>
    public async Task<MessageReplyDiagnosis> DiagnoseAsync(long callerId, long messageId, CancellationToken cancellationToken) {
        TeamsMessageOrigin origin = await AuthorizedOrigin(callerId, messageId);
        cancellationToken.ThrowIfCancellationRequested();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using HttpClient http = httpClients.CreateClient("TeamsConnectorDiagnosis");
        MessageReplyDiagnosis result = new();
        Uri route = new(Rest.Path(new Uri(origin.ServiceUrl).ToString().TrimEnd('/'), "v3", "conversations",
            Uri.EscapeDataString(origin.ConversationId), "members"));
        using HttpClient observed = new(new TeamsConnectorDiagnosisHandler(http, route, options.BotAppId, options.CredentialTenant, result));
        ConversationClient diagnostic = new(observed);
        try {
            await diagnostic.GetConversationMembersAsync(origin.ConversationId, new Uri(origin.ServiceUrl),
                requestContext: new BotRequestContext { BotAppId = options.BotAppId }, cancellationToken: timeout.Token);
            result.Completed = true;
            result.ProviderCode = "absent";
            result.Reason = "connector_read_completed";
            return result;
        } catch (Exception exception) {
            LogFailure(messageId, "diagnose", exception);
            return DiagnoseFailure(exception, cancellationToken.IsCancellationRequested, result);
        }
    }

    static MessageReplyDiagnosis DiagnoseFailure(Exception exception, bool callerCancelled, MessageReplyDiagnosis result) {
        Exception[] causes = FailureCauses(exception).ToArray();
        HttpRequestException response = causes.OfType<HttpRequestException>().FirstOrDefault(cause => cause.StatusCode.HasValue);
        result.HttpStatus = response?.StatusCode is HttpStatusCode status ? (int)status : null;
        result.ProviderCode = "absent";
        result.Reason = causes.Any(cause => cause is OperationCanceledException) ? callerCancelled ? "cancelled" : "timeout" :
            response == null ? "request_failed" : "connector_rejected";
        if (response == null || !QuotesInput(response))
            return result;
        string[] codes = ["Unauthorized", "Forbidden", "InvalidToken", "InvalidAuthenticationToken", "AuthenticationFailed",
            "AuthorizationFailed", "BotNotInConversationRoster", "BotNotInConversation", "BotNotRegistered", "InvalidBotId",
            "InvalidJwtToken", "InvalidAudience", "InvalidIssuer", "TokenExpired", "BadArgument", "ServiceError"];
        int start = response.Message.IndexOf('{');
        if (start >= 0) {
            try {
                Dictionary<string, object> fields = Json.Read<Dictionary<string, object>>(response.Message[start..]);
                if (fields.TryGetValue("error", out object error) && error is Dictionary<string, object> nested)
                    fields = nested;
                if (fields.TryGetValue("code", out object raw) && raw is string code)
                    result.ProviderCode = codes.FirstOrDefault(allowed => allowed.Equals(code, StringComparison.OrdinalIgnoreCase)) ?? "unlisted_code";
            } catch (Exception) {
                result.ProviderCode = "unreadable_code";
            }
        }
        foreach ((string text, string category) in new[] {
            ("Authorization has been denied for this request", "authorization_denied_generic"),
            ("Invalid audience", "invalid_audience"), ("Invalid issuer", "invalid_issuer"),
            ("signature validation failed", "signature_validation_failed"), ("token has expired", "token_expired"),
            ("bot is not part of the conversation roster", "bot_not_in_roster"),
            ("Authorization header is missing", "authorization_header_missing"),
            ("Missing Authorization header", "authorization_header_missing"),
            ("Invalid Bot AppId", "invalid_bot_app_id"), ("bot is not registered", "bot_not_registered"),
            ("Invalid JWT token", "invalid_jwt_token") })
            if (response.Message.Contains(text, StringComparison.OrdinalIgnoreCase)) {
                result.Reason = category;
                break;
            }
        return result;
    }

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
        string outbound = await SendReplyAsync(origin, body, cancellationToken, (state, category, activityId) =>
            database.Update<TeamsMessageOrigin>()
                .Set(route => route.ReplyState == state, route => route.ErrorCategory == category,
                    route => route.OutboundActivityId == activityId)
                .Where(route => route.MessageId == messageId).ExecuteAsync());
        origin.ReplyState = TeamsReplyState.Sent;
        origin.OutboundActivityId = outbound;
        return Receipt(origin);
    }

    /// <summary>Reads one existing logical reply without creating or resetting it.</summary>
    /// <param name="callerId">The authenticated original recipient.</param>
    /// <param name="messageId">The retained inbox identifier.</param>
    /// <param name="replyId">The saved logical-message UUID.</param>
    /// <returns>The safe receipt for this UUID.</returns>
    public async Task<MessageReplyDetails> GetReplyAsync(long callerId, long messageId, Guid replyId) {
        await AuthorizedOrigin(callerId, messageId);
        string key = ReplyKey(replyId);
        TeamsMessageReply reply = await database.Load<TeamsMessageReply>()
            .Where(row => row.MessageId == messageId && row.ReplyId == key).ExecuteEntityAsync();
        if (reply == null)
            throw new NotFoundException<Message>(messageId);
        return Receipt(reply);
    }

    /// <summary>Claims and attempts one independently idempotent logical response.</summary>
    /// <param name="callerId">The authenticated original recipient.</param>
    /// <param name="messageId">The retained inbox identifier.</param>
    /// <param name="replyId">The UUID persisted by the caller before sending.</param>
    /// <param name="body">The immutable exact text for this UUID.</param>
    /// <param name="cancellationToken">The requesting caller cancellation.</param>
    /// <returns>The confirmed or already accepted receipt.</returns>
    public async Task<MessageReplyDetails> CreateReplyAsync(long callerId, long messageId, Guid replyId, string body, CancellationToken cancellationToken) {
        await AuthorizedOrigin(callerId, messageId);
        string key = ReplyKey(replyId);
        if (string.IsNullOrWhiteSpace(body) || new UTF8Encoding(false, true).GetByteCount(body) > 16 * 1024)
            throw new ArgumentException("A reply must contain nonblank text of at most 16 KiB UTF-8.", nameof(body));
        string hash = Digest(body);
        cancellationToken.ThrowIfCancellationRequested();
        (TeamsMessageOrigin origin, TeamsMessageReply reply, bool send) claim;
        try {
            claim = await ClaimReplyAsync(callerId, messageId, key, hash, true);
        } catch (Exception exception) when (IsUniqueViolation(exception)) {
            claim = await ClaimReplyAsync(callerId, messageId, key, hash, false);
        }
        if (!claim.send)
            return Receipt(claim.reply);
        long? started = claim.reply.AttemptStartedAt;
        string outbound = await SendReplyAsync(claim.origin, body, cancellationToken, (state, category, activityId) =>
            database.Update<TeamsMessageReply>()
                .Set(row => row.State == state, row => row.ErrorCategory == category, row => row.OutboundActivityId == activityId)
                .Where(row => row.MessageId == messageId && row.ReplyId == key && row.BodyHash == hash &&
                    row.State == TeamsReplyState.Sending && row.AttemptStartedAt == started).ExecuteAsync(), key);
        claim.reply.State = TeamsReplyState.Sent;
        claim.reply.OutboundActivityId = outbound;
        return Receipt(claim.reply);
    }

    Task<(TeamsMessageOrigin origin, TeamsMessageReply reply, bool send)> ClaimReplyAsync(long callerId, long messageId, string key, string hash, bool allowInsert) =>
        MessageService.Transact(database, async transaction => {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long locked = await database.Update<TeamsMessageOrigin>().Set(origin => origin.RecipientId == callerId)
                .Where(origin => origin.MessageId == messageId && origin.RecipientId == callerId &&
                    (origin.ExpiresAt == null || origin.ExpiresAt > now)).ExecuteAsync(transaction);
            if (locked == 0)
                throw new NotFoundException<Message>(messageId);
            TeamsMessageOrigin origin = await AuthorizedOrigin(callerId, messageId, transaction);
            TeamsMessageReply reply = await database.Load<TeamsMessageReply>()
                .Where(row => row.MessageId == messageId && row.ReplyId == key).ExecuteEntityAsync(transaction);
            if (reply == null) {
                if (!allowInsert)
                    throw new MessageReplyException(HttpStatusCode.Conflict, "reply_in_progress", "The logical reply claim could not be confirmed. Inspect its receipt.");
                reply = new TeamsMessageReply { MessageId = messageId, ReplyId = key, BodyHash = hash,
                    State = TeamsReplyState.Sending, AttemptStartedAt = now };
                await database.Insert<TeamsMessageReply>()
                    .Columns(row => row.MessageId, row => row.ReplyId, row => row.BodyHash, row => row.State, row => row.AttemptStartedAt)
                    .Values(messageId, key, hash, reply.State, reply.AttemptStartedAt).ExecuteAsync(transaction);
                return (origin, reply, true);
            }
            if (reply.BodyHash != hash)
                throw new MessageReplyException(HttpStatusCode.Conflict, "reply_conflict", "This reply UUID already identifies different text.");
            if (reply.State == TeamsReplyState.Sent)
                return (origin, reply, false);
            if (reply.State != TeamsReplyState.Failed)
                throw new MessageReplyException(HttpStatusCode.Conflict, "reply_outcome_unknown", "This reply is in progress or uncertain. Inspect its receipt; do not resend.");
            long? previous = reply.AttemptStartedAt;
            long? started = now;
            long claimed = await database.Update<TeamsMessageReply>()
                .Set(row => row.State == TeamsReplyState.Sending, row => row.AttemptStartedAt == started, row => row.ErrorCategory == null)
                .Where(row => row.MessageId == messageId && row.ReplyId == key && row.BodyHash == hash &&
                    row.State == TeamsReplyState.Failed && row.AttemptStartedAt == previous).ExecuteAsync(transaction);
            if (claimed != 1)
                throw new MessageReplyException(HttpStatusCode.Conflict, "reply_in_progress", "Another attempt owns this logical reply. Inspect its receipt.");
            reply.State = TeamsReplyState.Sending;
            reply.AttemptStartedAt = started;
            return (origin, reply, true);
        });

    static string ReplyKey(Guid replyId) => replyId == Guid.Empty
        ? throw new ArgumentException("replyId must be a nonzero UUID.", nameof(replyId)) : replyId.ToString("D");

    async Task<string> SendReplyAsync(TeamsMessageOrigin origin, string body, CancellationToken cancellationToken,
        Func<TeamsReplyState, string, string, Task<long>> persist, string replyId = null) {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        SendActivityResponse sent;
        try {
            MessageActivityInput reply = new() {
                Text = body,
                ReplyToId = origin.ActivityId,
                Recipient = new ChannelAccount { Id = origin.SenderId }
            };
            reply.Properties["from"] = new ChannelAccount { Id = origin.BotId };
            sent = await connector.SendActivityAsync(origin.ConversationId, reply, new Uri(origin.ServiceUrl),
                requestContext: new BotRequestContext { BotAppId = options.BotAppId }, cancellationToken: timeout.Token);
        } catch (Exception exception) {
            LogFailure(origin.MessageId, "send", exception, replyId);
            bool definitive = exception is ArgumentException ||
                exception is HttpRequestException { StatusCode: >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError };
            await persist(definitive ? TeamsReplyState.Failed : TeamsReplyState.Unknown,
                definitive ? "connector_rejected" : "outcome_unknown", null);
            throw new MessageReplyException(HttpStatusCode.BadGateway,
                definitive ? "reply_failed" : "reply_outcome_unknown",
                definitive ? "The Connector definitively rejected the reply; an explicit retry is permitted." :
                    "The external reply outcome is unknown. Inspect reply status and reconcile externally; do not resend.");
        }
        if (string.IsNullOrWhiteSpace(sent?.Id)) {
            await persist(TeamsReplyState.Unknown, "outcome_unknown", null);
            throw new MessageReplyException(HttpStatusCode.BadGateway, "reply_outcome_unknown", "Connector confirmation is incomplete; do not resend.");
        }
        try {
            if (await persist(TeamsReplyState.Sent, null, sent.Id) != 1)
                throw new InvalidOperationException("The retained reply claim is no longer available.");
        } catch (Exception exception) {
            LogFailure(origin.MessageId, "receipt_persist", exception, replyId);
            throw new MessageReplyException(HttpStatusCode.BadGateway, "reply_outcome_unknown",
                "The Connector accepted the reply but its local receipt could not be confirmed. Do not resend.");
        }
        return sent.Id;
    }

    void LogFailure(long messageId, string stage, Exception exception, string replyId = null) {
        Exception[] causes = FailureCauses(exception).ToArray();
        int? status = causes.OfType<HttpRequestException>().Where(cause => cause.StatusCode.HasValue)
            .Select(cause => (int?)cause.StatusCode.Value).FirstOrDefault();
        if (causes.Any(QuotesInput)) {
            string details = string.Join("\n", causes.Select(MessageFreeStack));
            logger.LogError("Teams reply failure messageId={MessageId} replyId={ReplyId} stage={Stage} httpStatus={HttpStatus} exceptions={ExceptionDetails}",
                messageId, replyId, stage, status, details);
        } else
            logger.LogError(exception, "Teams reply failure messageId={MessageId} replyId={ReplyId} stage={Stage} httpStatus={HttpStatus}",
                messageId, replyId, stage, status);
    }

    static bool QuotesInput(Exception exception) {
        if (exception is FormatException)
            return true;
        for (Type type = exception.GetType(); type != null; type = type.BaseType)
            if (type.Name == "ScriptRuntimeException" && type.Namespace?.StartsWith("Pooshit.Scripting", StringComparison.Ordinal) == true)
                return true;
        Type origin = exception.TargetSite?.DeclaringType;
        return exception is HttpRequestException && origin?.DeclaringType == typeof(BotHttpClient) &&
            origin.Name.StartsWith("<HandleResponseAsync>", StringComparison.Ordinal);
    }

    static IEnumerable<Exception> FailureCauses(Exception exception) {
        yield return exception;
        IEnumerable<Exception> inner = exception is AggregateException aggregate ? aggregate.InnerExceptions :
            exception.InnerException == null ? [] : [exception.InnerException];
        foreach (Exception cause in inner)
            foreach (Exception descendant in FailureCauses(cause))
                yield return descendant;
    }

    static string MessageFreeStack(Exception exception) {
        StringBuilder details = new(exception.GetType().FullName);
        foreach (StackFrame frame in new StackTrace(exception, true).GetFrames() ?? []) {
            System.Reflection.MethodBase method = frame.GetMethod();
            if (method == null)
                continue;
            details.Append("\n at ").Append(method.DeclaringType?.FullName).Append('.').Append(method.Name);
            string file = frame.GetFileName();
            if (!string.IsNullOrEmpty(file)) {
                string basename = Path.GetFileName(file.Replace('\\', '/').Split('?', '#')[0]);
                details.Append(" in ").Append(basename).Append(':').Append(frame.GetFileLineNumber());
            }
        }
        return details.ToString();
    }

    async Task<TeamsMessageOrigin> AuthorizedOrigin(long callerId, long messageId, Transaction transaction = null) {
        if (!options.Enabled || callerId <= 0)
            throw new NotFoundException<Message>(messageId);
        if (transaction == null)
            await CleanupAsync();
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        TeamsMessageOrigin origin = await database.Load<TeamsMessageOrigin>()
            .Where(route => route.MessageId == messageId && route.RecipientId == callerId &&
                (route.ExpiresAt == null || route.ExpiresAt > now)).ExecuteEntityAsync(transaction);
        User recipient = origin == null ? null : await database.Load<User>()
            .Where(user => user.Id == callerId && user.Enabled).ExecuteEntityAsync(transaction);
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
        ids.Sort();
        if (ids.Count > 0)
            await MessageService.Transact(database, async transaction => {
                foreach (long id in ids) {
                    long removed = await database.Delete<TeamsMessageOrigin>()
                        .Where(origin => origin.MessageId == id && origin.ExpiresAt != null && origin.ExpiresAt <= now).ExecuteAsync(transaction);
                    if (removed != 0)
                        await database.Delete<TeamsMessageReply>().Where(reply => reply.MessageId == id).ExecuteAsync(transaction);
                }
                return true;
            });
    }

    static MessageReplyDetails Receipt(TeamsMessageReply reply) => new() {
        MessageId = reply.MessageId,
        ReplyId = Guid.Parse(reply.ReplyId),
        Transport = "teams",
        State = reply.State == TeamsReplyState.Sending && reply.AttemptStartedAt <= DateTimeOffset.UtcNow.AddSeconds(-15).ToUnixTimeSeconds()
            ? "unknown" : reply.State.ToString().ToLowerInvariant(),
        OutboundActivityId = reply.OutboundActivityId
    };

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

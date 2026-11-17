using System;
using System.Threading;
using System.Threading.Tasks;
using Npgsql;
using Pooshit.Ocelot.Clients;
using Backend.Extensions;
using Backend.Errors.Exceptions;
using Backend.Models.Messages;
using Backend.Models.Users;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Data;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
using Pooshit.Ocelot.Entities.Operations.Prepared;
using Pooshit.Ocelot.Expressions;
using Pooshit.Ocelot.Fields;
using Pooshit.Ocelot.Tokens;

namespace Backend.Services.Messages;

/// <inheritdoc />
public class MessageService : IMessageService {
    readonly IEntityManager database;

    /// <summary>
    /// creates a new <see cref="MessageService"/>
    /// </summary>
    /// <param name="database">access to database</param>
    public MessageService(IEntityManager database) {
        this.database = database;
    }


    /// <inheritdoc />
    public async Task<MessageDetails> Create(long callerId, MessageDetails details) {
        long id = await Insert(callerId, details);
        return await GetById(callerId, isAdmin: true, id);
    }

    internal async Task<long> Insert(long callerId, MessageDetails details, Transaction transaction = null, string replyTransport = null) {
        if (details.RecipientId <= 0)
            throw new ArgumentException("RecipientId must be a valid user id", nameof(details));

        string subject = details.Subject?.Trim() ?? "";
        if (string.IsNullOrEmpty(subject))
            throw new ArgumentException("Subject must not be empty", nameof(details));
        if (subject.Length > 256)
            throw new ArgumentException("Subject must not exceed 256 characters", nameof(details));
        if (string.IsNullOrEmpty(details.Body))
            throw new ArgumentException("Body must not be empty", nameof(details));

        long recipientExists = await database.Load<User>(DB.Count())
                                              .Where(u => u.Id == details.RecipientId && u.Enabled)
                                              .ExecuteScalarAsync<long>(transaction);
        if (recipientExists == 0)
            throw new NotFoundException<User>(details.RecipientId);

        DateTime now = DateTime.UtcNow;
        long id = await database.Insert<Message>()
                                .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt, m => m.ReplyTransport)
                                .Values(callerId, details.RecipientId, subject, details.Body, now, replyTransport)
                                .ReturnID()
                                .ExecuteAsync(transaction);

        return id;
    }


    /// <inheritdoc />
    public async Task<MessageDetails> GetById(long callerId, bool isAdmin, long id) {
        MessageMapper mapper = new();
        long scopedCallerId = callerId;
        LoadOperation<Message> operation = isAdmin
            ? mapper.CreateOperation(database).Where(m => m.Id == id)
            : mapper.CreateOperation(database).Where(m => m.Id == id && (m.AuthorId == scopedCallerId || m.RecipientId == scopedCallerId));

        MessageDetails message = await mapper.EntityFromOperation(operation);

        if (message == null)
            throw new NotFoundException<Message>(id);

        return message;
    }


    /// <inheritdoc />
    public async Task<AsyncPageResponseWriter<MessageDetails>> ListPaged(long callerId, bool isAdmin, MessageFilter filter = null, CancellationToken ct = default) {
        filter ??= new();

        MessageMapper mapper = new();
        PredicateExpression<Message> predicate = BuildPredicate(callerId, isAdmin, filter);

        LoadOperation<Message> operation = mapper.CreateOperation(database, filter.Fields);

        if (string.IsNullOrEmpty(filter.Sort)) {
            operation.OrderBy(new OrderByCriteria(mapper["createdat"].Field, ascending: false));
        }

        operation.ApplyFilter(filter, mapper);
        operation.Where(predicate?.Content);

        WindowResult<MessageDetails, long> windowed =
            await mapper.WindowedFromOperation<long, Message>(operation, DB.CountOver(), ct, filter.Fields);

        return new AsyncPageResponseWriter<MessageDetails>(
            windowed.Items,
            () => windowed.WindowValue,
            filter.Continue
        );
    }


    PredicateExpression<Message> BuildPredicate(long callerId, bool isAdmin, MessageFilter filter) {
        PredicateExpression<Message> predicate = null;

        bool explicitScope = filter.RecipientId?.Length > 0 || filter.AuthorId?.Length > 0;
        if (explicitScope) {
            if (filter.RecipientId?.Length > 0) {
                long[] recipientIds = filter.RecipientId;
                predicate &= new PredicateExpression<Message>(m => m.RecipientId.In(recipientIds));
            }
            if (filter.AuthorId?.Length > 0) {
                long[] authorIds = filter.AuthorId;
                predicate &= new PredicateExpression<Message>(m => m.AuthorId.In(authorIds));
            }
        } else {
            long scopeId = callerId;
            predicate &= filter.IncludeAuthored
                ? new PredicateExpression<Message>(m => m.RecipientId == scopeId || m.AuthorId == scopeId)
                : new PredicateExpression<Message>(m => m.RecipientId == scopeId);
        }

        if (!isAdmin) {
            long scopeId = callerId;
            predicate &= new PredicateExpression<Message>(m => m.AuthorId == scopeId || m.RecipientId == scopeId);
        }

        return predicate;
    }


    /// <inheritdoc />
    public async Task Delete(long callerId, bool isAdmin, long id) {
        await Transact(database, async transaction => {
            DeleteOperation<Message> operation = database.Delete<Message>();
            if (isAdmin)
                operation.Where(message => message.Id == id);
            else
                operation.Where(message => message.Id == id && message.RecipientId == callerId);
            if (await operation.ExecuteAsync(transaction) == 0)
                throw new NotFoundException<Message>(id);
            long? consumed = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            long? expires = consumed + 30 * 24 * 60 * 60;
            await database.Update<TeamsMessageOrigin>()
                .Set(origin => origin.ConsumedAt == consumed, origin => origin.ExpiresAt == expires)
                .Where(origin => origin.MessageId == id).ExecuteAsync(transaction);
            return true;
        });
    }

    internal static async Task<T> Transact<T>(IEntityManager database, Func<Transaction, Task<T>> action) {
        Transaction transaction = database.Transaction();
        NpgsqlConnection connection = transaction.DbTransaction.Connection as NpgsqlConnection;
        Exception failure = null;
        try {
            T result = await action(transaction);
            transaction.Commit();
            return result;
        } catch (Exception exception) {
            failure = exception;
            throw;
        } finally {
            try {
                transaction.Dispose();
            } catch (InvalidOperationException) when (connection != null && failure != null) {
                transaction.DbTransaction.Dispose();
                connection.Dispose();
            }
        }
    }
}

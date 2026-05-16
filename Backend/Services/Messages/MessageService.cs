using System;
using System.Threading.Tasks;
using Backend.Extensions;
using Backend.Errors.Exceptions;
using Backend.Models.Messages;
using Backend.Models.Users;
using Pooshit.AspNetCore.Services.Errors.Exceptions;
using Pooshit.AspNetCore.Services.Formatters.DataStream;
using Pooshit.Ocelot.Entities;
using Pooshit.Ocelot.Entities.Operations;
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
        if (details.RecipientId <= 0)
            throw new ArgumentException("RecipientId must be a valid user id", nameof(details));

        string subject = details.Subject?.Trim() ?? "";
        if (string.IsNullOrEmpty(subject))
            throw new ArgumentException("Subject must not be empty", nameof(details));
        if (subject.Length > 256)
            throw new ArgumentException("Subject must not exceed 256 characters", nameof(details));
        if (string.IsNullOrEmpty(details.Body))
            throw new ArgumentException("Body must not be empty", nameof(details));

        User recipient = await database.Load<User>()
                                       .Where(u => u.Id == details.RecipientId)
                                       .ExecuteEntityAsync();
        if (recipient == null)
            throw new NotFoundException<User>(details.RecipientId);

        DateTime now = DateTime.UtcNow;
        long id = await database.Insert<Message>()
                                .Columns(m => m.AuthorId, m => m.RecipientId, m => m.Subject, m => m.Body, m => m.CreatedAt)
                                .Values(callerId, details.RecipientId, subject, details.Body, now)
                                .ReturnID()
                                .ExecuteAsync();

        return await GetById(callerId, isAdmin: true, id);
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
    public AsyncPageResponseWriter<MessageDetails> ListPaged(long callerId, bool isAdmin, MessageFilter filter = null) {
        filter ??= new();

        MessageMapper mapper = new();
        PredicateExpression<Message> predicate = BuildPredicate(callerId, isAdmin, filter);

        LoadOperation<Message> operation = mapper.CreateOperation(database, filter.Fields);

        if (string.IsNullOrEmpty(filter.Sort)) {
            operation.OrderBy(new OrderByCriteria(mapper["createdat"].Field, ascending: false));
        }

        operation.ApplyFilter(filter, mapper);
        operation.Where(predicate?.Content);

        LoadOperation<Message> countOperation = mapper.CreateOperation(database, DB.Count());
        countOperation.Where(predicate?.Content);

        return new AsyncPageResponseWriter<MessageDetails>(
            mapper.EntitiesFromOperation(operation),
            () => countOperation.ExecuteScalarAsync<long>(),
            filter.Continue
        );
    }


    PredicateExpression<Message> BuildPredicate(long callerId, bool isAdmin, MessageFilter filter) {
        PredicateExpression<Message> predicate = null;

        if (!isAdmin) {
            long scopeId = callerId;
            predicate &= new PredicateExpression<Message>(m => m.AuthorId == scopeId || m.RecipientId == scopeId);
        }

        if (filter.RecipientId?.Length > 0) {
            long[] recipientIds = filter.RecipientId;
            predicate &= new PredicateExpression<Message>(m => m.RecipientId.In(recipientIds));
        }

        if (filter.AuthorId?.Length > 0) {
            long[] authorIds = filter.AuthorId;
            predicate &= new PredicateExpression<Message>(m => m.AuthorId.In(authorIds));
        }

        return predicate;
    }


    /// <inheritdoc />
    public async Task Delete(long callerId, bool isAdmin, long id) {
        DeleteOperation<Message> operation = database.Delete<Message>();

        if (isAdmin) {
            operation.Where(m => m.Id == id);
        } else {
            long recipientId = callerId;
            operation.Where(m => m.Id == id && m.RecipientId == recipientId);
        }

        if (await operation.ExecuteAsync() == 0)
            throw new NotFoundException<Message>(id);
    }
}

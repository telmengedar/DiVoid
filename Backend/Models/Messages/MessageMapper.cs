using Pooshit.Ocelot.Fields;

namespace Backend.Models.Messages;

/// <summary>
/// mapper for <see cref="Message"/> entities to <see cref="MessageDetails"/> DTOs.
/// no User join — author/recipient name resolution is the client's responsibility
/// (see architectural doc §10.3 for the trade-off rationale).
/// </summary>
public class MessageMapper : FieldMapper<MessageDetails, Message> {

    /// <summary>
    /// creates a new <see cref="MessageMapper"/>
    /// </summary>
    public MessageMapper()
        : base(Mappings()) {
    }


    /// <inheritdoc />
    public override string[] DefaultListFields => ["id", "authorid", "recipientid", "subject", "createdat"];


    static IEnumerable<FieldMapping<MessageDetails>> Mappings() {
        yield return new FieldMapping<MessageDetails, long>("id",
                                                            m => m.Id,
                                                            (m, v) => m.Id = v);
        yield return new FieldMapping<MessageDetails, long>("authorid",
                                                            m => m.AuthorId,
                                                            (m, v) => m.AuthorId = v);
        yield return new FieldMapping<MessageDetails, long>("recipientid",
                                                            m => m.RecipientId,
                                                            (m, v) => m.RecipientId = v);
        yield return new FieldMapping<MessageDetails, string>("subject",
                                                              m => m.Subject,
                                                              (m, v) => m.Subject = v);
        yield return new FieldMapping<MessageDetails, string>("body",
                                                              m => m.Body,
                                                              (m, v) => m.Body = v);
        yield return new FieldMapping<MessageDetails, DateTime>("createdat",
                                                                m => m.CreatedAt,
                                                                (m, v) => m.CreatedAt = v);
    }
}

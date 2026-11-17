using Pooshit.Ocelot.Clients;

namespace Backend.Services.Teams;

/// <summary>Resolves the current protected Teams graph binding.</summary>
public interface ITeamsGraphBindingService {
    /// <summary>Reads one bounded graph snapshot and validates current owner and target eligibility.</summary>
    /// <param name="transaction">The existing inbox insertion transaction.</param>
    /// <param name="cancellationToken">The incoming request cancellation.</param>
    /// <returns>The original recipient and trusted subject tag for this message.</returns>
    Task<(long RecipientId, string Tag)> ResolveAsync(Transaction transaction, CancellationToken cancellationToken);
}

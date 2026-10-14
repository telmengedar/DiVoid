namespace Backend.tests.Fixtures;

/// <summary>
/// thrown by a mocked client when a query reaches the database, to stop a request after its statement was built
/// </summary>
public class QueryExecuted : Exception;

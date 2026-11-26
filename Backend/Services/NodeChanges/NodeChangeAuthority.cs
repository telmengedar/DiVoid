namespace Backend.Services.NodeChanges;

internal sealed record NodeChangeAuthority(bool Admin, DateTime? ExpiresAt, Exception Failure, long? OrganizationId = null);

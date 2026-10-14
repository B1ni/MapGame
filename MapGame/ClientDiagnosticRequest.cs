namespace MapGame;

public sealed record ClientDiagnosticRequest(string? ClientId, string? Event, string? Detail);

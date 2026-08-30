namespace Ripple.Treasury.Assessment.Api.Models;

public class HealthResponse
{
    public string Status { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public List<HealthCheckEntry> Checks { get; set; } = [];
}

public class HealthCheckEntry
{
    public string Name { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public double DurationMs { get; set; }
    public string? Error { get; set; }
}

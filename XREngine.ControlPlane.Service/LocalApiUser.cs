using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace XREngine.ControlPlane.Service;

/// <summary>Local development identity; secrets are loaded only from operator environment variables.</summary>
public sealed class LocalApiUser
{
    public string UserId { get; set; } = string.Empty;
    public string TenantId { get; set; } = "local";
    public string TokenEnvironmentVariable { get; set; } = string.Empty;
    public bool IsAdministrator { get; set; }
    private byte[] _tokenHash = [];
    private readonly object _budgetLock = new();
    private long _budgetSecond;
    private int _requestsInSecond;

    [JsonIgnore]
    public string TokenFingerprint => Convert.ToHexString(_tokenHash);

    /// <summary>
    /// Initializes the local API user by validating its configuration and computing the token hash.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown if the local API user configuration is invalid or the token is not properly set in the environment variable.</exception>
    public void Initialize()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(TenantId);

        if (TenantId.Length > 128 || UserId.Length > 128)
            throw new InvalidOperationException("Account and tenant identifiers must be bounded.");
        
        ArgumentException.ThrowIfNullOrWhiteSpace(TokenEnvironmentVariable);

        string? token = Environment.GetEnvironmentVariable(TokenEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(token) || token.Length < 32)
            throw new InvalidOperationException($"User '{UserId}' requires a token of at least 32 characters in its configured environment variable.");
        
        _tokenHash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
    }

    /// <summary>
    /// Determines whether the provided token matches the stored token hash for this local API user.
    /// </summary>
    /// <param name="token">The token to validate against the stored token hash.</param>
    /// <returns>True if the token matches the stored token hash; otherwise, false.</returns>
    public bool Matches(string token)
        => CryptographicOperations.FixedTimeEquals(_tokenHash, SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    /// <summary>
    /// Attempts to acquire a request budget for the current second, enforcing a rate limit of 120 requests per second.
    /// </summary>
    /// <returns>True if the request budget was successfully acquired; otherwise, false.</returns>
    internal bool TryAcquireRequestBudget()
    {
        lock (_budgetLock)
        {
            long second = Environment.TickCount64 / 1000;
            if (_budgetSecond != second)
            {
                _budgetSecond = second;
                _requestsInSecond = 0;
            }
            return ++_requestsInSecond <= 120;
        }
    }

    /// <summary>
    /// Used only by the authenticated HTTPS gateway when addressing its private agent.
    /// </summary>
    /// <returns>The bearer credential for the agent, retrieved from the configured environment variable.</returns>
    internal string AgentBearerCredential()
        => Environment.GetEnvironmentVariable(TokenEnvironmentVariable)
            ?? throw new InvalidOperationException("Backend identity credential is unavailable.");
}

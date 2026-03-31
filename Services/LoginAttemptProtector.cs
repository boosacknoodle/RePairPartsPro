using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace RepairPartsPro.Services;

public sealed class LoginAttemptProtector
{
    private readonly IMemoryCache _cache;
    private readonly IOptions<AuthProtectionOptions> _options;
    private readonly object _gate = new();

    public LoginAttemptProtector(IMemoryCache cache, IOptions<AuthProtectionOptions> options)
    {
        _cache = cache;
        _options = options;
    }

    public (bool Allowed, int RetryAfterSeconds, string Message) CanAttempt(string email)
    {
        var key = BuildKey(email);
        lock (_gate)
        {
            if (!_cache.TryGetValue<LoginAttemptState>(key, out var state) || state is null)
            {
                return (true, 0, string.Empty);
            }

            var now = DateTime.UtcNow;
            if (state.LockoutUntilUtc.HasValue && state.LockoutUntilUtc.Value > now)
            {
                var retry = (int)Math.Ceiling((state.LockoutUntilUtc.Value - now).TotalSeconds);
                return (false, retry, "Too many failed sign-in attempts. Try again later.");
            }

            if (state.CooldownUntilUtc.HasValue && state.CooldownUntilUtc.Value > now)
            {
                var retry = (int)Math.Ceiling((state.CooldownUntilUtc.Value - now).TotalSeconds);
                return (false, retry, "Too many failed sign-in attempts in a row. Wait a moment and try again.");
            }

            return (true, 0, string.Empty);
        }
    }

    public void RecordFailure(string email)
    {
        var key = BuildKey(email);
        var options = _options.Value;
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!_cache.TryGetValue<LoginAttemptState>(key, out var state) || state is null || state.WindowExpiresUtc <= now)
            {
                state = new LoginAttemptState
                {
                    FailureCount = 0,
                    ConsecutiveFailureCount = 0,
                    WindowExpiresUtc = now.AddMinutes(Math.Max(1, options.ObservationWindowMinutes))
                };
            }

            state.FailureCount++;
            state.ConsecutiveFailureCount++;

            if (state.FailureCount >= Math.Max(options.LockoutAfterFailures, options.CooldownAfterFailures))
            {
                if (state.FailureCount >= Math.Max(1, options.LockoutAfterFailures))
                {
                    state.LockoutUntilUtc = now.AddMinutes(Math.Max(1, options.LockoutMinutes));
                    state.CooldownUntilUtc = null;
                }
                else if (state.ConsecutiveFailureCount >= Math.Max(1, options.CooldownAfterFailures))
                {
                    state.CooldownUntilUtc = now.AddSeconds(Math.Max(1, options.CooldownSeconds));
                }
            }
            else if (state.ConsecutiveFailureCount >= Math.Max(1, options.CooldownAfterFailures))
            {
                state.CooldownUntilUtc = now.AddSeconds(Math.Max(1, options.CooldownSeconds));
            }

            _cache.Set(key, state, state.WindowExpiresUtc);
        }
    }

    public void RecordSuccess(string email)
    {
        lock (_gate)
        {
            _cache.Remove(BuildKey(email));
        }
    }

    private static string BuildKey(string email)
    {
        return $"login-protect::{(email ?? string.Empty).Trim().ToLowerInvariant()}";
    }

    private sealed class LoginAttemptState
    {
        public int FailureCount { get; set; }
        public int ConsecutiveFailureCount { get; set; }
        public DateTime WindowExpiresUtc { get; set; }
        public DateTime? CooldownUntilUtc { get; set; }
        public DateTime? LockoutUntilUtc { get; set; }
    }
}

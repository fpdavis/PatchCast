using Microsoft.Extensions.Options;

namespace PatchCast.Service;

// Server-wide throttle that slows password attempts to resist brute force. The
// required wait doubles with each consecutive failed attempt (0, 1, 2, 4, 8, ...
// seconds) up to a configurable maximum. It never decays on its own: it only
// returns to zero after a successful authentication or a service restart. The
// counter is global (the server has a single password), so failures on either the
// TCP or the WebSocket transport escalate the same cooldown.
public sealed class PasswordCooldown(IOptions<PatchCastOptions> options)
{
    private int failureCount;

    // The delay the next attempt must wait, derived from accumulated failures.
    public TimeSpan CurrentDelay => ComputeDelay(Volatile.Read(ref failureCount));

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        var delay = CurrentDelay;
        if (delay > TimeSpan.Zero)
            await Task.Delay(delay, cancellationToken);
    }

    public void RecordFailure() => Interlocked.Increment(ref failureCount);

    public void RecordSuccess() => Interlocked.Exchange(ref failureCount, 0);

    private TimeSpan ComputeDelay(int failures)
    {
        if (failures <= 0)
            return TimeSpan.Zero;

        var maxSeconds = Math.Max(0, options.Value.MaxPasswordCooldownSeconds);
        // Double from 1s per failure (1, 2, 4, 8, ...), stopping once the cap is
        // reached. A running shift avoids overflow and needs no lookup table.
        long seconds = 1;
        for (var i = 1; i < failures && seconds < maxSeconds; i++)
            seconds <<= 1;
        return TimeSpan.FromSeconds(Math.Min(seconds, maxSeconds));
    }
}

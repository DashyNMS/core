namespace DesktopNMS.Core.Api;

/// <summary>
/// A host's turnstile for the requests a client sends (#99), so a host that
/// can be asked many things at once - DashyNMS MCP, with a model firing tool
/// calls in parallel - keeps a limit on how much it asks of a server.
/// </summary>
/// <remarks>
/// <para>
/// It's here rather than a wrapper around the clients because only they can
/// see each attempt: a call may send several (retries, the backup address),
/// and each one's timeout has to start after the wait, or queueing behind a
/// busy server would turn into timeouts it never caused.
/// </para>
/// <para>
/// Core puts no limit on the wait either: a host bounds it by cancelling the
/// caller. A retry waits again, behind whatever has queued since. Anything a
/// client raises while handling an answer, such as
/// <see cref="LibreNmsTransport.CertificateRejected"/>, is raised inside the
/// turn, so a handler that blocks holds the turn until it returns and must not
/// wait on another request.
/// </para>
/// <para>
/// Core sets no limit of its own and asks for nothing by default: the clients'
/// <c>RequestGate</c> is null until a host sets one.
/// </para>
/// </remarks>
public interface IRequestGate
{
    /// <summary>
    /// Waits for a turn and takes it. Disposing the result gives it back,
    /// which the client does exactly once, when the attempt's response has
    /// been read or the attempt has failed. Cancelling the wait throws
    /// <see cref="OperationCanceledException"/> and must take no turn.
    /// </summary>
    ValueTask<IDisposable> EnterAsync(CancellationToken cancellationToken);
}

internal static class RequestGates
{
    /// <summary>A turn from <paramref name="gate"/>, or null - which <c>using</c> ignores - when there's no gate.</summary>
    internal static async ValueTask<IDisposable?> EnterOrPassAsync(this IRequestGate? gate, CancellationToken cancellationToken)
        => gate is null ? null : await gate.EnterAsync(cancellationToken).ConfigureAwait(false);
}

namespace dotmeer.filebrowser.Tasks;

/// <summary>
/// Сериализует границу публикации upload с пользовательской отменой.
/// Фиксированное число полос ограничивает память и разделяет независимые задачи.
/// </summary>
public sealed class TaskOperationGate
{
    private const int StripeCount = 64;
    private readonly SemaphoreSlim[] _stripes = Enumerable.Range(0, StripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();

    public async ValueTask<IDisposable> EnterAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        var stripe = _stripes[(taskId.GetHashCode() & int.MaxValue) % StripeCount];
        await stripe.WaitAsync(cancellationToken);
        return new Releaser(stripe);
    }

    private sealed class Releaser(SemaphoreSlim stripe) : IDisposable
    {
        private SemaphoreSlim? _stripe = stripe;

        public void Dispose() => Interlocked.Exchange(ref _stripe, null)?.Release();
    }
}

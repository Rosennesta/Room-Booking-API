namespace RoomBooking.Api.Tests.Integration;

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    private int _readCount;

    public int ReadCount => Volatile.Read(ref _readCount);

    public override DateTimeOffset GetUtcNow()
    {
        Interlocked.Increment(ref _readCount);
        return now;
    }
}

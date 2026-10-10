namespace Elka.VoiceMeeterFxHost.App;

// UI-thread policy; elapsed times use Environment.TickCount64, not the wall clock.
internal sealed class VoicemeeterButtonRegistration
{
    private const long PollMilliseconds = 1000;
    private const long SettleMilliseconds = 10000;
    private const long RetryMilliseconds = 2000;
    private long _nextPoll;
    private long _nextAttempt;
    private long? _readySince;
    private bool _registered;
    private bool _stopped;

    public bool ShouldPoll(long now, bool force = false)
    {
        if (_stopped || (!force && now < _nextPoll))
        {
            return false;
        }

        _nextPoll = now + PollMilliseconds;
        return true;
    }

    public bool ShouldRegister(long now, bool remoteReady)
    {
        if (_stopped)
        {
            return false;
        }

        if (!remoteReady)
        {
            _readySince = null;
            _registered = false;
            _nextAttempt = 0;
            return false;
        }

        _readySince ??= now;
        return !_registered && now - _readySince.Value >= SettleMilliseconds && now >= _nextAttempt;
    }

    public void RecordResult(long now, bool succeeded)
    {
        _registered = succeeded;
        _nextAttempt = now + RetryMilliseconds;
    }

    public void Stop() => _stopped = true;
}

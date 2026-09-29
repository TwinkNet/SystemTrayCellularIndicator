namespace SystemTrayThing;

public class AnimationTracker
{
    private long _lastBytesSent = 0L;
    private long _lastBytesReceived = 0L;
    private bool _wasAnim = false;
    private int _animStage = 0;

    public bool Update(long thisBytesSent, long thisBytesReceived)
    {
        bool anim = false;
        if (_lastBytesSent + 8000 < thisBytesSent) anim = true;
        if (_lastBytesReceived + 8000 < thisBytesReceived) anim = true;
        if (_lastBytesReceived == 0L && _lastBytesSent == 0L) anim = false;
        if (_wasAnim && AnimStage < 2) anim = true; // allows the animation to finish before switching to the static (network inactive) icon.
        UpdateAnimStage();
        _wasAnim = anim;
        _lastBytesReceived = thisBytesReceived;
        _lastBytesSent = thisBytesSent;
        return anim;
    }

    private void UpdateAnimStage()
    {
        if (!_wasAnim)
        {
            _animStage = 0;
            return;
        }
        _animStage++;
        if (_animStage > 2) _animStage = 0;
    }

    public int AnimStage => _animStage;
}
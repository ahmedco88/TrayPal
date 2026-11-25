// Focus/break countdown. No timer or UI here: MeterForm ticks it once a second.
using System;

sealed class FocusTimer
{
    public int FocusMinutes = 25, BreakMinutes = 5;   // read when a block starts

    bool running, paused, onBreak;
    DateTime endUtc;     // when the active block ends (unused while paused)
    TimeSpan left;       // time remaining while paused

    public bool Running { get { return running; } }
    public bool Paused { get { return paused; } }
    public bool OnBreak { get { return onBreak; } }

    void StartBlock()
    {
        endUtc = DateTime.UtcNow.AddMinutes(onBreak ? BreakMinutes : FocusMinutes);
    }

    public void Toggle()
    {
        if (running) { running = false; paused = false; onBreak = false; return; }
        running = true; paused = false; onBreak = false;
        StartBlock();
    }

    public void TogglePause()
    {
        if (!running) return;
        if (paused) { endUtc = DateTime.UtcNow + left; paused = false; }
        else { left = Remaining(); paused = true; }
    }

    // Adds to the current block (running or paused); a stopped timer ignores it.
    public void AddMinutes(int minutes)
    {
        if (!running) return;
        if (paused) left += TimeSpan.FromMinutes(minutes);
        else endUtc = endUtc.AddMinutes(minutes);
    }

    TimeSpan Remaining()
    {
        var t = endUtc - DateTime.UtcNow;
        return t < TimeSpan.Zero ? TimeSpan.Zero : t;
    }

    // True when a block just ended and the next one has started.
    public bool Tick()
    {
        if (!running || paused || DateTime.UtcNow < endUtc) return false;
        onBreak = !onBreak;
        StartBlock();
        return true;
    }

    // mm:ss left in the block, or null when stopped.
    public string Countdown
    {
        get
        {
            if (!running) return null;
            var t = paused ? left : Remaining();
            int secs = (int)Math.Ceiling(t.TotalSeconds);   // round up so a fresh block reads 25:00
            return (secs / 60).ToString("00") + ":" + (secs % 60).ToString("00");
        }
    }

    public string Label
    {
        get
        {
            if (!running) return null;
            return (paused ? "Paused " : onBreak ? "Break " : "Focus ") + Countdown;
        }
    }
}

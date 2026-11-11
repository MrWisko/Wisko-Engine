namespace Wisko.Engine.Gameplay;

/// <summary>
/// The Play session's in-game clock (Lua: GameTime): days and hours that pass at a developer-chosen pace. It advances
/// by the simulation's (scaled) time, times its own <see cref="Scale"/>, unless <see cref="Paused"/>; one game day takes
/// <see cref="DayLength"/> seconds at scale 1. Hour 0 of day 1 is <see cref="TotalHours"/> 0. Nothing is saved: scripts
/// keep it in a save (GameSave) when they want it back.
/// </summary>
public sealed class GameClock
{
    /// <summary>Seconds per game day at scale 1 (10 minutes).</summary>
    public const double DefaultDayLength = 600d;

    /// <summary>Where a session's clock starts: day 1, 08:00.</summary>
    public const double DefaultStartHour = 8d;

    public const double MinDayLength = 1d;
    public const double MaxDayLength = 10_000_000d;
    public const double MaxScale = 10_000d;

    // Days are counted from 1; far enough out, hours lose their minutes (a double keeps them for millions of days).
    public const double MaxDay = 1_000_000d;

    public double TotalHours { get; private set; } = DefaultStartHour;

    public double DayLength { get; private set; } = DefaultDayLength;

    public double Scale { get; private set; } = 1d;

    public bool Paused { get; set; }

    /// <summary>The day, from 1.</summary>
    public int Day => (int)global::System.Math.Floor(TotalHours / 24d) + 1;

    /// <summary>The hour of the day, 0 up to (not including) 24, with its fraction.</summary>
    public double Hour => TotalHours - (Day - 1) * 24d;

    public string? SetDayLength(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < MinDayLength || seconds > MaxDayLength)
        {
            return $"GameTime.DayLength must be a number of seconds from {MinDayLength:0} to {MaxDayLength:0}";
        }

        DayLength = seconds;
        return null;
    }

    public string? SetScale(double scale)
    {
        if (!double.IsFinite(scale) || scale < 0d || scale > MaxScale)
        {
            return $"GameTime.Scale must be a number from 0 to {MaxScale:0} (1: normal, 0: stopped)";
        }

        Scale = scale;
        return null;
    }

    /// <summary>Jumps to a day (a whole number from 1) and hour (0 up to 24).</summary>
    public string? Set(double day, double hour)
    {
        if (!double.IsFinite(day) || day < 1d || day > MaxDay || global::System.Math.Floor(day) != day)
        {
            return "GameTime.Set expects a day: a whole number from 1";
        }

        if (!double.IsFinite(hour) || hour < 0d || hour >= 24d)
        {
            return "GameTime.Set expects an hour from 0 up to 24 (e.g. 6.5 for 06:30)";
        }

        TotalHours = (day - 1d) * 24d + hour;
        return null;
    }

    /// <summary>One frame of simulation time (already scaled by the session's time scale).</summary>
    public void Advance(double seconds)
    {
        if (Paused || !(seconds > 0d) || !double.IsFinite(seconds))
        {
            return;
        }

        var next = TotalHours + seconds * Scale * 24d / DayLength;
        TotalHours = global::System.Math.Min(next, MaxDay * 24d - 1e-6);
    }
}

namespace Wisko.Tests;

/// <summary>Pass/fail bookkeeping: one line per check, sections, and a summary.</summary>
internal sealed class TestReport
{
    private readonly List<string> _failures = [];

    public int Passed { get; private set; }

    public int Failed => _failures.Count;

    public void Section(string title, Action body)
    {
        Console.WriteLine($"\n=== {title} ===");
        try
        {
            body();
        }
        catch (Exception ex)
        {
            Check($"{title}: completes without an unexpected exception", false, ex.ToString());
        }
    }

    public void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(string.IsNullOrEmpty(detail) ? string.Empty : " -- " + detail)}");
        if (ok)
        {
            Passed++;
        }
        else
        {
            _failures.Add(name);
        }
    }

    public static void Info(string text) => Console.WriteLine($"[INFO] {text}");

    /// <summary>Prints the totals and every failure; true when nothing failed.</summary>
    public bool PrintSummary()
    {
        Console.WriteLine($"\n=== SUMMARY: {Passed} passed, {Failed} failed ===");
        foreach (var failure in _failures)
        {
            Console.WriteLine($"  FAILED: {failure}");
        }

        return Failed == 0;
    }
}

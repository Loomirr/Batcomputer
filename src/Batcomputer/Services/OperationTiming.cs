using System.Diagnostics;

namespace Batcomputer;

/// <summary>One compact diagnostic line per operation; no persistent performance cache.</summary>
internal sealed class OperationTiming(string name, Action<string> log) : IDisposable
{
    private readonly Stopwatch _watch = Stopwatch.StartNew();
    private readonly List<string> _steps = [];
    private double _previous;
    internal void Mark(string step)
    {
        var elapsed = _watch.Elapsed.TotalSeconds;
        _steps.Add($"{step} {elapsed - _previous:F2}s");
        _previous = elapsed;
    }
    public void Dispose() => log($"Timing · {name}: {_watch.Elapsed.TotalSeconds:F2}s" +
        (_steps.Count == 0 ? "" : " (" + string.Join(", ", _steps) + ")"));
}

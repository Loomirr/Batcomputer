using System.Diagnostics;

namespace Batcomputer;

/// <summary>Pass the user's selected local runtime to helpers; never fetch or copy it.</summary>
internal static class RetocRuntime
{
    internal const string Variable = "BATCOMPUTER_OODLE_DLL";
    internal static void Configure(ProcessStartInfo start, AppSettings? settings = null)
    {
        start.Environment.Remove(Variable); // Do not trust a stale inherited override.
        var runtime = (settings ?? AppSettings.Current).EffectiveOodleRuntimeDllPath();
        if (!string.IsNullOrWhiteSpace(runtime) && File.Exists(runtime))
            start.Environment[Variable] = Path.GetFullPath(runtime);
    }
}

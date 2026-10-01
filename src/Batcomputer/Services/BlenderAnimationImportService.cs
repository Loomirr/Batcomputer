using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Batcomputer;

/// <summary>Runs the bundled read-only bridge in a separate Blender process. Never saves its source.</summary>
internal static class BlenderAnimationImportService
{
    internal sealed record Choice(string Rig, string Action, string Slot, string Label);
    internal static async Task RunAsync(string executable, string source, string work, object config)
    {
        if (!File.Exists(executable) || !Path.GetFileName(executable).Equals("blender.exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose the installed blender.exe.");
        if (!File.Exists(source) || !Path.GetExtension(source).Equals(".blend", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Choose a saved Blender file on the LOTDK body rig.");
        Directory.CreateDirectory(work);
        var script = Path.Combine(work, "import_blender_animation.py");
        using (var resource = typeof(BlenderAnimationImportService).Assembly.GetManifestResourceStream("Batcomputer.Tools.Animations.import_blender_animation.py")
            ?? throw new InvalidDataException("The Blender import bridge is missing."))
        using (var output = File.Create(script)) await resource.CopyToAsync(output);
        var configPath = Path.Combine(work, "import.json");
        await File.WriteAllTextAsync(configPath, JsonSerializer.Serialize(config));
        byte[] before;
        using (var stream = File.OpenRead(source)) before = SHA256.HashData(stream);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = work };
        foreach (var argument in new[] { "--background", "--factory-startup", "--disable-autoexec", source,
            "--python-exit-code", "1", "--python", script, "--", configPath }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidDataException("Blender could not start.");
        var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { try { process.Kill(true); } catch { } throw new InvalidDataException("Blender import timed out after three minutes."); }
        var details = (await stdout) + "\n" + (await stderr);
        await File.WriteAllTextAsync(Path.Combine(work, "blender-import.log"), details);
        using (var stream = File.OpenRead(source))
            if (!before.SequenceEqual(SHA256.HashData(stream))) throw new InvalidDataException("The source Blender file changed while importing. Retry after saving and closing its edits.");
        if (process.ExitCode != 0) throw new InvalidDataException("Blender import failed. " + string.Join("\n", details.Split('\n').TakeLast(16)));
    }
}

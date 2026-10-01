using System.Diagnostics;
using System.Security.Cryptography;

namespace Batcomputer;

internal static class CharacterVoicePreviewService
{
    internal static async Task<string> NativeAsync(string workspace, string media, string language, CancellationToken cancellation)
    {
        var decoder = AppSettings.Current.EffectiveVgmstreamExePath()
            ?? throw new InvalidDataException("Choose a vgmstream executable with Audio decoder… to preview native voice recordings. Imported PCM WAVs play without it.");
        var cache = Path.Combine(AppSettings.GeneratedRootFor(workspace), "VoicePreview");
        using var provider = ModelPreviewService.MakeProvider(AppSettings.Current.EffectiveGamePaksRoot(), AppSettings.Current.EffectiveUsmapPath()!);
        var source = CharacterVoiceCatalogService.FindMedia(provider.Files.Keys, media, language)
            ?? throw new FileNotFoundException("This recording was not found in the selected language or shared media. Other languages were not substituted.");
        cancellation.ThrowIfCancellationRequested();
        var bytes = provider.SaveAsset(source);
        var key = Convert.ToHexString(SHA256.HashData(bytes));
        Directory.CreateDirectory(cache);
        var output = Path.Combine(cache, key + ".wav");
        if (File.Exists(output)) { CharacterVoiceLibraryService.InspectWav(File.ReadAllBytes(output)); return output; }
        var temporary = Path.Combine(cache, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var input = Path.Combine(temporary, "input.wem"); var decoded = Path.Combine(temporary, "output.wav");
        File.WriteAllBytes(input, bytes);
        var start = new ProcessStartInfo(decoder) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("-o"); start.ArgumentList.Add(decoded); start.ArgumentList.Add(input);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the voice decoder.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellation); var stderr = process.StandardError.ReadToEndAsync(cancellation);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation); timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); throw; }
        await stdout; var error = await stderr;
        if (process.ExitCode != 0 || !File.Exists(decoded)) throw new InvalidDataException("Voice decoding failed: " + error[..Math.Min(error.Length, 500)]);
        CharacterVoiceLibraryService.InspectWav(File.ReadAllBytes(decoded));
        cancellation.ThrowIfCancellationRequested();
        if (!File.Exists(output)) File.Copy(decoded, output);
        return output;
    }
}

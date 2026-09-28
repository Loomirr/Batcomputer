using System.Diagnostics;
using CUE4Parse.FileProvider;

namespace Batcomputer;

/// <summary>
/// Hearing a vehicle sound before choosing it. A <c>WubAudioEvent</c> holds only a name, so the audio is found the
/// way Wwise finds it: the event's short ID is the FNV-1 hash of its lowercased name, and this game generates one
/// soundbank per event (6,847 of 6,892 banks hold exactly one), so the bank whose HIRC list contains that ID owns
/// the sound. Its <c>CAkSound</c> entries name media IDs, which are loose <c>Media/&lt;id&gt;.wem</c> files.
///
/// The .wem itself is Wwise-encoded: mostly the proprietary ADPCM (RIFF codec 0x8311, two 16-bit histories per
/// 36-byte block rather than IMA), some PCM, a little Vorbis and a few source plugins that synthesise their sound
/// and have no media at all. Rather than guess at an undocumented ADPCM variant, decoding is delegated to
/// vgmstream-cli, which the user points at in Settings; without it the preview says so and changes nothing else.
/// </summary>
internal static class VehicleSoundPreviewService
{
    private const string Banks = "LEGOBatmanLotDK/Content/Wub/Platforms/Windows/Banks/";
    private const string Media = "LEGOBatmanLotDK/Content/Wub/Platforms/Windows/Media/";
    internal const string Missing =
        "Sound preview needs vgmstream to decode the game's audio. Download vgmstream-cli (vgmstream.org), then set " +
        "its path in Setup → Tools. Batcomputer never ships or copies it, and vehicle sounds work in game without it.";

    /// <summary>Wwise's own name hash: FNV-1 (multiply, then xor) over the lowercased event name.</summary>
    internal static uint EventId(string name)
    {
        var hash = 2166136261u;
        foreach (var value in name.ToLowerInvariant()) hash = unchecked(hash * 16777619u) ^ value;
        return hash;
    }

    /// <param name="Plugin">Wwise codec/plugin id: 0x00020001 ADPCM, 0x00010001 PCM, 0x00040001 Vorbis; anything
    /// else (0x01990002 here) is a source plugin that generates its sound and has no media file.</param>
    internal sealed record Source(uint MediaId, uint Plugin, int Size);

    private static uint U32(ReadOnlySpan<byte> data, int offset) => BitConverter.ToUInt32(data[offset..]);

    /// <summary>The sounds of the bank that owns this event, biggest first: for a layered engine the largest layer
    /// is the most representative thing to play.</summary>
    internal static IReadOnlyList<Source> Sources(IFileProvider provider, string eventName)
    {
        var id = EventId(eventName);
        foreach (var path in provider.Files.Keys.Where(f => f.StartsWith(Banks, StringComparison.OrdinalIgnoreCase) && f.EndsWith(".bnk", StringComparison.OrdinalIgnoreCase)))
        {
            byte[] bank;
            try { bank = provider.SaveAsset(path); } catch (Exception) { continue; }
            var found = false; var sounds = new List<Source>();
            for (var offset = 0; offset + 8 <= bank.Length;)
            {
                var chunk = System.Text.Encoding.ASCII.GetString(bank, offset, 4);
                var size = (int)U32(bank, offset + 4); var body = offset + 8;
                if (size < 0 || body + size > bank.Length) break;
                if (chunk == "HIRC")
                {
                    var count = U32(bank, body); var p = body + 4;
                    for (var i = 0u; i < count && p + 9 <= bank.Length; i++)
                    {
                        var type = bank[p]; var objectSize = (int)U32(bank, p + 1); var objectId = U32(bank, p + 5);
                        if (type == 4 && objectId == id) found = true;
                        if (type == 2 && objectSize >= 17)
                            sounds.Add(new(U32(bank, p + 14), U32(bank, p + 9), (int)U32(bank, p + 18)));
                        p += 5 + objectSize;
                    }
                }
                offset = body + size;
            }
            if (found) return sounds.OrderByDescending(s => s.Size).ToArray();
        }
        return [];
    }

    /// <summary>A .wem is a RIFF: the format chunk says how it is encoded, the data chunk is the audio.</summary>
    private static (ushort Codec, ushort Channels, int Rate, ushort Bits, int DataStart, int DataLength)? Riff(byte[] wem)
    {
        if (wem.Length < 20 || System.Text.Encoding.ASCII.GetString(wem, 0, 4) != "RIFF") return null;
        ushort codec = 0, channels = 0, bits = 0; var rate = 0; var start = 0; var length = 0;
        for (var offset = 12; offset + 8 <= wem.Length;)
        {
            var chunk = System.Text.Encoding.ASCII.GetString(wem, offset, 4);
            var size = (int)U32(wem, offset + 4); var body = offset + 8;
            if (size < 0 || body + size > wem.Length) break;
            if (chunk == "fmt " && size >= 16)
            {
                codec = BitConverter.ToUInt16(wem, body); channels = BitConverter.ToUInt16(wem, body + 2);
                rate = (int)U32(wem, body + 4); bits = BitConverter.ToUInt16(wem, body + 14);
            }
            if (chunk == "data") { start = body; length = size; break; }
            offset = body + size + (size & 1);
        }
        return length == 0 ? null : (codec, channels, rate, bits, start, length);
    }
    private static bool IsPcm(ushort codec) => codec is 0x0001 or 0xFFFE;
    /// <summary>Force-feedback tracks sit in the same banks as the audio, at 2-3 kHz. They are not sounds.</summary>
    private const int LowestAudioRate = 8000;

    private static void WritePcmWav(string path, byte[] wem, (ushort Codec, ushort Channels, int Rate, ushort Bits, int DataStart, int DataLength) riff)
    {
        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);
        var blockAlign = (ushort)(riff.Channels * riff.Bits / 8);
        writer.Write("RIFF"u8); writer.Write(36 + riff.DataLength); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((ushort)1); writer.Write(riff.Channels);
        writer.Write(riff.Rate); writer.Write(riff.Rate * blockAlign); writer.Write(blockAlign); writer.Write(riff.Bits);
        writer.Write("data"u8); writer.Write(riff.DataLength);
        writer.Write(wem, riff.DataStart, riff.DataLength);
    }

    /// <summary>
    /// Decodes the event's audio to a .wav in <paramref name="cacheDirectory"/>, or explains why it cannot.
    /// Sounds are tried largest first, since a layered engine's biggest layer is its most representative one;
    /// plain PCM is written out directly, so those previews work with no external tool at all.
    /// </summary>
    internal static (string? Wav, string? Problem) Wav(IFileProvider provider, string eventName, string cacheDirectory)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(eventName, "^[A-Za-z0-9_]{1,160}$"))
            return (null, "The sound event name is invalid.");
        Directory.CreateDirectory(cacheDirectory);
        var wav = Path.Combine(cacheDirectory, eventName + ".wav");
        if (File.Exists(wav)) return (wav, null);
        var sources = Sources(provider, eventName);
        if (sources.Count == 0) return (null, "This sound is not in the installed game's audio banks: " + eventName);
        var tool = AppSettings.Current.EffectiveVgmstreamExePath();
        var encoded = false;
        foreach (var source in sources)
        {
            if (source.Plugin is not (0x00010001 or 0x00020001 or 0x00040001)) continue;
            var mediaPath = Media + source.MediaId + ".wem";
            if (!provider.Files.ContainsKey(mediaPath)) continue;
            byte[] wem;
            try { wem = provider.SaveAsset(mediaPath); } catch (Exception) { continue; }
            if (Riff(wem) is not { } riff || riff.Rate < LowestAudioRate) continue;
            if (IsPcm(riff.Codec) && riff.Bits == 16) { WritePcmWav(wav, wem, riff); return (wav, null); }
            encoded = true;
            if (tool is null) continue; // Maybe a later source is plain PCM and needs no decoder.
            var wemFile = Path.Combine(cacheDirectory, source.MediaId + ".wem");
            File.WriteAllBytes(wemFile, wem);
            try
            {
                using var process = Process.Start(new ProcessStartInfo(tool, ["-o", wav, wemFile]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })
                    ?? throw new InvalidOperationException("vgmstream did not start.");
                var stderr = process.StandardError.ReadToEndAsync();
                var stdout = process.StandardOutput.ReadToEndAsync();
                if (!process.WaitForExit(60000)) { process.Kill(entireProcessTree: true); return (null, "vgmstream timed out while decoding " + eventName + "."); }
                Task.WaitAll(stderr, stdout);
                var error = stderr.Result;
                if (File.Exists(wav)) return (wav, null);
                return (null, "vgmstream could not decode " + eventName + ". " + error.Trim());
            }
            catch (Exception ex) { return (null, "vgmstream could not be run: " + ex.Message); }
            finally { try { File.Delete(wemFile); } catch (Exception) { } }
        }
        return (null, encoded ? Missing : "This sound is generated by a Wwise plugin rather than a recording, so there is nothing to play back.");
    }
}

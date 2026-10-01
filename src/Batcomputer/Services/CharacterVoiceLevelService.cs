namespace Batcomputer;

internal static class CharacterVoiceLevelService
{
    internal static void Validate(double gainDb)
    {
        if (!double.IsFinite(gainDb) || gainDb is < -12 or > 6)
            throw new InvalidDataException("Voice volume must be between -12 and +6 dB.");
    }

    /// <summary>Adjusts a build/preview copy. A smooth peak limit prevents boost clipping.</summary>
    internal static byte[] Apply(byte[] source, double gainDb)
    {
        Validate(gainDb);
        CharacterVoiceLibraryService.InspectWav(source);
        var output = source.ToArray();
        if (gainDb == 0) return output;
        double gain = Math.Pow(10, gainDb / 20);
        for (int at = 12; at <= output.Length - 8;)
        {
            int size = checked((int)BitConverter.ToUInt32(output, at + 4));
            if (output.AsSpan(at, 4).SequenceEqual("data"u8))
            {
                for (int i = at + 8; i < at + 8 + size; i += 2)
                {
                    double value = BitConverter.ToInt16(output, i) / 32768.0 * gain;
                    var magnitude = Math.Abs(value);
                    // Leave reconstruction headroom for the game audio codec/resampler.
                    if (gainDb > 0 && magnitude > .80)
                        magnitude = .80 + .10 * (1 - Math.Exp(-(magnitude - .80) / .10));
                    short sample = (short)Math.Clamp(Math.Round(Math.CopySign(magnitude, value) * 32768), short.MinValue, short.MaxValue);
                    System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(i, 2), sample);
                }
            }
            at = checked(at + 8 + size + (size & 1));
        }
        return output;
    }

    internal static bool HasClippedSamples(byte[] source)
    {
        CharacterVoiceLibraryService.InspectWav(source);
        for(int at=12;at<=source.Length-8;)
        {
            int size=checked((int)BitConverter.ToUInt32(source,at+4));
            if(source.AsSpan(at,4).SequenceEqual("data"u8))
                for(int i=at+8;i<at+8+size;i+=2)
                    if(BitConverter.ToInt16(source,i) is short.MinValue or short.MaxValue)return true;
            at=checked(at+8+size+(size&1));
        }
        return false;
    }
}

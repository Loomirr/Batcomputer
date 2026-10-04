using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Batcomputer;

/// <summary>Separates decoder-setup identities in user-encoded modern Vorbis media.
/// Some encoders emit one fixed identity across different quality/channel setups.
/// Native media must never pass through this private-output normalization.</summary>
internal static class PrivateWemSetupService
{
    internal static byte[] Normalize(byte[] source)
    {
        if(source.Length<12 || !source.AsSpan(0,4).SequenceEqual("RIFF"u8) || !source.AsSpan(8,4).SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Private audio is not a RIFF WEM.");
        if(BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4,4))!=source.Length-8)
            throw new InvalidDataException("Private audio RIFF length is invalid.");
        int fmt=-1,fmtSize=0,data=-1,dataSize=0;
        for(int at=12;at<source.Length;)
        {
            if(at>source.Length-8)throw new InvalidDataException("Truncated private audio chunk.");
            int size=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(at+4,4)));
            if(size>source.Length-at-8)throw new InvalidDataException("Private audio chunk exceeds its file.");
            if(source.AsSpan(at,4).SequenceEqual("fmt "u8)){if(fmt>=0)throw new InvalidDataException("Duplicate WEM format.");fmt=at+8;fmtSize=size;}
            if(source.AsSpan(at,4).SequenceEqual("data"u8)){if(data>=0)throw new InvalidDataException("Duplicate WEM data.");data=at+8;dataSize=size;}
            at=checked(at+8+size+(size&1));
        }
        if(fmt<0||fmtSize<16||data<0)throw new InvalidDataException("Incomplete private WEM.");
        // Other Wwise codec/header layouts do not expose this field here.
        if(BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(fmt,2))!=0xffff||fmtSize!=0x42)return source.ToArray();
        int seek=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(fmt+0x28,4)));
        int audio=checked((int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(fmt+0x2c,4)));
        if(seek>dataSize-2||audio>dataSize)throw new InvalidDataException("Invalid private Vorbis setup offsets.");
        int setupSize=BinaryPrimitives.ReadUInt16LittleEndian(source.AsSpan(data+seek,2));
        if(setupSize==0||setupSize>dataSize-seek-2||seek+2+setupSize!=audio)
            throw new InvalidDataException("Invalid private Vorbis setup packet.");
        using var hash=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("PrivateWemDecoderSetup-v1"u8);
        hash.AppendData(source.AsSpan(fmt+2,6));
        hash.AppendData(source.AsSpan(fmt+0x14,4));
        hash.AppendData(source.AsSpan(fmt+0x40,2));
        hash.AppendData(source.AsSpan(data+seek+2,setupSize));
        var identity=BinaryPrimitives.ReadUInt32LittleEndian(hash.GetHashAndReset());if(identity==0)identity=1;
        var result=source.ToArray();BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(fmt+0x3c,4),identity);return result;
    }
}

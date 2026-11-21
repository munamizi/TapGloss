using System.Buffers.Binary;
using System.IO;

namespace WindowsLookup;

/// <summary>在子进程和播放器之间交换标准 PCM16 WAV。</summary>
internal static class SpeechWave
{
    internal const int MaxBytes = 8 * 1024 * 1024;

    // 量化单声道采样，并在有效音频前后分别补 20 和 40 毫秒静音。
    internal static byte[] _encode(float[] samples, int rate)
    {
        if (rate is < 8000 or > 192000 || samples.Length == 0 || samples.Length > (MaxBytes - 44) / 2)
        {
            throw new InvalidDataException("Amy 返回了无效的采样。");
        }

        int leading = rate * 20 / 1000;
        int trailing = rate * 40 / 1000;
        int length = checked((leading + samples.Length + trailing) * sizeof(short));
        if (length > MaxBytes - 44)
        {
            throw new InvalidDataException("Amy 的音频数据过长。");
        }
        byte[] wave = new byte[44 + length];
        Span<byte> bytes = wave;
        "RIFF"u8.CopyTo(bytes);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[4..], wave.Length - 8);
        "WAVEfmt "u8.CopyTo(bytes[8..]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[20..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[24..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[28..], rate * sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[32..], sizeof(short));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes[34..], 16);
        "data"u8.CopyTo(bytes[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(bytes[40..], length);

        int offset = 44 + leading * sizeof(short);
        for (int index = 0; index < samples.Length; index++)
        {
            float sample = float.IsFinite(samples[index]) ? Math.Clamp(samples[index], -1, 1) : 0;
            int quantized = (int)MathF.Round(sample * 32768);
            short pcm = (short)Math.Clamp(quantized, short.MinValue, short.MaxValue);
            BinaryPrimitives.WriteInt16LittleEndian(bytes[(offset + index * sizeof(short))..], pcm);
        }
        return wave;
    }

    // 按 RIFF 块长度寻找格式和数据，并校验 PCM16 格式与对齐。
    internal static Pcm _read(ReadOnlySpan<byte> wave)
    {
        if (wave.Length < 12 || !wave[..4].SequenceEqual("RIFF"u8) || !wave[8..12].SequenceEqual("WAVE"u8))
        {
            throw new InvalidDataException("WAV 头无效。");
        }
        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(wave[4..]);
        if (declared < 4 || declared > wave.Length - 8)
        {
            throw new InvalidDataException("WAV 长度不符。");
        }

        int end = (int)declared + 8;
        int pcmOffset = 0;
        int pcmSize = 0;
        int rate = 0;
        ushort channels = 0;
        ushort align = 0;
        for (int offset = 12; offset + 8 <= end;)
        {
            ReadOnlySpan<byte> chunk = wave[offset..];
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
            int body = offset + 8;
            if ((long)size + (size & 1) > end - body)
            {
                throw new InvalidDataException("WAV 数据块超出范围。");
            }
            if (chunk[..4].SequenceEqual("fmt "u8) && size >= 16)
            {
                ushort format = BinaryPrimitives.ReadUInt16LittleEndian(chunk[8..]);
                channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[10..]);
                rate = BinaryPrimitives.ReadInt32LittleEndian(chunk[12..]);
                int byteRate = BinaryPrimitives.ReadInt32LittleEndian(chunk[16..]);
                align = BinaryPrimitives.ReadUInt16LittleEndian(chunk[20..]);
                ushort bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk[22..]);
                if (format != 1 || bits != 16 || channels is < 1 or > 2 || rate is < 8000 or > 192000
                    || align != channels * sizeof(short) || byteRate != rate * align)
                {
                    throw new InvalidDataException("WAV 的 PCM 格式无效。");
                }
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                pcmOffset = body;
                pcmSize = (int)size;
            }
            offset = body + (int)size + (int)(size & 1);
        }

        if (rate == 0 || pcmSize == 0 || pcmSize % align != 0)
        {
            throw new InvalidDataException("WAV 缺少完整的音频数据。");
        }
        return new(rate, channels, align, pcmOffset, pcmSize);
    }

    // 读取实际 PCM 幅度，防止静音结果误通过发布自检。
    internal static bool _hasSound(byte[] wave)
    {
        Pcm pcm = _read(wave);
        ReadOnlySpan<byte> samples = wave.AsSpan(pcm.Offset, pcm.Length);
        for (int i = 0; i < samples.Length; i += sizeof(short))
        {
            if (Math.Abs((int)BinaryPrimitives.ReadInt16LittleEndian(samples[i..])) > 32)
            {
                return true;
            }
        }
        return false;
    }

    internal readonly record struct Pcm(int Rate, ushort Channels, ushort Align, int Offset, int Length);
}

using System.Buffers.Binary;
using NVorbis;
using NVorbis.Ogg;
using GetPacketGranuleCount = NVorbis.Contracts.GetPacketGranuleCount;
using IPacket = NVorbis.Contracts.IPacket;
using IPacketProvider = NVorbis.Contracts.IPacketProvider;

namespace SkinForge.Plugin;

// Ogg Vorbis to interleaved float PCM, ported from ForgeAudio (VorbisDecode, AssetValidation.Ogg). The container is
// checked before decoding: one Vorbis stream, mono or stereo, at most 10 seconds. NVorbis 0.10.5 can spin forever when it
// trims the last packet to a too-small end granule, so it is fed packets that never report end-of-stream, a hard packet
// limit bounds the loop, and the PCM is trimmed to the final granule here. Every failure is an InvalidDataException.
static class Vorbis
{
    const int MaxSeconds = 10;
    const int ToleranceFrames = 16384; // two maximum Vorbis blocks of encoder padding

    public static (float[] Samples, int Channels, int Rate) Decode(byte[] bytes)
    {
        var (channels, rate, frames) = Check(bytes);
        try
        {
            using var container = new ContainerReader(new MemoryStream(bytes, false), true);
            IPacketProvider? stream = null;
            container.NewStreamCallback = p => { stream ??= p; return true; };
            if (!container.TryInit() || stream == null) throw new InvalidDataException("no Vorbis stream in the Ogg file");
            // Every Ogg packet ends at a lacing value, so a file cannot hold more packets than bytes.
            using var decoder = new StreamDecoder(new BoundedPackets(stream, bytes.Length + 16)) { ClipSamples = true };
            if (decoder.Channels != channels || decoder.SampleRate != rate) throw new InvalidDataException("Vorbis header disagrees with the Ogg page");
            var samples = new float[(frames + ToleranceFrames) * channels];
            int count = 0;
            while (count < samples.Length)
            {
                int read = decoder.Read(samples, count, Math.Min(16384 * channels, samples.Length - count));
                if (read == 0) break;
                count += read;
            }
            long decoded = count / channels;
            if (decoded <= 0 || frames - decoded > ToleranceFrames) throw new InvalidDataException("Vorbis audio ends before its declared end");
            Array.Resize(ref samples, (int)Math.Min(decoded, frames) * channels); // frames past the final granule are padding
            foreach (var x in samples)
                if (!float.IsFinite(x)) throw new InvalidDataException("Vorbis decoder produced non-finite samples");
            return (samples, channels, rate);
        }
        catch (Exception e) when (e is not InvalidDataException)
        {
            throw new InvalidDataException($"Vorbis decoder rejected the stream: {e.Message}", e);
        }
    }

    static (int Channels, int Rate, long Frames) Check(byte[] b)
    {
        if (b.Length < 58 || b[0] != 'O' || b[1] != 'g' || b[2] != 'g' || b[3] != 'S' || b[4] != 0 || b[26] < 1)
            throw new InvalidDataException("not an Ogg file");
        int packet = 27 + b[26];
        if (packet + 30 > b.Length || b[packet] != 1 || System.Text.Encoding.ASCII.GetString(b, packet + 1, 6) != "vorbis")
            throw new InvalidDataException("Ogg but not Vorbis (Opus is not supported)");
        int channels = b[packet + 11];
        uint rate = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(packet + 12));
        if (channels is < 1 or > 2 || rate is < 8000 or > 192000 || BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(packet + 7)) != 0 || b[packet + 29] != 1)
            throw new InvalidDataException("expected mono or stereo Vorbis at 8 to 192 kHz");
        uint serial = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(14)), sequence = 0;
        int offset = 0;
        ulong samples = 0;
        bool ended = false;
        while (offset < b.Length)
        {
            if (ended || b.Length - offset < 27 || b[offset] != 'O' || b[offset + 1] != 'g' || b[offset + 2] != 'g' || b[offset + 3] != 'S' || b[offset + 4] != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset + 14)) != serial
                || BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(offset + 18)) != sequence++)
                throw new InvalidDataException("invalid or chained Ogg pages (one Vorbis stream is required)");
            byte flags = b[offset + 5];
            if (offset == 0 && flags != 2 && flags != 6 || offset != 0 && (flags & 2) != 0) throw new InvalidDataException("invalid Ogg beginning-of-stream flag");
            int segments = b[offset + 26], body = offset + 27 + segments;
            if (body > b.Length) throw new InvalidDataException("truncated Ogg segment table");
            int length = 0;
            for (int i = 0; i < segments; i++) length += b[offset + 27 + i];
            if (length > b.Length - body || offset == 0 && length < 30) throw new InvalidDataException("truncated Ogg page");
            ulong granule = BinaryPrimitives.ReadUInt64LittleEndian(b.AsSpan(offset + 6));
            if (granule != ulong.MaxValue)
            {
                if (granule < samples) throw new InvalidDataException("non-monotonic Vorbis sample positions");
                samples = granule;
                if (samples > rate * (ulong)MaxSeconds) throw new InvalidDataException($"longer than {MaxSeconds} seconds");
            }
            ended = (flags & 4) != 0;
            offset = body + length;
        }
        if (!ended || samples == 0) throw new InvalidDataException("Ogg stream is truncated (no final page)");
        return (channels, (int)rate, (long)samples);
    }

    sealed class BoundedPackets : IPacketProvider
    {
        readonly IPacketProvider _inner;
        readonly int _limit;
        int _served;
        public BoundedPackets(IPacketProvider inner, int limit) { _inner = inner; _limit = limit; }
        public bool CanSeek => false;
        public int StreamSerial => _inner.StreamSerial;
        public IPacket? GetNextPacket()
        {
            if (++_served > _limit) throw new InvalidDataException("Vorbis stream yields more packets than the file can hold");
            var p = _inner.GetNextPacket();
            return p == null ? null : new Packet(p);
        }
        public IPacket? PeekNextPacket()
        {
            var p = _inner.PeekNextPacket();
            return p == null ? null : new Packet(p);
        }
        public long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount) => throw new NotSupportedException();
        public long GetGranuleCount() => _inner.GetGranuleCount();
    }

    sealed class Packet : IPacket
    {
        readonly IPacket _inner;
        public Packet(IPacket inner) { _inner = inner; }
        public bool IsResync => _inner.IsResync;
        public bool IsShort => _inner.IsShort;
        public long? GranulePosition => _inner.GranulePosition;
        public bool IsEndOfStream => false;
        public int BitsRead => _inner.BitsRead;
        public int BitsRemaining => _inner.BitsRemaining;
        public int ContainerOverheadBits => _inner.ContainerOverheadBits;
        public ulong TryPeekBits(int count, out int bitsRead) => _inner.TryPeekBits(count, out bitsRead);
        public void SkipBits(int count) => _inner.SkipBits(count);
        public ulong ReadBits(int count) => _inner.ReadBits(count);
        public void Done() => _inner.Done();
        public void Reset() => _inner.Reset();
    }
}

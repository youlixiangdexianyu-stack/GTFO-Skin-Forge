using System;
using System.IO;
using System.Threading;
using NVorbis;
using IPacket = NVorbis.Contracts.IPacket;
using IPacketProvider = NVorbis.Contracts.IPacketProvider;
using GetPacketGranuleCount = NVorbis.Contracts.GetPacketGranuleCount;
using NVorbis.Ogg;

namespace ForgeAudio;

internal sealed record DecodedAudio(float[] Samples, int Channels, int SampleRate)
{
    internal int Frames => Samples.Length / Channels;
}

/// <summary>Managed Vorbis decoding that always terminates and can be cancelled between packets.
/// NVorbis 0.10.5 trims the final packet to the end-of-stream granule inside <c>StreamDecoder.Read</c>; when that granule is
/// smaller than the decoded position by more than the packet, the trim makes the packet's end index fall below its start
/// index and <c>Read</c> spins forever without asking for another packet. Kit therefore feeds NVorbis through a packet
/// provider that never reports end-of-stream (NVorbis never trims), counts and checks every packet it hands out, and trims
/// the decoded PCM to the container's final granule itself. Without the trim branch every pass of the read loop either
/// copies samples or pulls the next packet, so decoding is bounded by the file's packet count.</summary>
internal static class VorbisDecode
{
    /// <summary>Allowed disagreement between decoded frames and the Ogg final granule: two maximum Vorbis blocks.
    /// Up to this many frames past the granule are codec padding and are dropped; a larger gap is a corrupt file.</summary>
    internal const int ToleranceFrames = 16384;
    private const int ChunkFrames = 16384;

    /// <summary>Input has passed <see cref="AssetValidation.Ogg"/>. No Unity access. Every failure is an
    /// <see cref="InvalidDataException"/> (or <see cref="OperationCanceledException"/>) and fails this clip only.</summary>
    internal static DecodedAudio Decode(byte[] bytes, OggInfo info, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        try { return DecodeChecked(bytes, info, cancellation); }
        catch (Exception e) when (e is not (InvalidDataException or OperationCanceledException))
        {
            throw new InvalidDataException("Vorbis decoder rejected the stream: " + e.GetType().Name + ": " + e.Message, e);
        }
    }

    private static DecodedAudio DecodeChecked(byte[] bytes, OggInfo info, CancellationToken cancellation)
    {
        using var container = new ContainerReader(new MemoryStream(bytes, writable: false), closeOnDispose: true);
        IPacketProvider? stream = null;
        container.NewStreamCallback = provider => { stream ??= provider; return true; };
        if (!container.TryInit() || stream == null) throw new InvalidDataException("No Vorbis stream in the Ogg container.");
        // Every Ogg packet ends at a lacing value, so a file cannot hold more packets than bytes.
        var packets = new BoundedPackets(stream, bytes.Length + 16, cancellation);
        using var decoder = new StreamDecoder(packets) { ClipSamples = true };
        if (decoder.Channels != info.Channels || decoder.SampleRate != info.SampleRate)
            throw new InvalidDataException("Vorbis header disagrees with the Ogg identification page.");
        var channels = info.Channels;
        var capacity = checked((int)((info.Frames + ToleranceFrames) * channels));
        var samples = new float[capacity];
        var count = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var room = Math.Min(ChunkFrames * channels, capacity - count);
            if (room == 0)
            {
                // Buffer holds granule + tolerance frames; any further sample means the file is not what its pages claim.
                if (decoder.Read(new float[channels], 0, channels) != 0)
                    throw new InvalidDataException("Vorbis audio runs more than " + ToleranceFrames + " frames past its declared end.");
                break;
            }
            var read = decoder.Read(samples, count, room);
            if (read == 0) break;
            count += read;
        }
        var frames = count / channels;
        if (frames <= 0 || info.Frames - frames > ToleranceFrames)
            throw new InvalidDataException("Vorbis audio ends " + (info.Frames - frames) + " frames before its declared end.");
        // Frames past the final granule are encoder padding.
        Array.Resize(ref samples, (int)Math.Min(frames, info.Frames) * channels);
        for (var i = 0; i < samples.Length; i++)
            if (!float.IsFinite(samples[i])) throw new InvalidDataException("Vorbis decoder produced non-finite samples.");
        return new DecodedAudio(samples, channels, info.SampleRate);
    }

    /// <summary>Hands NVorbis the container's packets with end-of-stream hidden, observing cancellation and a hard packet
    /// count. Header peeks are not counted.</summary>
    private sealed class BoundedPackets : IPacketProvider
    {
        private readonly IPacketProvider _inner;
        private readonly CancellationToken _cancellation;
        private readonly int _limit;
        private int _served;
        internal BoundedPackets(IPacketProvider inner, int limit, CancellationToken cancellation)
        { _inner = inner; _limit = limit; _cancellation = cancellation; }
        public bool CanSeek => false;
        public int StreamSerial => _inner.StreamSerial;
        public IPacket? GetNextPacket()
        {
            _cancellation.ThrowIfCancellationRequested();
            if (++_served > _limit) throw new InvalidDataException("Vorbis stream yields more packets than the file can hold.");
            var packet = _inner.GetNextPacket();
            return packet == null ? null : new Packet(packet);
        }
        public IPacket? PeekNextPacket()
        {
            var packet = _inner.PeekNextPacket();
            return packet == null ? null : new Packet(packet);
        }
        public long SeekTo(long granulePos, int preRoll, GetPacketGranuleCount getPacketGranuleCount)
            => throw new NotSupportedException("Kit decodes whole clips without seeking.");
        public long GetGranuleCount() => _inner.GetGranuleCount();
    }

    private sealed class Packet : IPacket
    {
        private readonly IPacket _inner;
        internal Packet(IPacket inner) { _inner = inner; }
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

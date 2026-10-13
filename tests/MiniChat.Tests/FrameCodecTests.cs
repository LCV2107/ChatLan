using System.Buffers.Binary;
using MiniChat.Core.Protocol;

namespace MiniChat.Tests;

public class FrameCodecTests
{
    /// <summary>Simula un transporte que entrega los datos de uno en uno (fragmentación extrema).</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= data.Length) return 0;
            buffer[offset] = data[_pos++];
            return 1;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= data.Length || buffer.Length == 0) return ValueTask.FromResult(0);
            buffer.Span[0] = data[_pos++];
            return ValueTask.FromResult(1);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }

    private readonly FrameCodec _codec = new(1536 * 1024, 16 * 1024);

    private async Task<byte[]> EncodeAsync(params (PacketBase P, byte[] Body)[] frames)
    {
        var ms = new MemoryStream();
        foreach (var (p, b) in frames) await _codec.WriteAsync(ms, p, b, CancellationToken.None);
        return ms.ToArray();
    }

    [Fact]
    public async Task ReadsFramesDeliveredOneByteAtATime_IncludingBinaryBody()
    {
        var body = Enumerable.Range(0, 3000).Select(i => (byte)i).ToArray();
        var mid = Guid.NewGuid().ToString("N");
        var bytes = await EncodeAsync(
            (new MessagePacket { MessageId = mid, Text = "Ünïcödé 🎉 \n línea 2" }, Array.Empty<byte>()),
            (new PingPacket(), body));
        var stream = new TrickleStream(bytes);

        var f1 = await _codec.ReadAsync(stream, CancellationToken.None);
        var m = Assert.IsType<MessagePacket>(f1!.Packet);
        Assert.Equal("Ünïcödé 🎉 \n línea 2", m.Text);
        Assert.Equal(mid, m.MessageId);
        var f2 = await _codec.ReadAsync(stream, CancellationToken.None);
        Assert.IsType<PingPacket>(f2!.Packet);
        Assert.Equal(body, f2.Body.ToArray());
        Assert.Null(await _codec.ReadAsync(stream, CancellationToken.None)); // cierre limpio
    }

    [Fact]
    public async Task TruncatedFrame_ThrowsEndOfStream_NotGarbage()
    {
        var bytes = await EncodeAsync((new PingPacket(), new byte[100]));
        var cut = bytes[..^10];
        await Assert.ThrowsAsync<EndOfStreamException>(() => _codec.ReadAsync(new TrickleStream(cut), CancellationToken.None));
        await Assert.ThrowsAsync<EndOfStreamException>(() => _codec.ReadAsync(new TrickleStream(bytes[..2]), CancellationToken.None));
    }

    [Fact]
    public async Task InvalidLengths_AreRejected()
    {
        var tooBig = new byte[8];
        BinaryPrimitives.WriteUInt32BigEndian(tooBig, 50_000_000);
        await Assert.ThrowsAsync<ProtocolException>(() => _codec.ReadAsync(new MemoryStream(tooBig), CancellationToken.None));

        var badHeader = new byte[4 + 10];
        BinaryPrimitives.WriteUInt32BigEndian(badHeader, 10);
        BinaryPrimitives.WriteUInt16BigEndian(badHeader.AsSpan(4), 500); // cabecera mayor que la trama
        await Assert.ThrowsAsync<FrameFormatException>(() => _codec.ReadAsync(new MemoryStream(badHeader), CancellationToken.None));
    }

    [Fact]
    public void VersionNegotiation()
    {
        Assert.Equal(1, ProtocolInfo.Negotiate(1, 1));      // par v1: se habla v1 (sin documentos)
        Assert.Equal(2, ProtocolInfo.Negotiate(1, 5));      // par más nuevo: la más alta común
        Assert.Equal(2, ProtocolInfo.Negotiate(2, 2));
        Assert.Null(ProtocolInfo.Negotiate(3, 5));          // par que ya no admite v1 ni v2
        Assert.Equal(2, ProtocolInfo.Negotiate(1, 3, localMin: 1, localMax: 2));
    }
}

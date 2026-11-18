// Binary wire protocol (SPEC.md section 5), identical to agent/protocol.py.
// All integers are little-endian.
using System.Buffers.Binary;
using System.Text;

namespace TouchpadAgent.Core;

public enum MsgKind { Move, Scroll, Button, Key, Text, Ping, Zoom }

/// <summary>A parsed client frame. Which fields are meaningful depends on <see cref="Kind"/>.</summary>
public readonly record struct Message(MsgKind Kind, int A = 0, int B = 0, string? Text = null, uint Token = 0);

public static class Protocol
{
    public const int Version = 1;
    public const byte TMove = 0x01, TScroll = 0x02, TButton = 0x03, TKey = 0x04, TText = 0x05, TPing = 0x06, TZoom = 0x07;
    public const byte TPong = 0x81;
    public const int MaxTextBytes = 4096;

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Parse a binary client frame; null when malformed or of an unknown type (callers ignore those).</summary>
    public static Message? Parse(ReadOnlySpan<byte> d)
    {
        if (d.Length == 0) return null;
        byte t = d[0];
        int n = d.Length;
        switch (t)
        {
            case TMove:
            case TScroll:
                if (n != 7) return null;
                return new Message(t == TMove ? MsgKind.Move : MsgKind.Scroll,
                    BinaryPrimitives.ReadInt16LittleEndian(d[3..]), BinaryPrimitives.ReadInt16LittleEndian(d[5..]));
            case TButton:
                if (n != 3 || d[1] > 2 || d[2] > 1) return null;
                return new Message(MsgKind.Button, d[1], d[2]);
            case TKey:
                if (n != 4) return null;
                int vk = BinaryPrimitives.ReadUInt16LittleEndian(d[1..]);
                if (vk < 1 || vk > 254 || d[3] > 1) return null;
                return new Message(MsgKind.Key, vk, d[3]);
            case TText:
                if (n < 2 || n - 1 > MaxTextBytes) return null;
                try { return new Message(MsgKind.Text, Text: StrictUtf8.GetString(d[1..])); }
                catch (DecoderFallbackException) { return null; }
            case TPing:
                if (n != 5) return null;
                return new Message(MsgKind.Ping, Token: BinaryPrimitives.ReadUInt32LittleEndian(d[1..]));
            case TZoom:
                if (n != 3) return null;
                return new Message(MsgKind.Zoom, BinaryPrimitives.ReadInt16LittleEndian(d[1..]));
            default:
                return null;
        }
    }

    public static byte[] EncodePong(uint token)
    {
        var b = new byte[5];
        b[0] = TPong;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), token);
        return b;
    }

    // Encoders are used by tests (and could be used by a C# client).
    public static byte[] EncodeXy(byte type, ushort seq, short dx, short dy)
    {
        var b = new byte[7];
        b[0] = type;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), seq);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(3), dx);
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(5), dy);
        return b;
    }

    public static byte[] EncodeMove(ushort seq, short dx, short dy) => EncodeXy(TMove, seq, dx, dy);
    public static byte[] EncodeScroll(ushort seq, short dx, short dy) => EncodeXy(TScroll, seq, dx, dy);
    public static byte[] EncodeButton(byte button, byte state) => new[] { TButton, button, state };

    public static byte[] EncodeKey(ushort vk, byte state)
    {
        var b = new byte[4];
        b[0] = TKey;
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(1), vk);
        b[3] = state;
        return b;
    }

    public static byte[] EncodeText(string text)
    {
        var body = Encoding.UTF8.GetBytes(text);
        var b = new byte[body.Length + 1];
        b[0] = TText;
        body.CopyTo(b, 1);
        return b;
    }

    public static byte[] EncodeZoom(short delta)
    {
        var b = new byte[3];
        b[0] = TZoom;
        BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(1), delta);
        return b;
    }

    public static byte[] EncodePing(uint token)
    {
        var b = new byte[5];
        b[0] = TPing;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(1), token);
        return b;
    }
}

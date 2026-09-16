using System.Text;

namespace ITVCoffin.Proxy;

// packetType values (client: TPS.Net.PacketType)
public enum PacketType : byte
{
    Invalid = 0,
    Handshake = 1,
    HandshakeAck = 2,
    Heartbeat = 3,
    Data = 4,
    Kick = 5,
}

// contract type (client: Contract.type, 3 bits)
public enum ContractType : byte
{
    Request = 0,
    Response = 1,
    Notify = 2,
    Push = 3,
}

public struct Contract
{
    public ContractType Type;
    public uint Id;
    public string Route;
    public bool Error;
    public bool Compressed;
    public uint CompressRoute;
    public byte[] Data;
}

// Wire format as implemented by the client (TPS.Net.Socket.ContractEncode / ContractDecode):
//   frame  = [byte packetType][3-byte big-endian payload length][payload]
//   payload(Data) = [flags byte]
//                   [id: LEB128 varint]  -- only when type == Request(0) or Notify(2)
//                   [route: 1-byte len + ASCII] -- only when type == Request(0)/Response(1)/Push(3)
//                   [protobuf data]
//   flags = (type << 1) | (compressed ? 1 : 0) | (error ? 0x20 : 0)
public static class Wire
{
    public const int MaxMessageSize = 16777215; // 0xFFFFFF

    public static byte[] BuildFrame(PacketType type, byte[]? payload)
    {
        payload ??= Array.Empty<byte>();
        var frame = new byte[4 + payload.Length];
        frame[0] = (byte)type;
        frame[1] = (byte)((payload.Length >> 16) & 0xFF);
        frame[2] = (byte)((payload.Length >> 8) & 0xFF);
        frame[3] = (byte)(payload.Length & 0xFF);
        Buffer.BlockCopy(payload, 0, frame, 4, payload.Length);
        return frame;
    }

    public static bool TryReadFrame(Stream stream, out PacketType type, out byte[] payload)
    {
        type = PacketType.Invalid;
        payload = Array.Empty<byte>();
        var header = new byte[4];
        if (!ReadExactly(stream, header, 4))
            return false;
        type = (PacketType)header[0];
        int len = (header[1] << 16) | (header[2] << 8) | header[3];
        if (len < 0 || len > MaxMessageSize)
            return false;
        payload = new byte[len];
        if (len > 0 && !ReadExactly(stream, payload, len))
            return false;
        return true;
    }

    private static bool ReadExactly(Stream stream, byte[] buf, int count)
    {
        int off = 0;
        while (off < count)
        {
            int n = stream.Read(buf, off, count - off);
            if (n <= 0)
                return false;
            off += n;
        }
        return true;
    }

    public static byte[] EncodeContract(Contract c)
    {
        var buf = new List<byte>(64);
        byte flags = (byte)((byte)c.Type << 1);
        if (c.Compressed) flags |= 1;
        if (c.Error) flags |= 0x20;
        buf.Add(flags);

        if (c.Type == ContractType.Request || c.Type == ContractType.Notify)
            WriteVarint(buf, c.Id);

        if (c.Type == ContractType.Request || c.Type == ContractType.Response || c.Type == ContractType.Push)
        {
            if (c.Compressed)
            {
                buf.Add((byte)(c.CompressRoute >> 8));
                buf.Add((byte)c.CompressRoute);
            }
            else
            {
                var route = Encoding.ASCII.GetBytes(c.Route ?? string.Empty);
                buf.Add((byte)route.Length);
                buf.AddRange(route);
            }
        }

        if (c.Data != null)
            buf.AddRange(c.Data);
        return buf.ToArray();
    }

    public static Contract? DecodeContract(byte[] data)
    {
        if (data.Length < 2)
            return null;
        var c = new Contract();
        byte b = data[0];
        int pos = 1;
        c.Type = (ContractType)((b >> 1) & 7);
        if ((int)c.Type > 3)
            return null;

        if (c.Type == ContractType.Request || c.Type == ContractType.Notify)
            c.Id = ReadVarint(data, ref pos);

        c.Error = (b & 0x20) == 0x20;

        if (c.Type == ContractType.Request || c.Type == ContractType.Response || c.Type == ContractType.Push)
        {
            if ((b & 1) == 1)
            {
                c.Compressed = true;
                c.CompressRoute = (uint)((data[pos] << 8) | data[pos + 1]);
                pos += 2;
            }
            else
            {
                int rlen = data[pos++];
                c.Route = Encoding.ASCII.GetString(data, pos, rlen);
                pos += rlen;
            }
        }

        c.Data = data[pos..];
        return c;
    }

    public static void WriteVarint(List<byte> buf, uint value)
    {
        while (value >= 128)
        {
            buf.Add((byte)((value & 127) | 128));
            value >>= 7;
        }
        buf.Add((byte)value);
    }

    public static uint ReadVarint(byte[] data, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (uint)(b & 127) << shift;
            if (b < 128)
                break;
            shift += 7;
        }
        return result;
    }
}

using System.Security.Cryptography;
using System.Text;

namespace DownloadBot.QBittorrent;

// A .torrent file's info hash is the SHA-1 of the exact bencoded bytes of its "info" dict value —
// not a re-serialization, since key ordering/formatting must match byte-for-byte. This is a minimal
// bencode "skip" parser: it tracks byte offsets well enough to slice out that raw span without needing
// a full decode into objects.
public static class TorrentInfoHash
{
    public static string? TryCompute(byte[] torrentBytes)
    {
        try
        {
            var pos = 0;
            if (torrentBytes.Length == 0 || torrentBytes[pos] != (byte)'d')
                return null;

            pos++;
            while (pos < torrentBytes.Length && torrentBytes[pos] != (byte)'e')
            {
                var key = ReadString(torrentBytes, ref pos);
                if (key == "info")
                {
                    var infoStart = pos;
                    SkipValue(torrentBytes, ref pos);
                    var infoBytes = torrentBytes[infoStart..pos];
                    return Convert.ToHexStringLower(SHA1.HashData(infoBytes));
                }

                SkipValue(torrentBytes, ref pos);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(byte[] data, ref int pos)
    {
        var colon = Array.IndexOf(data, (byte)':', pos);
        var len = int.Parse(Encoding.ASCII.GetString(data, pos, colon - pos));
        pos = colon + 1;
        var value = Encoding.UTF8.GetString(data, pos, len);
        pos += len;
        return value;
    }

    private static void SkipValue(byte[] data, ref int pos)
    {
        switch ((char)data[pos])
        {
            case 'i':
                pos = Array.IndexOf(data, (byte)'e', pos) + 1;
                break;
            case 'l':
                pos++;
                while (data[pos] != (byte)'e')
                    SkipValue(data, ref pos);
                pos++;
                break;
            case 'd':
                pos++;
                while (data[pos] != (byte)'e')
                {
                    ReadString(data, ref pos);
                    SkipValue(data, ref pos);
                }
                pos++;
                break;
            default:
                var colon = Array.IndexOf(data, (byte)':', pos);
                var len = int.Parse(Encoding.ASCII.GetString(data, pos, colon - pos));
                pos = colon + len + 1;
                break;
        }
    }
}

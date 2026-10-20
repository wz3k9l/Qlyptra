using System.Text;

namespace Qlyptra.Services;

public static class Id3Writer
{
    public static void CreateTaggedCopy(string sourcePath, string destinationPath, string title, string artist, byte[]? coverBytes)
    {
        if (!File.Exists(sourcePath)) throw new FileNotFoundException("Исходный MP3-файл не найден.", sourcePath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var frames = new List<byte[]>();
        var hasNewCover = coverBytes is { Length: > 100 and < 12_000_000 } && IsPngOrJpeg(coverBytes);
        if (!string.IsNullOrWhiteSpace(title)) frames.Add(TextFrame("TIT2", title.Trim()));
        if (!string.IsNullOrWhiteSpace(artist)) frames.Add(TextFrame("TPE1", artist.Trim()));
        if (hasNewCover) frames.Add(PictureFrame(coverBytes!));

        // Keep useful original frames (including the embedded cover) whenever Qlyptra
        // does not have a replacement value from the Yandex page.
        foreach (var existingFrame in ReadExistingId3Frames(sourcePath))
        {
            var id = Encoding.ASCII.GetString(existingFrame, 0, 4);
            if (id == "TIT2" && !string.IsNullOrWhiteSpace(title)) continue;
            if (id == "TPE1" && !string.IsNullOrWhiteSpace(artist)) continue;
            if (id == "APIC" && hasNewCover) continue;
            frames.Add(existingFrame);
        }
        var payloadLength = frames.Sum(f => f.Length);
        using var output = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        if (payloadLength > 0)
        {
            output.Write(new byte[] { (byte)'I', (byte)'D', (byte)'3', 3, 0, 0 });
            output.Write(Synchsafe(payloadLength));
            foreach (var frame in frames) output.Write(frame);
        }

        using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        long audioOffset = 0;
        var header = new byte[10];
        if (input.Read(header, 0, header.Length) == header.Length && header[0] == 'I' && header[1] == 'D' && header[2] == '3')
        {
            var tagSize = ((header[6] & 0x7F) << 21) | ((header[7] & 0x7F) << 14) | ((header[8] & 0x7F) << 7) | (header[9] & 0x7F);
            audioOffset = 10L + tagSize + ((header[5] & 0x10) != 0 ? 10 : 0);
            if (audioOffset > input.Length) audioOffset = 0;
        }
        input.Position = audioOffset;
        input.CopyTo(output);
        output.Flush(true);
    }

    private static List<byte[]> ReadExistingId3Frames(string sourcePath)
    {
        var frames = new List<byte[]>();
        try
        {
            using var input = File.OpenRead(sourcePath);
            var header = new byte[10];
            if (input.Read(header, 0, header.Length) != header.Length || header[0] != 'I' || header[1] != 'D' || header[2] != '3') return frames;
            var version = header[3];
            if (version is not (3 or 4) || (header[5] & 0x80) != 0) return frames;
            var tagSize = DecodeSynchsafe(header, 6);
            if (tagSize <= 0 || tagSize > 16 * 1024 * 1024 || tagSize > input.Length - 10) return frames;
            var tag = new byte[tagSize];
            if (input.Read(tag, 0, tag.Length) != tag.Length) return frames;
            var offset = 0;
            if ((header[5] & 0x40) != 0)
            {
                if (tag.Length < 4) return frames;
                var extSize = version == 3
                    ? (tag[0] << 24) | (tag[1] << 16) | (tag[2] << 8) | tag[3]
                    : DecodeSynchsafe(tag, 0);
                offset = extSize + (version == 3 ? 4 : 0);
                if (offset < 0 || offset > tag.Length) return frames;
            }
            while (offset + 10 <= tag.Length)
            {
                if (tag[offset] == 0) break;
                var id = Encoding.ASCII.GetString(tag, offset, 4);
                if (id.Any(ch => !(ch is >= 'A' and <= 'Z' or >= '0' and <= '9'))) break;
                var size = version == 3
                    ? (tag[offset + 4] << 24) | (tag[offset + 5] << 16) | (tag[offset + 6] << 8) | tag[offset + 7]
                    : DecodeSynchsafe(tag, offset + 4);
                if (size < 0 || offset + 10L + size > tag.Length) break;
                var frame = new byte[10 + size];
                Buffer.BlockCopy(tag, offset, frame, 0, frame.Length);
                // Rebuild the raw frame header as ID3v2.3 because Qlyptra writes a v2.3 tag.
                if (version == 4)
                {
                    frame[4] = (byte)((size >> 24) & 0xFF);
                    frame[5] = (byte)((size >> 16) & 0xFF);
                    frame[6] = (byte)((size >> 8) & 0xFF);
                    frame[7] = (byte)(size & 0xFF);
                }
                frames.Add(frame);
                offset += frame.Length;
            }
        }
        catch { }
        return frames;
    }

    private static int DecodeSynchsafe(byte[] bytes, int offset) =>
        ((bytes[offset] & 0x7F) << 21) | ((bytes[offset + 1] & 0x7F) << 14) |
        ((bytes[offset + 2] & 0x7F) << 7) | (bytes[offset + 3] & 0x7F);

    private static byte[] TextFrame(string id, string text)
    {
        var encoded = Encoding.Unicode.GetBytes(text);
        var data = new byte[3 + encoded.Length + 2];
        data[0] = 1; data[1] = 0xFF; data[2] = 0xFE;
        Buffer.BlockCopy(encoded, 0, data, 3, encoded.Length);
        return MakeFrame(id, data);
    }

    private static bool IsPngOrJpeg(byte[] image)
    {
        var png = image.Length >= 8 && image[0] == 0x89 && image[1] == 0x50 && image[2] == 0x4E && image[3] == 0x47;
        var jpeg = image.Length >= 3 && image[0] == 0xFF && image[1] == 0xD8 && image[2] == 0xFF;
        return png || jpeg;
    }

    private static byte[] PictureFrame(byte[] image)
    {
        var mime = IsPngOrJpeg(image) && image.Length > 8 && image[0] == 0x89 && image[1] == 0x50 ? "image/png" : "image/jpeg";
        var mimeBytes = Encoding.ASCII.GetBytes(mime);
        using var ms = new MemoryStream();
        ms.WriteByte(0); // ISO-8859-1 encoding for MIME and empty description.
        ms.Write(mimeBytes); ms.WriteByte(0);
        ms.WriteByte(3); // Front cover.
        ms.WriteByte(0); // Empty description.
        ms.Write(image);
        return MakeFrame("APIC", ms.ToArray());
    }

    private static byte[] MakeFrame(string id, byte[] data)
    {
        var output = new byte[10 + data.Length];
        Encoding.ASCII.GetBytes(id).CopyTo(output, 0);
        var size = data.Length;
        output[4] = (byte)((size >> 24) & 0xFF);
        output[5] = (byte)((size >> 16) & 0xFF);
        output[6] = (byte)((size >> 8) & 0xFF);
        output[7] = (byte)(size & 0xFF);
        Buffer.BlockCopy(data, 0, output, 10, data.Length);
        return output;
    }

    private static byte[] Synchsafe(int value) => new[]
    {
        (byte)((value >> 21) & 0x7F), (byte)((value >> 14) & 0x7F), (byte)((value >> 7) & 0x7F), (byte)(value & 0x7F)
    };
}

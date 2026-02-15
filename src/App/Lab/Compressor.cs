using ProtoBuf;
using System.Buffers.Text;
using System.IO.Compression;

namespace DotNetLab.Lab;

internal static class Compressor
{
    public static string Compress(SavedState input)
    {
        using var ms = new MemoryStream();
        using (var compressor = new DeflateStream(ms, CompressionLevel.Optimal))
        {
            Serializer.Serialize(compressor, input);
        }
        return Base64Url.EncodeToString(ms.ToArray());
    }

    public static SavedState Uncompress(string slug)
    {
        if (TryUncompress(slug, out var state, out var exception))
        {
            return state;
        }

        return new SavedState
        {
            Inputs =
            [
                new InputCode { FileName = "(error)", Text = $"Error when parsing '{slug}':\n{exception}" },
            ],
        };
    }

    public static bool TryUncompress(
        string slug,
        [NotNullWhen(true)] out SavedState? state,
        [NotNullWhen(false)] out Exception? exception)
    {
        try
        {
            var bytes = Base64Url.DecodeFromChars(slug);
            using var ms = new MemoryStream(bytes);
            using var compressor = new DeflateStream(ms, CompressionMode.Decompress);
            state = Serializer.Deserialize<SavedState>(compressor);
            exception = null;
            return true;
        }
        catch (Exception ex)
        {
            state = null;
            exception = ex;
            return false;
        }
    }
}

using Knapcode.MiniZip;
using System.IO.Compression;

namespace DotNetLab;

internal static class ZipUtil
{
    extension(ZipDirectoryReader reader)
    {
        public async Task<byte[]> ReadFileDataAsync(ZipDirectory directory, CentralDirectoryHeader entry)
        {
            if (entry.CompressionMethod == (ushort)ZipCompressionMethod.Store)
            {
                return await reader.ReadUncompressedFileDataAsync(directory, entry);
            }

            var localEntry = await reader.ReadLocalFileHeaderAsync(directory, entry);
            await using var decompressStream = new DeflateStream(reader.Stream, CompressionMode.Decompress, leaveOpen: true);
            var buffer = new byte[localEntry.UncompressedSize];
            await decompressStream.ReadExactlyAsync(buffer);
            return buffer;
        }
    }
}

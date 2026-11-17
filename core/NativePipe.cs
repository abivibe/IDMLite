using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace IDMLite.Core;

internal static class NativePipe
{
    internal const PipeOptions Options = PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly;
    internal const int MaxMessageSize = 1024 * 1024;

    internal static string Name(string token) =>
        "IDMLite-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    internal static async Task<string> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        int length = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length <= 0 || length > MaxMessageSize) throw new InvalidDataException("Invalid native message size.");
        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(body);
    }

    internal static async Task WriteAsync(Stream stream, string message, CancellationToken ct)
    {
        byte[] body = Encoding.UTF8.GetBytes(message);
        if (body.Length == 0 || body.Length > MaxMessageSize) throw new InvalidDataException("Invalid native message size.");
        byte[] header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, ct).ConfigureAwait(false);
        await stream.WriteAsync(body, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
    }
}

using System.Security.Cryptography;

namespace IDMLite.Core;

public static class ChecksumHelper
{
    public static async Task<string> ComputeHashAsync(
        string filePath,
        string algorithm = "SHA256",
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found for checksum calculation", filePath);

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, true);
        long totalLength = stream.Length;

        using var hasher = algorithm.ToUpperInvariant() switch
        {
            "MD5" => IncrementalHash.CreateHash(HashAlgorithmName.MD5),
            "SHA1" => IncrementalHash.CreateHash(HashAlgorithmName.SHA1),
            "SHA256" => IncrementalHash.CreateHash(HashAlgorithmName.SHA256),
            "SHA512" => IncrementalHash.CreateHash(HashAlgorithmName.SHA512),
            _ => IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
        };

        byte[] buffer = new byte[128 * 1024];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false)) > 0)
        {
            hasher.AppendData(buffer, 0, bytesRead);
            totalRead += bytesRead;
            if (totalLength > 0 && progress != null)
            {
                progress.Report(Math.Clamp((double)totalRead / totalLength * 100.0, 0.0, 100.0));
            }
        }

        progress?.Report(100.0);

        byte[] hash = hasher.GetHashAndReset();
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task<bool> VerifyAsync(string filePath, string expectedHash, string algorithm = "SHA256", CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(expectedHash))
            return true;

        string actual = await ComputeHashAsync(filePath, algorithm, null, ct).ConfigureAwait(false);
        return string.Equals(actual.Trim(), expectedHash.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}

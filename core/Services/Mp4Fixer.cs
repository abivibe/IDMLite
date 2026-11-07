using System.Buffers.Binary;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace IDMLite.Core.Services;

/// <summary>
/// MP4 dosyasının başlığındaki 'moov -> mvhd' ve 'trak -> tkhd' duration (süre) bilgisini kontrol eder ve eksikse (0 ise) onarır.
/// KaraDelik2 MP4 fixer modülünden optimize edilmiştir.
/// </summary>
public static class Mp4Fixer
{
    public static async Task<bool> FixDurationIfNeededAsync(string filePath, TimeSpan? expectedDuration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            var fileInfo = new FileInfo(filePath);
            if (fileInfo.Length < 64)
                return false;

            byte[] bytes = await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
            if (bytes.Length < 64)
                return false;

            int offset = 0;
            int moovOffset = -1;
            int moovSize = 0;

            // 1. 'moov' atomunu bul
            while (offset + 8 <= bytes.Length)
            {
                uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);

                if (type == "moov")
                {
                    moovOffset = offset;
                    moovSize = (int)size;
                    break;
                }

                if (size <= 0) break;
                offset += (int)size;
            }

            if (moovOffset == -1) return false;

            int moovEnd = moovOffset + moovSize;
            if (moovEnd > bytes.Length) return false;

            // 2. 'mvhd' atomunu bul
            int subOffset = moovOffset + 8;
            int mvhdOffset = -1;
            int mvhdSize = 0;

            while (subOffset + 8 <= moovEnd)
            {
                uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(subOffset, 4));
                string type = Encoding.ASCII.GetString(bytes, subOffset + 4, 4);

                if (type == "mvhd")
                {
                    mvhdOffset = subOffset;
                    mvhdSize = (int)size;
                    break;
                }

                if (size <= 0) break;
                subOffset += (int)size;
            }

            if (mvhdOffset == -1) return false;

            byte version = bytes[mvhdOffset + 8];
            int timescale;
            ulong currentDuration;
            int durFieldOffsetInMvhd;
            bool is64Bit;

            if (version == 1)
            {
                timescale = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mvhdOffset + 8 + 20, 4));
                currentDuration = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(mvhdOffset + 8 + 24, 8));
                durFieldOffsetInMvhd = mvhdOffset + 8 + 24;
                is64Bit = true;
            }
            else
            {
                timescale = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mvhdOffset + 8 + 12, 4));
                currentDuration = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(mvhdOffset + 8 + 16, 4));
                durFieldOffsetInMvhd = mvhdOffset + 8 + 16;
                is64Bit = false;
            }

            // Duration zaten geçerli (0 değil) ise dosyaya dokunma
            if (currentDuration > 0)
            {
                return true;
            }

            int targetSeconds = expectedDuration.HasValue && expectedDuration.Value.TotalSeconds > 0
                ? (int)expectedDuration.Value.TotalSeconds
                : 0;

            // Beklenen süre verilmediyse mfra veya tfra fragmanlarından tahmin et
            if (targetSeconds <= 0)
            {
                targetSeconds = EstimateDurationFromMfra(bytes, timescale);
            }

            // ffprobe ile son çare kontrol
            if (targetSeconds <= 0)
            {
                targetSeconds = await ProbeDurationWithFfmpegAsync(filePath, cancellationToken).ConfigureAwait(false);
            }

            if (targetSeconds <= 0) return false;

            ulong targetDurValue = (ulong)targetSeconds * (ulong)(timescale > 0 ? timescale : 1000);

            // mvhd süresini güncelle
            if (is64Bit)
            {
                BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(durFieldOffsetInMvhd, 8), targetDurValue);
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(durFieldOffsetInMvhd, 4), (uint)targetDurValue);
            }

            // trak -> tkhd bloklarını da güncelle
            PatchAllTkhd(bytes, moovOffset + 8, moovEnd, targetDurValue, is64Bit);

            // Dosyaya geri yaz
            await File.WriteAllBytesAsync(filePath, bytes, cancellationToken).ConfigureAwait(false);
            Debug.WriteLine($"MP4 duration başarıyla onarıldı: {targetSeconds} saniye");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MP4 duration onarım hatası: {ex.Message}");
            return false;
        }
    }

    private static void PatchAllTkhd(byte[] bytes, int moovStart, int moovEnd, ulong durationValue, bool is64Bit)
    {
        int offset = moovStart;
        while (offset + 8 <= moovEnd)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
            string type = Encoding.ASCII.GetString(bytes, offset + 4, 4);

            if (type == "trak")
            {
                int trakSub = offset + 8;
                int trakEnd = offset + (int)size;
                while (trakSub + 8 <= trakEnd && trakSub + 8 <= bytes.Length)
                {
                    uint tSize = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(trakSub, 4));
                    string tType = Encoding.ASCII.GetString(bytes, trakSub + 4, 4);

                    if (tType == "tkhd")
                    {
                        byte version = bytes[trakSub + 8];
                        int durOffset = trakSub + 8 + (version == 1 ? 24 : 16);
                        if (durOffset + (version == 1 ? 8 : 4) <= bytes.Length)
                        {
                            if (version == 1)
                            {
                                BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(durOffset, 8), durationValue);
                            }
                            else
                            {
                                BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(durOffset, 4), (uint)durationValue);
                            }
                        }
                        break;
                    }

                    if (tSize <= 0) break;
                    trakSub += (int)tSize;
                }
            }

            if (size <= 0) break;
            offset += (int)size;
        }
    }

    private static int EstimateDurationFromMfra(byte[] bytes, int timescale)
    {
        try
        {
            int fileLength = bytes.Length;
            int start = fileLength > 4096 ? fileLength - 4096 : 0;
            for (int i = start; i < fileLength - 16; i++)
            {
                // 'tfra' (0x74, 0x66, 0x72, 0x61)
                if (bytes[i] == 0x74 && bytes[i + 1] == 0x66 && bytes[i + 2] == 0x72 && bytes[i + 3] == 0x61)
                {
                    byte version = bytes[i + 4];
                    if (version == 1 && i + 28 < fileLength)
                    {
                        ulong lastTime = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(i + 20, 8));
                        if (lastTime > 0 && timescale > 0)
                        {
                            return (int)Math.Ceiling((double)lastTime / timescale);
                        }
                    }
                    else if (version == 0 && i + 24 < fileLength)
                    {
                        uint lastTime = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 16, 4));
                        if (lastTime > 0 && timescale > 0)
                        {
                            return (int)Math.Ceiling((double)lastTime / timescale);
                        }
                    }
                }
            }
        }
        catch { }
        return 0;
    }

    private static async Task<int> ProbeDurationWithFfmpegAsync(string filePath, CancellationToken ct)
    {
        try
        {
            string? ffmpeg = YtDlpHelper.FindFfmpegPath();
            if (string.IsNullOrEmpty(ffmpeg)) return 0;

            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-i \"{filePath}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return 0;

            string err = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);

            var m = System.Text.RegularExpressions.Regex.Match(err, @"Duration:\s*(\d{2}):(\d{2}):(\d{2}(?:\.\d+)?)");
            if (m.Success)
            {
                int hours = int.Parse(m.Groups[1].Value);
                int mins = int.Parse(m.Groups[2].Value);
                double secs = double.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture);
                return (int)Math.Ceiling(hours * 3600 + mins * 60 + secs);
            }
        }
        catch { }
        return 0;
    }
}

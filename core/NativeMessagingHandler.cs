using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace IDMLite.Core;

public static class NativeMessagingHandler
{
    public static async Task RunAsync()
    {
        using Stream stdin = Console.OpenStandardInput();
        using Stream stdout = Console.OpenStandardOutput();

        byte[] lengthBuffer = new byte[4];

        while (true)
        {
            int bytesRead = 0;
            while (bytesRead < 4)
            {
                int read = await stdin.ReadAsync(lengthBuffer.AsMemory(bytesRead, 4 - bytesRead)).ConfigureAwait(false);
                if (read <= 0) return; // Browser closed connection
                bytesRead += read;
            }

            int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
            if (messageLength <= 0 || messageLength > 1024 * 1024)
            {
                return;
            }

            byte[] messageBuffer = new byte[messageLength];
            int totalMessageRead = 0;
            while (totalMessageRead < messageLength)
            {
                int read = await stdin.ReadAsync(messageBuffer.AsMemory(totalMessageRead, messageLength - totalMessageRead)).ConfigureAwait(false);
                if (read <= 0) return;
                totalMessageRead += read;
            }

            string jsonString = Encoding.UTF8.GetString(messageBuffer);
            string responseJson = await ProcessNativeMessageAsync(jsonString).ConfigureAwait(false);

            byte[] responseBytes = Encoding.UTF8.GetBytes(responseJson);
            byte[] responseLength = BitConverter.GetBytes(responseBytes.Length);

            await stdout.WriteAsync(responseLength).ConfigureAwait(false);
            await stdout.WriteAsync(responseBytes).ConfigureAwait(false);
            await stdout.FlushAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string> ProcessNativeMessageAsync(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string action = root.TryGetProperty("action", out var act) ? act.GetString() ?? "download" : "download";

            // Relay to local HTTP IPC
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
            var settings = new SettingsManager().Current;
            int port = settings.IpcPort;
            // Native host manifests restrict which browser extension can request this secret.
            if (string.Equals(action, "getAuth", StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(new { success = true, token = settings.IpcToken, port });
            client.DefaultRequestHeaders.Add("X-IDMLite-Token", settings.IpcToken);
            string endpoint = $"http://127.0.0.1:{port}";

            if (string.Equals(action, "checkStatus", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using var statusRes = await client.GetAsync(endpoint + "/api/status").ConfigureAwait(false);
                    if (statusRes.IsSuccessStatusCode)
                    {
                        return await statusRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
                catch { }
                return JsonSerializer.Serialize(new { success = false, online = false });
            }

            if (!string.Equals(action, "download", StringComparison.OrdinalIgnoreCase))
                return JsonSerializer.Serialize(new { success = false, error = "Unsupported native action" });

            try
            {
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var res = await client.PostAsync(endpoint + "/api/download", content).ConfigureAwait(false);
                string resBody = await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                return resBody;
            }
            catch
            {
                // If IDMLite GUI is not running, launch it in the background!
                string exePath = Process.GetCurrentProcess().MainModule?.FileName ?? "";
                if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
                {
                    try
                    {
                        Process.Start(new ProcessStartInfo
                        {
                            FileName = exePath,
                            Arguments = "--minimized",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        });
                    }
                    catch { }

                    // Wait a moment and retry
                    for (int attempt = 0; attempt < 3; attempt++)
                    {
                        await Task.Delay(700).ConfigureAwait(false);
                        try
                        {
                            if (string.Equals(action, "checkStatus", StringComparison.OrdinalIgnoreCase))
                            {
                                using var statusRes = await client.GetAsync(endpoint + "/api/status").ConfigureAwait(false);
                                if (statusRes.IsSuccessStatusCode)
                                {
                                    return await statusRes.Content.ReadAsStringAsync().ConfigureAwait(false);
                                }
                            }
                            else
                            {
                                using var retryContent = new StringContent(json, Encoding.UTF8, "application/json");
                                using var res = await client.PostAsync(endpoint + "/api/download", retryContent).ConfigureAwait(false);
                                return await res.Content.ReadAsStringAsync().ConfigureAwait(false);
                            }
                        }
                        catch { }
                    }
                }

                return JsonSerializer.Serialize(new { success = false, online = false, message = "Could not reach IDMLite IPC server." });
            }
        }
        catch (Exception)
        {
            return JsonSerializer.Serialize(new { success = false, online = false, error = "Invalid native message" });
        }
    }
}

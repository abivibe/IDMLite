using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace IDMLite.Core;

public static class NativeMessagingHandler
{
    public static async Task RunAsync()
    {
        using Stream stdin = Console.OpenStandardInput();
        using Stream stdout = Console.OpenStandardOutput();
        try
        {
            while (true)
            {
                string json = await NativePipe.ReadAsync(stdin, CancellationToken.None).ConfigureAwait(false);
                string response = await ProcessNativeMessageAsync(json).ConfigureAwait(false);
                await NativePipe.WriteAsync(stdout, response, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (IOException) { }
    }

    private static async Task<string> ProcessNativeMessageAsync(string json)
    {
        const string offline = """{"success":false,"online":false,"error":"IDMLite native bridge is unavailable."}""";
        try
        {
            using var doc = JsonDocument.Parse(json);
            string action = doc.RootElement.TryGetProperty("action", out var value) ? value.GetString() ?? "" : "download";
            if (action is not ("download" or "preview" or "checkStatus"))
                return """{"success":false,"error":"Unsupported native action."}""";

            string name = NativePipe.Name(new SettingsManager().Current.IpcToken);
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, NativePipe.Options);
            try { await pipe.ConnectAsync(500).ConfigureAwait(false); }
            catch (TimeoutException) when (action == "download")
            {
                if (Environment.ProcessPath is not string exe) return offline;
                using var started = Process.Start(new ProcessStartInfo(exe)
                {
                    Arguments = "--minimized", UseShellExecute = false, CreateNoWindow = true
                });
                await pipe.ConnectAsync(3000).ConfigureAwait(false);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(action == "preview" ? 49 : 8));
            await NativePipe.WriteAsync(pipe, json, timeout.Token).ConfigureAwait(false);
            return await NativePipe.ReadAsync(pipe, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or TimeoutException or
            OperationCanceledException or JsonException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return offline;
        }
    }
}

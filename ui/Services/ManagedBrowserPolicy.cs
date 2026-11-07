using System.IO;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace IDMLite.Services;

internal static class ManagedBrowserPolicy
{
    private const string OwnerKey = @"Software\IDMLite\ManagedBrowserPolicy";
    private const string FirefoxKey = @"Software\Policies\Mozilla\Firefox";
    private const string FirefoxId = "idmlite@extension";
    private const string FirefoxValue = "ExtensionSettings";
    private const string ChromiumId = "gdcnkipjepgidojhonbcfpgagchjcdci";
    private const string ChromeStore = "https://clients2.google.com/service/update2/crx";

    public static bool CanApply(string name) => name is "Chrome" or "Brave" or "Firefox";

    public static int Run(string[] args)
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            if (args is ["--remove-idmlite-browser-policies"])
            {
                using var owner = machine.OpenSubKey(OwnerKey, writable: true);
                if (owner is not null)
                    foreach (string browser in new[] { "Chrome", "Brave", "Firefox" }) Remove(machine, owner, browser);
                return 0;
            }
            if (args is ["--apply-idmlite-browser-policy", var name, var address] && CanApply(name))
            {
                _ = BrowserExtensionSetup.CreatePolicy(name, address);
                var url = new Uri(address).AbsoluteUri;
                if (name != "Firefox" && url != ChromeStore && !IsDomainJoined()) return 4;
                using var owner = machine.CreateSubKey(OwnerKey);
                return name == "Firefox" ? ApplyFirefox(machine, owner, url) : ApplyChromium(machine, owner, name, url);
            }
            return 2;
        }
        catch { return 3; }
    }

    private static string ChromiumPath(string name) => name switch
    {
        "Chrome" => @"Software\Policies\Google\Chrome\ExtensionInstallForcelist",
        "Brave" => @"Software\Policies\BraveSoftware\Brave\ExtensionInstallForcelist",
        _ => throw new ArgumentException()
    };

    private static int ApplyChromium(RegistryKey machine, RegistryKey owner, string name, string address)
    {
        using var list = machine.CreateSubKey(ChromiumPath(name));
        string[] owned = owner.GetValue(name) as string[] ?? [];
        string? match = list.GetValueNames().FirstOrDefault(slot =>
            (list.GetValue(slot) as string)?.Split(';', 2)[0] == ChromiumId);
        if (match is not null && (owned.Length != 2 || match != owned[0] ||
            !Equals(list.GetValue(match), owned[1]))) return 5;

        if (match is null)
        {
            var names = list.GetValueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
            // Append to an existing policy list, leaving its entries intact.
            for (int i = 1; i <= 10000 && match is null; i++)
            {
                string candidate = i.ToString(CultureInfo.InvariantCulture);
                if (!names.Contains(candidate)) match = candidate;
            }
            if (match is null) return 5;
        }

        string value = $"{ChromiumId};{address}";
        list.SetValue(match, value, RegistryValueKind.String);
        owner.SetValue(name, new[] { match, value }, RegistryValueKind.MultiString);
        owner.SetValue(name + ".slot", match);
        owner.SetValue(name + ".value", value);
        return 0;
    }

    private static JsonObject ReadFirefox(RegistryKey key) => key.GetValue(FirefoxValue) switch
    {
        null => new(),
        string value => JsonNode.Parse(value)?.AsObject() ?? throw new InvalidDataException(),
        string[] lines => JsonNode.Parse(string.Join("\n", lines))?.AsObject() ?? throw new InvalidDataException(),
        _ => throw new InvalidDataException()
    };

    private static JsonObject FirefoxEntry(string url) => new()
    {
        ["installation_mode"] = "force_installed",
        ["install_url"] = url
    };

    private static int ApplyFirefox(RegistryKey machine, RegistryKey owner, string url)
    {
        using var key = machine.CreateSubKey(FirefoxKey);
        var policy = ReadFirefox(key);
        string? owned = owner.GetValue("Firefox") as string;
        if (policy.ContainsKey(FirefoxId) &&
            (owned is null || !JsonNode.DeepEquals(policy[FirefoxId], FirefoxEntry(owned)))) return 5;

        policy.Remove(FirefoxId);
        string remaining = policy.Count == 0 ? "" : policy.ToJsonString();
        policy[FirefoxId] = FirefoxEntry(url);
        string written = policy.ToJsonString();
        key.SetValue(FirefoxValue, new[] { written }, RegistryValueKind.MultiString);
        owner.SetValue("Firefox", url, RegistryValueKind.String);
        owner.SetValue("Firefox.written", written);
        owner.SetValue("Firefox.remaining", remaining);
        return 0;
    }

    private static void Remove(RegistryKey machine, RegistryKey owner, string name)
    {
        if (name == "Firefox")
        {
            if (owner.GetValue(name) is not string url) return;
            using var key = machine.OpenSubKey(FirefoxKey, writable: true);
            if (key is not null)
            {
                var policy = ReadFirefox(key);
                if (JsonNode.DeepEquals(policy[FirefoxId], FirefoxEntry(url)))
                {
                    policy.Remove(FirefoxId);
                    if (policy.Count == 0) key.DeleteValue(FirefoxValue, throwOnMissingValue: false);
                    else key.SetValue(FirefoxValue, new[] { policy.ToJsonString() }, RegistryValueKind.MultiString);
                }
            }
        }
        else
        {
            if (owner.GetValue(name) is not string[] { Length: 2 } pair) return;
            using var key = machine.OpenSubKey(ChromiumPath(name), writable: true);
            if (key is not null && Equals(key.GetValue(pair[0]), pair[1]))
                key.DeleteValue(pair[0], throwOnMissingValue: false);
        }
        owner.DeleteValue(name, throwOnMissingValue: false);
        foreach (string suffix in new[] { ".slot", ".value", ".written", ".remaining" })
            owner.DeleteValue(name + suffix, throwOnMissingValue: false);
    }

    private static bool IsDomainJoined()
    {
        int result = NetGetJoinInformation(null, out var buffer, out int status);
        if (buffer != IntPtr.Zero) NetApiBufferFree(buffer);
        return result == 0 && status == 3; // NetSetupDomainName
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetGetJoinInformation(string? server, out IntPtr name, out int status);
    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);
}

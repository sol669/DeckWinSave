using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinSCP;

namespace GameSavePreview;

internal sealed record DeckSettings(string Address, int Port, string User, string Fingerprint)
{
    public string? VpnAddress { get; init; }
}
internal sealed record GameProfile(string Name, string WindowsPath, string DeckPath, string[] Exceptions)
{
    public string[] LegacyPatterns { get; init; } = [];
    public string? WindowsExe { get; init; }
    public string? DeckExe { get; init; }
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    public bool IsExcluded(string path) => Exceptions.Any(e =>
        path.Equals(e, StringComparison.OrdinalIgnoreCase) || path.StartsWith(e + "/", StringComparison.OrdinalIgnoreCase))
        || (LegacyPatterns.Length > 0 && !LegacyPatterns.Any(p =>
            System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(p, Path.GetFileName(path), true)));
}

internal sealed record Settings(DeckSettings? Deck, List<GameProfile> Games)
{
    public bool AutoConnect { get; init; }
    public string Theme { get; init; } = "system";
    public string Language { get; init; } = "ru";
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static readonly string Directory = ResolveDirectory();
    public static string Key => Path.Combine(Directory, "keys", "client.ppk");
    public static string PublicKey => Path.Combine(Directory, "keys", "client.pub");
    public static string WinScp => Path.Combine(AppContext.BaseDirectory, "WinSCP.exe");

    private static string ResolveDirectory()
    {
        var location = Path.Combine(AppContext.BaseDirectory, "data-location.txt");
        return File.Exists(location)
            ? Path.GetFullPath(File.ReadAllText(location).Trim())
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DeckWinSave");
    }

    public static Settings Read()
    {
        var file = Path.Combine(Directory, "settings.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Json)
            ?? throw new IOException("Не удалось прочитать настройки.") : new(null, []);
    }

    public void Save()
    {
        System.IO.Directory.CreateDirectory(Directory);
        var file = Path.Combine(Directory, "settings.json");
        WriteJson(file, this);
    }

    public static void WriteJson<T>(string file, T value)
    {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(value, Json)); File.Move(temp, file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static string NewWorkFolder()
    {
        var path = Path.Combine(Directory, "work", Guid.NewGuid().ToString("N"));
        return System.IO.Directory.CreateDirectory(path).FullName;
    }

    public static void OpenWinScp(DeckSettings deck)
    {
        var uri = new UriBuilder("sftp", deck.Address, deck.Port) { UserName = deck.User };
        var ini = Path.Combine(Directory, "WinSCP.ini");
        if (!File.Exists(ini))
        {
            using var stream = new FileStream(ini, FileMode.CreateNew, FileAccess.Write);
            using var configuration = new StreamWriter(stream);
            configuration.WriteLine("[Configuration]");
        }
        var cli = new ProcessStartInfo(WinScp) { WorkingDirectory = Directory, UseShellExecute = false };
        foreach (var argument in new[] { uri.Uri.GetLeftPart(UriPartial.Authority), "/newinstance", "/ini=" + ini,
            "/privatekey=" + Key, "/hostkey=" + deck.Fingerprint })
            cli.ArgumentList.Add(argument);
        using var process = Process.Start(cli);
    }
}

internal static class Pairing
{
    public static string NormalizeAddress(string input)
    {
        var address = input.Trim();
        var slash = address.IndexOf('/');
        if (slash >= 0)
        {
            var host = address[..slash];
            var kind = Uri.CheckHostName(host);
            var maxPrefix = kind == UriHostNameType.IPv4 ? 32 : 128;
            if ((kind is UriHostNameType.IPv4 or UriHostNameType.IPv6) &&
                int.TryParse(address.AsSpan(slash + 1), out var prefix) && prefix >= 0 && prefix <= maxPrefix)
                return host;
            return address;
        }

        if (!address.Contains('.') && Uri.CheckHostName(address) == UriHostNameType.Dns &&
            !IPAddress.TryParse(address, out _))
            return address + ".local";
        return address;
    }

    public static Session CreateSession() => new() { ExecutablePath = Settings.WinScp, Timeout = TimeSpan.FromSeconds(25),
        ReconnectTime = TimeSpan.Zero };

    public static SessionOptions Options(DeckSettings deck, TimeSpan? timeout = null) => new()
    {
        Protocol = Protocol.Sftp, HostName = deck.Address, PortNumber = deck.Port, UserName = deck.User,
        SshHostKeyPolicy = SshHostKeyPolicy.Check, Timeout = timeout ?? TimeSpan.FromSeconds(8)
    };

    public static string Probe(DeckSettings deck, TimeSpan? timeout = null)
    {
        if (Uri.CheckHostName(deck.Address) == UriHostNameType.Unknown || deck.Port is < 1 or > 65535
            || !Regex.IsMatch(deck.User, "^[a-zA-Z0-9_-]+$"))
            throw new IOException("Проверьте адрес, порт и имя пользователя.");
        using var session = CreateSession();
        return session.ScanFingerprint(Options(deck, timeout), "SHA-256");
    }

    public static Session Connect(DeckSettings deck, TimeSpan? timeout = null)
    {
        var session = CreateSession();
        try
        {
            var options = Options(deck, timeout);
            options.SshPrivateKeyPath = Settings.Key;
            options.SshHostKeyFingerprint = deck.Fingerprint;
            options.AddRawSettings("TryAgent", "0");
            options.AddRawSettings("AuthKI", "0");
            session.Open(options);
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    public static DeckSettings? FindEndpoint(DeckSettings deck, string? preferredAddress,
        Func<DeckSettings, TimeSpan?, bool> check)
    {
        var addresses = new[] { deck.Address, deck.VpnAddress }
            .OfType<string>()
            .Where(address => !string.IsNullOrWhiteSpace(address))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(address => string.Equals(address, preferredAddress, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var timeout = addresses.Length > 1 ? TimeSpan.FromSeconds(4) : (TimeSpan?)null;
        foreach (var address in addresses)
        {
            var endpoint = deck with { Address = address };
            try
            {
                if (check(endpoint, timeout)) return endpoint;
            }
            catch (Exception error) when (error is SessionException or TimeoutException)
            {
                // Try the other address with the same SSH credentials and host key.
            }
        }
        return null;
    }

    public static string OpenSshDigest(string value)
    {
        var digest = value.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[^1];
        return digest.StartsWith("SHA256:", StringComparison.Ordinal) ? digest[7..] : digest;
    }

    public static string PublicKeyCommand()
    {
        EnsureKey();
        var fields = File.ReadAllText(Settings.PublicKey).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 2 || fields[0] != "ssh-ed25519" || !Regex.IsMatch(fields[1], "^[A-Za-z0-9+/]+=*$"))
            throw new IOException("Публичный ключ имеет неверный формат.");
        string identity = fields[0] + " " + fields[1];
        string line = "restrict " + identity + " GameSaveManager";
        return "umask 077 && mkdir -p ~/.ssh && chmod 700 ~/.ssh && touch ~/.ssh/authorized_keys && "
            + "chmod 600 ~/.ssh/authorized_keys && (grep -Fq -- '" + identity + "' ~/.ssh/authorized_keys || "
            + "printf '\\n%s\\n' '" + line + "' >> ~/.ssh/authorized_keys)";
    }

    public static void InstallKey(DeckSettings deck, System.Security.SecureString password)
    {
        var command = PublicKeyCommand();
        using (var session = CreateSession())
        {
            var options = Options(deck);
            options.SshHostKeyFingerprint = deck.Fingerprint;
            options.SecurePassword = password;
            session.Open(options);
            session.ExecuteCommand(command).Check();
        }
        using var verified = Connect(deck);
        verified.ListDirectory(".");
    }

    private static void EnsureKey()
    {
        var dir = Path.GetDirectoryName(Settings.Key)!;
        if (File.Exists(Settings.Key) && File.Exists(Settings.PublicKey)) return;
        System.IO.Directory.CreateDirectory(dir);
        using var identity = WindowsIdentity.GetCurrent();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { identity.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(dir).SetAccessControl(acl);
        var path = Path.Combine(dir, "client");
        if (!File.Exists(path))
        {
            var keygen = Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh-keygen.exe");
            if (!File.Exists(keygen)) throw new IOException("Включите компонент Windows «Клиент OpenSSH» для создания ключа.");
            Run(keygen, ["-q", "-t", "ed25519", "-N", "", "-C", "GameSaveManager", "-f", path]);
        }
        if (!File.Exists(Settings.Key))
        {
            var output = Path.Combine(dir, "convert-" + Guid.NewGuid().ToString("N") + ".ppk");
            try
            {
                Run(Settings.WinScp, ["/console", "/ini=nul", "/keygen", path, "/output=" + output],
                    () => IsCompletePpk(output));
                File.Move(output, Settings.Key);
            }
            finally { if (File.Exists(output)) File.Delete(output); }
        }
        if (!File.Exists(Settings.Key) || !File.Exists(Settings.PublicKey)) throw new IOException("Ключ не создан.");
    }

    private static bool IsCompletePpk(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var lines = File.ReadAllLines(path);
            return lines.Length > 1 && lines[0] == "PuTTY-User-Key-File-3: ssh-ed25519"
                && Regex.IsMatch(lines[^1], "^Private-MAC: [0-9a-f]{64}$");
        }
        catch (IOException) { return false; }
    }

    private static void Run(string program, string[] args, Func<bool>? outputReady = null)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Settings.Directory };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Не удалось запустить " + Path.GetFileName(program));
        var timer = Stopwatch.StartNew();
        while (!process.WaitForExit(100))
        {
            if (outputReady?.Invoke() == true)
            {
                // WinSCP.exe can leave its confirmation window open after writing the PPK.
                if (!process.HasExited) process.Kill();
                process.WaitForExit();
                return;
            }
            if (timer.Elapsed < TimeSpan.FromSeconds(15)) continue;
            if (!process.HasExited) process.Kill();
            throw new IOException("Создание ключа не завершилось за отведённое время.");
        }
        if (process.ExitCode != 0) throw new IOException("Не удалось создать ключ. Код: " + process.ExitCode);
        if (outputReady?.Invoke() == false) throw new IOException("WinSCP не записал полный файл ключа.");
    }
}

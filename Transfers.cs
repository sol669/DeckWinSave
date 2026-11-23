using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Text.Json;
using WinSCP;

namespace GameSavePreview;

internal record Entry(string Path, bool Directory, long Size, DateTime Modified);
internal record Fingerprint(long Size, string Hash);
internal record Change(string Path, Fingerprint? Source, Fingerprint? Destination, string Action)
{
    public bool CanCopy => Action is "Добавить" or "Заменить";
}

internal abstract class SaveFolder : IDisposable
{
    public abstract Entry[] List(string directory);
    public abstract void Read(string path, string local);
    public abstract void Write(string local, string path);
    public abstract void Replace(string temporary, string path);
    public abstract void Delete(string path);
    public abstract void EnsureDirectory(string path);
    public abstract void CheckProcess(string? executable);
    public abstract void Dispose();

    public static string Parent(string path) => path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
    public static void Validate(string path)
    {
        foreach (var name in path.Split('/'))
        {
            if (name.Length == 0 || name is "." or ".." || name.EndsWith('.') || name.EndsWith(' ')
                || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || Regex.IsMatch(name, @"^(CON|PRN|AUX|NUL|COM[0-9¹²³]|LPT[0-9¹²³])(\.|$)", RegexOptions.IgnoreCase))
                throw new IOException("Имя несовместимо с Windows: " + path);
        }
    }

    public Dictionary<string, Entry> Files(Func<string, bool>? skipDirectory = null)
    {
        var result = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue("");
        while (queue.TryDequeue(out var directory))
        {
            foreach (var entry in List(directory))
            {
                Validate(entry.Path);
                if (!names.Add(entry.Path)) throw new IOException("Конфликт имён: " + entry.Path);
                if (entry.Directory)
                {
                    if (skipDirectory?.Invoke(entry.Path) != true) queue.Enqueue(entry.Path);
                }
                else result.Add(entry.Path, entry);
            }
        }
        return result;
    }

    public Entry? Inspect(string path)
    {
        var parent = Parent(path);
        if (parent.Length > 0 && Inspect(parent)?.Directory != true) return null;
        return List(parent).FirstOrDefault(e => e.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class WindowsFolder(string root, bool checkProcesses = true) : SaveFolder
{
    private string P(string relative)
    {
        if (relative.Length > 0) Validate(relative);
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        for (var node = path; node is not null; node = Path.GetDirectoryName(node))
            if ((File.Exists(node) || Directory.Exists(node)) && File.GetAttributes(node).HasFlag(FileAttributes.ReparsePoint))
                throw new IOException("Ссылка в папке сохранений: " + node);
        return path;
    }

    public override Entry[] List(string directory) => new DirectoryInfo(P(directory)).EnumerateFileSystemInfos()
        .Where(info => !info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        .Select(info => new Entry(Join(directory, info.Name), info is DirectoryInfo,
            info is FileInfo file ? file.Length : 0, info.LastWriteTime)).ToArray();
    private static string Join(string dir, string name) => dir.Length == 0 ? name : dir + "/" + name;
    public override void Read(string path, string local) => File.Copy(P(path), local);
    public override void Write(string local, string path) => File.Copy(local, P(path));
    public override void Replace(string temporary, string path) => File.Move(P(temporary), P(path), true);
    public override void Delete(string path) => File.Delete(P(path));
    public override void EnsureDirectory(string path) => Directory.CreateDirectory(P(path));
    public override void CheckProcess(string? executable)
    {
        if (!checkProcesses || string.IsNullOrWhiteSpace(executable)) return;
        var processes = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable));
        try { if (processes.Length != 0) throw new IOException("Закройте игру на ПК."); }
        finally { foreach (var process in processes) process.Dispose(); }
    }
    public override void Dispose() { }
}

internal sealed class DeckConnectionException(Exception cause) : IOException(cause.Message, cause);

internal sealed class DeckFolder : SaveFolder
{
    private static readonly string[] ConnectionErrors = ["network error", "connection lost", "connection closed",
        "connection reset", "connection refused", "connection aborted", "timed out", "host is not communicating",
        "ошибка сети", "сетевая ошибка", "соединение закрыто", "соединение разорвано", "время ожидания"];
    private readonly Session session;
    private readonly string root;
    public DeckFolder(DeckSettings deck, string root)
    {
        try { session = Pairing.Connect(deck); }
        catch (Exception error) { throw new DeckConnectionException(error); }
        this.root = root == "." ? session.HomePath.TrimEnd('/') : root.TrimEnd('/');
    }

    private T Remote<T>(Func<T> action)
    {
        try { return action(); }
        catch (Exception error) when (error is TimeoutException or SessionLocalException ||
            error is SessionRemoteException && (!session.Opened ||
                ConnectionErrors.Any(message => error.Message.Contains(message, StringComparison.OrdinalIgnoreCase))))
        {
            throw new DeckConnectionException(error);
        }
    }

    private void Remote(Action action) => Remote(() => { action(); return true; });
    private string P(string relative)
    {
        if (relative.Length > 0) Validate(relative);
        return relative.Length == 0 ? (root.Length == 0 ? "/" : root) : root + "/" + relative;
    }
    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'") + "'";
    private static TransferOptions Options => new() { TransferMode = TransferMode.Binary, PreserveTimestamp = false,
        ResumeSupport = new TransferResumeSupport { State = TransferResumeSupportState.Off } };

    public override Entry[] List(string directory) => Remote(() => session.ListDirectory(P(directory))).Files
        .Where(f => !f.IsParentDirectory && !f.IsThisDirectory && f.FileType != 'l' && f.FileType != 'L')
        .Where(f => f.IsDirectory || f.FileType is '-' or 'f')
        .Select(f => new Entry(directory.Length == 0 ? f.Name : directory + "/" + f.Name,
            f.IsDirectory, f.Length, f.LastWriteTime)).ToArray();

    public override void Read(string path, string local) =>
        Remote(() => session.GetFiles(RemotePath.EscapeFileMask(P(path)), local, false, Options).Check());
    public override void Write(string local, string path) =>
        Remote(() => session.PutFiles(local, RemotePath.EscapeOperationMask(P(path)), false, Options).Check());
    public override void Replace(string temporary, string path) =>
        Remote(() => session.ExecuteCommand("mv -fT -- " + Quote(P(temporary)) + " " + Quote(P(path))).Check());
    public override void Delete(string path) => Remote(() => session.RemoveFiles(RemotePath.EscapeFileMask(P(path))).Check());
    public override void EnsureDirectory(string path)
    {
        if (path.Length == 0) return;
        var existing = Inspect(path);
        if (existing is not null)
        {
            if (!existing.Directory || existing.Path != path)
                throw new IOException("Конфликт имени папки на Steam Deck: " + path);
            return;
        }
        EnsureDirectory(Parent(path));
        Remote(() => session.CreateDirectory(P(path)));
    }

    public override void CheckProcess(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return;
        var name = executable.Split('/').Last();
        var output = Remote(() =>
        {
            var result = session.ExecuteCommand("LC_ALL=C ps -eo comm=,args=");
            result.Check();
            return result;
        });
        if (Regex.IsMatch(output.Output, @"(?:^|[\\/\s""'])" + Regex.Escape(name) + @"(?:$|[\s""'])",
            RegexOptions.IgnoreCase | RegexOptions.Multiline))
            throw new IOException("Закройте игру на Steam Deck.");
    }
    public override void Dispose() => session.Dispose();
}

internal static class Transfers
{
    internal const string TempPrefix = ".gsm-transfer-";
    internal static bool IsBackup(string path) => path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase);
    internal static bool Ignored(string path) => IsBackup(path)
        || path.Split('/').Any(p => p.StartsWith(TempPrefix, StringComparison.OrdinalIgnoreCase));
    private const string StampFormat = "yyyyMMdd-HHmmss-fff";
    internal static string Stamp() => DateTimeOffset.Now.ToString(StampFormat, System.Globalization.CultureInfo.InvariantCulture);
    internal static string BackupName(string path, string stamp, string id) => path + "." + stamp + "." + id + ".bak";
    internal static string? OriginalName(string backup)
    {
        var match = Regex.Match(backup, @"^(.+)\.\d{8}-\d{6}-\d{3}\.[a-f0-9]{8}\.bak$");
        return match.Success ? match.Groups[1].Value : null;
    }

    internal static Change[] Preview(SaveFolder source, SaveFolder destination, GameProfile game)
    {
        var from = source.Files(d => game.Exceptions.Contains(d, StringComparer.OrdinalIgnoreCase));
        var to = destination.Files(d => game.Exceptions.Contains(d, StringComparer.OrdinalIgnoreCase));
        var files = from.Values.Where(f => !Ignored(f.Path))
            .Select(f =>
            {
                if (game.IsExcluded(f.Path)) return new Change(f.Path, null, null, "Исключён в профиле");
                var before = ReadFingerprint(source, f.Path);
                if (!to.TryGetValue(f.Path, out var previous)) return new Change(f.Path, before, null, "Добавить");
                var after = ReadFingerprint(destination, previous.Path);
                return new Change(f.Path, before, after, before == after ? "Уже совпадает" : "Заменить");
            });
        var folders = game.Exceptions.Where(e => !from.ContainsKey(e) && source.Inspect(e)?.Directory == true)
            .Select(e => new Change(e + "/", null, null, "Исключён в профиле"));
        return files.Concat(folders).OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static void Transfer(SaveFolder source, SaveFolder destination, GameProfile game, bool toDeck, Change[] changes)
    {
        void Guard() { source.CheckProcess(toDeck ? game.WindowsExe : game.DeckExe); destination.CheckProcess(toDeck ? game.DeckExe : game.WindowsExe); }
        Guard();
        Execute(source, destination, changes.Where(c => c.CanCopy).Select(c => (c.Path, c.Path, c.Source!, c.Destination)).ToArray(), Guard, true);
    }

    internal static void Restore(SaveFolder source, SaveFolder destination, string backup, string target,
        Fingerprint verifiedBackup, Fingerprint? verifiedDestination, Action guard)
    {
        guard();
        if (OriginalName(backup) != target) throw new IOException("Выберите резервную копию Game Save Manager.");
        Execute(source, destination, [(backup, target, verifiedBackup, verifiedDestination)], guard, false);
    }

    internal static void Execute(SaveFolder source, SaveFolder destination,
        (string Source, string Target, Fingerprint Data, Fingerprint? Expected)[] items, Action guard, bool copySource,
        IReadOnlyDictionary<string, string>? fullBackup = null)
    {
        if (items.Length == 0) return;
        var dir = Settings.NewWorkFolder();
        var stamp = Stamp();
        var id = Guid.NewGuid().ToString("N")[..8];
        var staged = new List<(string Temp, string Target, string Incoming, string? Previous, string? Backup, Fingerprint Data, Fingerprint? Expected)>();
        var started = new List<int>();
        var cleanup = new List<string>();
        var journal = Path.Combine(dir, "transaction.json");
        bool unresolved = false;
        try
        {
            for (int i = 0; i < items.Length; i++)
            {
                var item = items[i];
                var incoming = Path.Combine(dir, i + "-incoming");
                var previous = Path.Combine(dir, i + "-previous");
                source.Read(item.Source, incoming);
                if (Hash(incoming) != item.Data) throw new IOException("Источник изменился: " + item.Source);
                var dest = destination.Inspect(item.Target);
                if (dest?.Directory == true) throw new IOException("В месте назначения находится папка: " + item.Target);
                if (dest is not null)
                {
                    destination.Read(dest.Path, previous);
                    if (Hash(previous) != item.Expected) throw new IOException("Назначение изменилось: " + dest.Path);
                }
                else if (item.Expected is not null) throw new IOException("Файл назначения исчез: " + item.Target);
                var target = dest?.Path ?? item.Target;
                var parent = SaveFolder.Parent(target);
                destination.EnsureDirectory(parent);
                var temp = (parent.Length == 0 ? "" : parent + "/") + TempPrefix + Guid.NewGuid().ToString("N");
                cleanup.Add(temp);
                destination.Write(incoming, temp);
                if (ReadFingerprint(destination, temp) != item.Data) throw new IOException("Проверка копии не прошла: " + target);
                string? backup = dest is null ? null : fullBackup is null
                    ? BackupName(target, stamp, id) : fullBackup[target];
                if (backup is not null && fullBackup is null) SaveBackup(destination, previous, backup);
                if (copySource) SaveBackup(source, incoming, BackupName(item.Source, stamp, id));
                staged.Add((temp, target, incoming, dest is null ? null : previous, backup, item.Data, item.Expected));
            }

            guard();
            for (int i = 0; i < staged.Count; i++)
            {
                var file = staged[i];
                if (ReadFingerprint(source, items[i].Source) != file.Data
                    || ReadCurrent(destination, file.Target) != file.Expected)
                    throw new IOException("Файлы изменились после предпросмотра: " + file.Target);
            }
            for (int i = 0; i < staged.Count; i++)
            {
                var file = staged[i];
                started.Add(i);
                File.WriteAllText(journal, JsonSerializer.Serialize(new
                {
                    attempted = started.Select(n => staged[n].Target),
                    copies = staged.Select(s => new { s.Target, s.Backup, s.Previous, s.Temp })
                }, Settings.Json));
                destination.Replace(file.Temp, file.Target);
                if (ReadFingerprint(destination, file.Target) != file.Data)
                    throw new IOException("Итоговая проверка не прошла: " + file.Target);
            }
        }
        catch (Exception error)
        {
            var pending = new List<string>();
            foreach (var i in started.AsEnumerable().Reverse())
            {
                var file = staged[i];
                try
                {
                    var actual = ReadCurrent(destination, file.Target);
                    if (actual == file.Expected) continue;
                    if (actual != file.Data) throw new IOException("Файл изменён другим процессом.");
                    if (file.Previous is null) destination.Delete(file.Target);
                    else
                    {
                        var rollback = file.Temp + "-rollback";
                        cleanup.Add(rollback);
                        destination.Write(file.Previous, rollback);
                        if (ReadFingerprint(destination, rollback) != file.Expected) throw new IOException("Ошибка копии отката.");
                        destination.Replace(rollback, file.Target);
                    }
                    if (ReadCurrent(destination, file.Target) != file.Expected) throw new IOException("Ошибка проверки отката.");
                }
                catch { pending.Add(file.Target + (file.Backup is null ? "" : " → " + file.Backup)); }
            }
            unresolved = pending.Count > 0;
            throw new IOException(error.Message + (unresolved
                ? "\nИтог не подтверждён. Проверьте файлы и копии:\n" + string.Join("\n", pending) + "\nРабочие копии: " + dir
                : "\nПеренос отменён. Игровые файлы назначения не изменились."), error);
        }
        finally
        {
            foreach (var path in cleanup)
                try { if (destination.Inspect(path) is not null) destination.Delete(path); } catch { }
            if (!unresolved) Cleanup(dir);
        }
    }

    public static void ManualBackup(SaveFolder folder, GameProfile game)
    {
        var dir = Settings.NewWorkFolder();
        var stamp = Stamp();
        var id = Guid.NewGuid().ToString("N")[..8];
        try
        {
            var entries = folder.Files().Values.Where(f => !Ignored(f.Path) && !game.IsExcluded(f.Path)).ToArray();
            if (entries.Length == 0) throw new IOException("В выбранной папке нет файлов сохранений.");
            foreach (var entry in entries)
            {
                var file = Path.Combine(dir, Guid.NewGuid().ToString("N"));
                folder.Read(entry.Path, file);
                SaveBackup(folder, file, BackupName(entry.Path, stamp, id));
            }
        }
        finally { Cleanup(dir); }
    }

    internal static void SaveBackup(SaveFolder folder, string local, string target)
    {
        if (folder.Inspect(target) is not null) throw new IOException("Резервная копия с таким именем уже существует.");
        var temp = SaveFolder.Parent(target);
        temp = (temp.Length == 0 ? "" : temp + "/") + TempPrefix + Guid.NewGuid().ToString("N");
        try
        {
            folder.Write(local, temp);
            if (ReadFingerprint(folder, temp) != Hash(local)) throw new IOException("Не удалось проверить резервную копию.");
            folder.Replace(temp, target);
        }
        finally { try { if (folder.Inspect(temp) is not null) folder.Delete(temp); } catch { } }
    }

    internal static Fingerprint? ReadCurrent(SaveFolder folder, string path)
    {
        var entry = folder.Inspect(path);
        if (entry?.Directory == true) throw new IOException("Вместо файла найдена папка: " + path);
        return entry is null ? null : ReadFingerprint(folder, entry.Path);
    }

    internal static Fingerprint ReadFingerprint(SaveFolder folder, string path)
    {
        var dir = Settings.NewWorkFolder();
        var temp = Path.Combine(dir, "content");
        try { folder.Read(path, temp); return Hash(temp); }
        finally { Cleanup(dir); }
    }

    internal static Fingerprint Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return new(stream.Length, Convert.ToHexString(SHA256.HashData(stream)));
    }

    internal static void Cleanup(string dir)
    {
        var parent = Path.GetFullPath(Path.Combine(Settings.Directory, "work")) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(dir).StartsWith(parent, StringComparison.OrdinalIgnoreCase))
            try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

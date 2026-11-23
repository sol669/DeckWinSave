using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GameSavePreview;

internal sealed record FileState(string Path, Fingerprint Fingerprint, DateTime Modified);
internal sealed record FolderState(FileState[] Files, string[] Excluded)
{
    public DateTime? LastModified => Files.Length == 0 ? null : Files.Max(f => f.Modified);
    public Dictionary<string, Fingerprint> Index() => Files.ToDictionary(f => f.Path, f => f.Fingerprint, StringComparer.OrdinalIgnoreCase);
}
internal sealed record Comparison(FolderState Pc, FolderState Deck, bool? ToDeck, string Reason)
{
    public bool IsFirstTransfer { get; init; }

    public Change[] Preview(bool toDeck)
    {
        var from = toDeck ? Pc : Deck;
        var to = (toDeck ? Deck : Pc).Index();
        return from.Files.Select(f =>
        {
            to.TryGetValue(f.Path, out var existing);
            return new Change(f.Path, f.Fingerprint, existing, existing is null ? "Добавить"
                : existing == f.Fingerprint ? "Уже совпадает" : "Заменить");
        }).Concat(from.Excluded.Select(path => new Change(path, null, null, "Исключён в профиле")))
            .OrderBy(c => c.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
internal sealed record BackupFile(string Path, string Backup, Fingerprint Fingerprint);
internal sealed record Operation(string Id, DateTimeOffset At, string Kind, bool DeckDestination,
    BackupFile[] Pc, BackupFile[] Deck, Change[] Changes, string State);
internal sealed record Baseline(FolderState Pc, FolderState Deck, bool ReviewRequired, string Rules);
internal sealed record RestorePlan(Operation Operation, BackupFile[] Files, Change[] Added, FolderState Before);

internal sealed class Sync(GameProfile game, string hostFingerprint)
{
    private readonly string history = Path.Combine(Settings.Directory, "history", Key(game.Id, game.WindowsPath, game.DeckPath,
        hostFingerprint));
    private readonly string rules = Key(game.Exceptions.OrderBy(e => e, StringComparer.OrdinalIgnoreCase),
        game.LegacyPatterns.OrderBy(e => e, StringComparer.OrdinalIgnoreCase));

    private static string Key(params object[] values) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(values))));

    public static FolderState Scan(SaveFolder folder, GameProfile profile)
    {
        var entries = folder.Files().Values.Where(f => !Transfers.Ignored(f.Path)).ToArray();
        return new(
            entries.Where(f => !profile.IsExcluded(f.Path))
                .Select(f => new FileState(f.Path, Transfers.ReadFingerprint(folder, f.Path), f.Modified)).ToArray(),
            entries.Where(f => profile.IsExcluded(f.Path)).Select(f => f.Path).ToArray());
    }

    private static bool Same(FolderState a, FolderState b)
    {
        var index = b.Index();
        return a.Files.Length == index.Count && a.Files.All(f => index.GetValueOrDefault(f.Path) == f.Fingerprint);
    }

    public Comparison Compare(SaveFolder pc, SaveFolder deck)
    {
        var a = Scan(pc, game);
        var b = Scan(deck, game);
        var baselineFile = Path.Combine(history, "baseline.json");
        var baseline = Read<Baseline>(baselineFile);
        return Decide(a, b, baseline?.Rules == rules ? baseline : null);
    }

    internal static Comparison Decide(FolderState a, FolderState b, Baseline? previous)
    {
        if (Same(a, b)) return new(a, b, null, a.Files.Length == 0 ? "Сохранения не найдены" : "Сохранения совпадают");
        if (previous is null)
        {
            if (a.Files.Length == 0) return new(a, b, false, "") { IsFirstTransfer = true };
            if (b.Files.Length == 0) return new(a, b, true, "") { IsFirstTransfer = true };
            return new(a, b, null, "Первый перенос — выберите направление вручную") { IsFirstTransfer = true };
        }
        if (previous.ReviewRequired) return new(a, b, null, "Остались различия — выберите направление вручную");
        var pcChanged = !Same(a, previous.Pc);
        var deckChanged = !Same(b, previous.Deck);
        if (pcChanged == deckChanged)
            return new(a, b, null, pcChanged ? "Сохранения изменились на обоих устройствах" : "Нет новых изменений после переноса");
        if ((pcChanged ? a : b).Files.Length == 0)
            return new(a, b, null, "На изменившейся стороне нет сохранений");
        return new(a, b, pcChanged, "");
    }

    public void RememberEqual(Comparison comparison)
    {
        if (comparison.Pc.Files.Length > 0 && Same(comparison.Pc, comparison.Deck))
            Settings.WriteJson(Path.Combine(history, "baseline.json"), new Baseline(comparison.Pc, comparison.Deck, false, rules));
    }

    public void Transfer(SaveFolder pc, SaveFolder deck, Comparison approved, bool toDeck, Change[] selected)
    {
        if (selected.Length == 0) throw new IOException("Выберите файлы для переноса.");
        void Guard() { pc.CheckProcess(game.WindowsExe); deck.CheckProcess(game.DeckExe); }
        Guard();
        var now = Compare(pc, deck);
        if (!Same(now.Pc, approved.Pc) || !Same(now.Deck, approved.Deck))
            throw new IOException("Сохранения изменились. Откройте предпросмотр заново.");
        var at = DateTimeOffset.Now;
        var id = Transfers.Stamp() + "." + Guid.NewGuid().ToString("N")[..8];
        var a = BackUp(pc, now.Pc, id);
        var b = BackUp(deck, now.Deck, id);
        var operation = new Operation(id, at, "transfer", toDeck, a, b, selected, "prepared");
        Write(operation);
        try
        {
            var source = toDeck ? pc : deck;
            var destination = toDeck ? deck : pc;
            var previous = (toDeck ? b : a).ToDictionary(f => f.Path, f => f.Backup, StringComparer.OrdinalIgnoreCase);
            void Check()
            {
                Guard();
                if (!Same(Scan(pc, game), now.Pc) || !Same(Scan(deck, game), now.Deck))
                    throw new IOException("Файлы изменились после резервирования.");
            }
            Transfers.Execute(source, destination,
                selected.Select(c => (c.Path, c.Path, c.Source!, c.Destination)).ToArray(), Check, false, previous);
            Write(operation with { State = "complete" });

            var pcAfter = toDeck ? now.Pc : Applied(now.Pc, selected);
            var deckAfter = toDeck ? Applied(now.Deck, selected) : now.Deck;
            bool partial = selected.Length != approved.Preview(toDeck).Count(c => c.CanCopy);
            Settings.WriteJson(Path.Combine(history, "baseline.json"), new Baseline(pcAfter, deckAfter, partial, rules));
        }
        catch
        {
            var persisted = Read<Operation>(OperationPath(id));
            if (persisted?.State == "prepared") Write(operation with { State = "interrupted" });
            throw;
        }
    }

    private static FolderState Applied(FolderState old, Change[] selected)
    {
        var index = old.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var change in selected)
            index[change.Path] = new(change.Path, change.Source!, DateTime.Now);
        return new(index.Values.ToArray(), old.Excluded);
    }

    public Operation CreateBackup(SaveFolder folder, bool onDeck)
    {
        folder.CheckProcess(onDeck ? game.DeckExe : game.WindowsExe);
        var snapshot = Scan(folder, game);
        if (snapshot.Files.Length == 0)
            throw new IOException("В выбранной папке нет файлов сохранений.");

        var at = DateTimeOffset.Now;
        var id = Transfers.Stamp() + "." + Guid.NewGuid().ToString("N")[..8];
        var files = BackUp(folder, snapshot, id);
        var operation = new Operation(id, at, "backup", onDeck, onDeck ? [] : files, onDeck ? files : [], [], "complete");
        try { Write(operation); }
        catch
        {
            DeleteIncompleteBackup(folder, files);
            throw;
        }
        return operation;
    }

    public RestorePlan PlanRestore(SaveFolder folder, bool onDeck)
    {
        var operation = Latest(onDeck) ?? throw new IOException("На выбранном устройстве пока нет полного бэкапа.");
        var before = Scan(folder, game);
        var current = before.Index();
        var files = (onDeck ? operation.Deck : operation.Pc).Where(f => !game.IsExcluded(f.Path)).ToArray();
        var added = operation.Kind == "transfer" && operation.DeckDestination == onDeck
            ? operation.Changes.Where(c => c.Destination is null && current.GetValueOrDefault(c.Path) == c.Source).ToArray()
            : [];
        if (files.Length == 0 && added.Length == 0)
            throw new IOException("В последнем бэкапе нет файлов для восстановления с текущими исключениями.");
        return new(operation, files, added, before);
    }

    public int Restore(SaveFolder folder, bool onDeck, RestorePlan plan, bool removeAdded)
    {
        void Guard() => folder.CheckProcess(onDeck ? game.DeckExe : game.WindowsExe);
        Guard();
        if (!Same(Scan(folder, game), plan.Before)) throw new IOException("Сохранения изменились. Повторите проверку.");
        var at = DateTimeOffset.Now;
        var id = Transfers.Stamp() + "." + Guid.NewGuid().ToString("N")[..8];
        var full = BackUp(folder, plan.Before, id);
        var current = plan.Before.Index();
        var operation = new Operation(id, at, "restore", onDeck, onDeck ? [] : full, onDeck ? full : [], [], "prepared");
        Write(operation);
        int removed = 0;
        try
        {
            var previous = full.ToDictionary(f => f.Path, f => f.Backup, StringComparer.OrdinalIgnoreCase);
            Transfers.Execute(folder, folder,
                plan.Files.Select(f => (f.Backup, f.Path, f.Fingerprint, current.GetValueOrDefault(f.Path))).ToArray(),
                Guard, false, previous);
            if (removeAdded)
                foreach (var added in plan.Added)
                {
                    Guard();
                    if (Transfers.ReadCurrent(folder, added.Path) == added.Source)
                    {
                        folder.Delete(folder.Inspect(added.Path)!.Path);
                        removed++;
                    }
                }
            Write(operation with { State = "complete" });
            var baselineFile = Path.Combine(history, "baseline.json");
            if (File.Exists(baselineFile)) File.Delete(baselineFile);
            return removed;
        }
        catch (Exception error)
        {
            Write(operation with { State = "interrupted" });
            throw new IOException(error.Message + "\nБэкап до восстановления: " + operation.Id, error);
        }
    }

    private static BackupFile[] BackUp(SaveFolder folder, FolderState snapshot, string id)
    {
        var work = Settings.NewWorkFolder();
        var items = new List<BackupFile>();
        try
        {
            foreach (var entry in snapshot.Files)
            {
                var file = Path.Combine(work, Guid.NewGuid().ToString("N"));
                folder.Read(entry.Path, file);
                if (Transfers.Hash(file) != entry.Fingerprint)
                    throw new IOException("Файл изменился перед созданием бэкапа: " + entry.Path);
                var backup = entry.Path + "." + id + ".bak";
                Transfers.SaveBackup(folder, file, backup);
                items.Add(new(entry.Path, backup, entry.Fingerprint));
            }
            return items.ToArray();
        }
        catch
        {
            DeleteIncompleteBackup(folder, items);
            throw;
        }
        finally { Transfers.Cleanup(work); }
    }

    private static void DeleteIncompleteBackup(SaveFolder folder, IEnumerable<BackupFile> files)
    {
        foreach (var file in files)
            try { folder.Delete(file.Backup); } catch { }
    }

    private Operation? Latest(bool onDeck)
    {
        if (!Directory.Exists(history)) return null;
        return Directory.EnumerateFiles(history, "operation-*.json")
            .Select(Read<Operation>).OfType<Operation>()
            .Where(o => (o.State is "complete" or "interrupted" or "prepared")
                && ((onDeck ? o.Deck : o.Pc).Length > 0
                    || (o.Kind == "transfer" && o.DeckDestination == onDeck && o.Changes.Any(c => c.Destination is null))))
            .OrderByDescending(o => o.At).FirstOrDefault();
    }

    private static T? Read<T>(string file) where T : class =>
        File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Settings.Json) : null;
    private string OperationPath(string id) => Path.Combine(history, "operation-" + id + ".json");
    private void Write(Operation operation) => Settings.WriteJson(OperationPath(operation.Id), operation);
}

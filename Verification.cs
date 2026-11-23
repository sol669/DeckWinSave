using System.IO;

namespace GameSavePreview;

internal static class Verification
{
    public static int Run(string report)
    {
        var lines = new List<string>();
        var test = Settings.NewWorkFolder();
        try
        {
            Require(Pairing.NormalizeAddress(" steamdeck ") == "steamdeck.local", "short hostname gets local suffix");
            Require(Pairing.NormalizeAddress("steamdeck.local") == "steamdeck.local", "local suffix is not duplicated");
            Require(Pairing.NormalizeAddress("steamdeck.example.net") == "steamdeck.example.net", "qualified DNS name preserved");
            Require(Pairing.NormalizeAddress("192.0.2.107/24") == "192.0.2.107" &&
                Pairing.NormalizeAddress("198.51.100.4/32") == "198.51.100.4", "IPv4 prefixes removed");
            Require(Pairing.NormalizeAddress("fd00::4/64") == "fd00::4" &&
                Pairing.NormalizeAddress("192.0.2.107/40") == "192.0.2.107/40", "IPv6 and invalid prefixes");
            lines.Add("PASS Steam Deck hostname and pasted IP normalization");

            const string legacy = """{"Address":"steamdeck.local","Port":22,"User":"deck","Fingerprint":"fixture"}""";
            var original = System.Text.Json.JsonSerializer.Deserialize<DeckSettings>(legacy, Settings.Json)!;
            Require(original.VpnAddress is null, "old pairing loads without VPN address");
            var dual = original with { VpnAddress = "198.51.100.4" };
            var tried = new List<string>();
            var selected = Pairing.FindEndpoint(dual, null, (candidate, timeout) =>
            {
                tried.Add(candidate.Address);
                Require(candidate.Fingerprint == original.Fingerprint && timeout is not null, "same pin and bounded timeout");
                if (candidate.Address == original.Address) throw new TimeoutException();
                return true;
            });
            Require(tried.SequenceEqual(new[] { original.Address, dual.VpnAddress }) &&
                selected?.Address == dual.VpnAddress, "VPN fallback after a timeout");
            tried.Clear();
            selected = Pairing.FindEndpoint(dual, dual.VpnAddress, (candidate, _) =>
            {
                tried.Add(candidate.Address);
                return candidate.Address == original.Address;
            });
            Require(tried.SequenceEqual(new[] { dual.VpnAddress, original.Address }) &&
                selected?.Address == original.Address, "fall back to home after VPN fails verification");
            lines.Add("PASS legacy pairing, VPN fallback, last address preference and shared SSH pin");

            var left = Path.Combine(test, "pc");
            var right = Path.Combine(test, "deck");
            Directory.CreateDirectory(Path.Combine(left, "slots"));
            Directory.CreateDirectory(Path.Combine(right, "slots"));
            File.WriteAllText(Path.Combine(left, "slots", "save.dat"), "PC progress");
            File.WriteAllText(Path.Combine(right, "slots", "save.dat"), "Deck progress");
            File.WriteAllText(Path.Combine(right, "slots", "new.dat"), "Deck new slot");
            File.WriteAllText(Path.Combine(left, "settings.ini"), "PC settings");
            File.WriteAllText(Path.Combine(right, "settings.ini"), "Deck settings");
            File.WriteAllText(Path.Combine(left, "unchanged.dat"), "PC-only slot");
            var game = new GameProfile("Fixture", left, right, ["settings.ini"]);
            var engine = new Sync(game, "fixture");
            using var pc = new WindowsFolder(left, false);
            using var deck = new WindowsFolder(right, false);
            var comparison = engine.Compare(pc, deck);
            Require(comparison.ToDeck is null, "first conflict requires manual choice");
            Require(comparison.Preview(false).Any(c => c.Path == "settings.ini" && !c.CanCopy), "exclusion");
            Copy(engine, pc, deck, comparison, false);
            Require(File.ReadAllText(Path.Combine(left, "slots", "save.dat")) == "Deck progress", "overwrite");
            Require(File.Exists(Path.Combine(left, "slots", "new.dat")), "new slot copied");
            Require(File.ReadAllText(Path.Combine(left, "settings.ini")) == "PC settings", "settings untouched");
            var restore = engine.PlanRestore(pc, false);
            Require(restore.Files.Any(f => f.Path == "unchanged.dat"), "full backup includes untouched save");
            Require(restore.Added.Single().Path == "slots/new.dat", "journal remembers new slot");
            engine.Restore(pc, false, restore, true);
            Require(File.ReadAllText(Path.Combine(left, "slots", "save.dat")) == "PC progress", "same-device rollback");
            Require(!File.Exists(Path.Combine(left, "slots", "new.dat")), "unchanged added slot can be removed");
            Require(engine.PlanRestore(pc, false).Files.Any(f => f.Path == "slots/new.dat"), "rollback of restore stored");
            lines.Add("PASS manual first transfer, exclusions, full backup, restore on same device, added-slot cleanup");

            Copy(engine, pc, deck, engine.Compare(pc, deck), false);
            File.WriteAllText(Path.Combine(left, "slots", "save.dat"), "new PC progress");
            Require(engine.Compare(pc, deck).ToDeck == true, "propose the changed PC");
            var edited = new Sync(game with { Exceptions = ["settings.ini", "unchanged.dat"] }, "fixture");
            Require(edited.Compare(pc, deck).ToDeck is null, "edited rules require a fresh baseline");
            var editedRestore = edited.PlanRestore(pc, false);
            Require(editedRestore.Files.Any(f => f.Path == "slots/save.dat") &&
                editedRestore.Files.All(f => f.Path != "unchanged.dat"), "backup survives edits and respects current exclusions");
            File.WriteAllText(Path.Combine(right, "slots", "save.dat"), "new Deck progress");
            Require(engine.Compare(pc, deck).ToDeck is null, "two-sided conflict");
            comparison = engine.Compare(pc, deck);
            File.WriteAllText(Path.Combine(right, "slots", "save.dat"), "changed after preview");
            try { Copy(engine, pc, deck, comparison, false); throw new InvalidOperationException("accepted stale preview"); }
            catch (IOException e) when (e.Message.Contains("изменились")) { }
            lines.Add("PASS automatic direction after one-sided change, conflict, stale preview rejected");
            lines.Add("PASS backup survives edits to exclusions and restore respects current exclusions");
            var deckBeforeBackup = File.ReadAllText(Path.Combine(right, "slots", "save.dat"));
            var manual = engine.CreateBackup(pc, false);
            Require(manual.Deck.Length == 0 && manual.Pc.Length == 3 &&
                manual.Pc.All(file => File.Exists(Path.Combine(left, file.Backup))), "manual backup on PC");
            Require(File.ReadAllText(Path.Combine(left, "slots", "save.dat")) == "new PC progress" &&
                File.ReadAllText(Path.Combine(right, "slots", "save.dat")) == deckBeforeBackup, "active files untouched");
            File.WriteAllText(Path.Combine(left, "slots", "save.dat"), "after manual backup");
            restore = engine.PlanRestore(pc, false);
            Require(restore.Operation.Id == manual.Id, "manual backup appears as latest for PC");
            engine.Restore(pc, false, restore, false);
            Require(File.ReadAllText(Path.Combine(left, "slots", "save.dat")) == "new PC progress", "manual backup restores correctly");
            lines.Add("PASS manual backup is local to the selected device and can be restored");

            if (Settings.Read().Deck is DeckSettings paired)
            {
                var fingerprint = Pairing.Probe(paired);
                Require(Pairing.OpenSshDigest(fingerprint) == Pairing.OpenSshDigest(paired.Fingerprint), "live host key");
                using var session = Pairing.Connect(paired);
                var create = session.ExecuteCommand("mktemp -d \"$HOME/DeckWinSave-test.XXXXXXXX\"");
                create.Check();
                string remote = create.Output.Trim();
                Require(System.Text.RegularExpressions.Regex.IsMatch(remote,
                    @"^/[a-zA-Z0-9_/-]+/DeckWinSave-test\.[a-zA-Z0-9]+$"), "fixture path");
                try
                {
                    using var remoteFolder = new DeckFolder(paired, remote);
                    var live = new Sync(game with { DeckPath = remote }, paired.Fingerprint);
                    comparison = live.Compare(pc, remoteFolder);
                    Require(comparison.ToDeck == true, "empty live destination");
                    Copy(live, pc, remoteFolder, comparison, true);
                    File.WriteAllText(Path.Combine(left, "slots", "save.dat"), "test local overwrite");
                    Copy(live, pc, remoteFolder, live.Compare(pc, remoteFolder), false);
                    Require(Transfers.ReadFingerprint(pc, "slots/save.dat") ==
                        Transfers.ReadFingerprint(remoteFolder, "slots/save.dat"), "round trip");
                    Require(live.PlanRestore(remoteFolder, true).Files.Length > 0, "remote backup exists");
                    var remoteBackup = live.CreateBackup(remoteFolder, true);
                    Require(remoteBackup.Pc.Length == 0 && remoteBackup.Deck.Length > 0 &&
                        live.PlanRestore(remoteFolder, true).Operation.Id == remoteBackup.Id, "manual backup on Steam Deck");
                }
                finally { session.ExecuteCommand("rm -rf -- '" + remote + "'").Check(); }
                lines.Add("PASS pinned SFTP on Steam Deck, both directions and remote backups in isolated fixture");
            }
            else
            {
                var command = Pairing.PublicKeyCommand();
                Require(File.Exists(Settings.Key) && File.Exists(Settings.PublicKey) &&
                    command.Contains("restrict ssh-ed25519 "), "initial key generation");
                lines.Add("PASS fresh Windows SSH key generated");
            }
            File.WriteAllLines(report, lines);
            return 0;
        }
        catch (Exception e) { lines.Add(e.ToString()); File.WriteAllLines(report, lines); return 1; }
        finally { Transfers.Cleanup(test); }
    }

    private static void Copy(Sync sync, SaveFolder pc, SaveFolder deck, Comparison comparison, bool toDeck) =>
        sync.Transfer(pc, deck, comparison, toDeck, comparison.Preview(toDeck).Where(c => c.CanCopy).ToArray());
    private static void Require(bool result, string label)
    {
        if (!result) throw new InvalidOperationException("FAIL " + label);
    }
}

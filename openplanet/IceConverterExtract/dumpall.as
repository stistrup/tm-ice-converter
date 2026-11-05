// the whole GameData tree, plain extract only; files already on disk at full size are skipped, so a rerun resumes.
// The hook method loads the file into the game (a Collection crashed it), so cut files only go to dump-cut.txt;
// the hook pass takes a reviewed hook-list.txt.
// Unopened packs list no files, but load by path: a second phase follows the ref tables of everything that leads to geometry.

bool g_dumpStop = false;
int g_dumpDone = 0, g_dumpSkipped = 0, g_dumpCut = 0, g_dumpFailed = 0;
array<string> g_dumpEmptyFolders;
uint64 g_dumpLastYield = 0;
array<string> g_dumpFollow;
const array<string> FOLLOWED_KINDS = {
    ".item.gbx", ".prefab.gbx", ".staticobject.gbx", ".dynaobject.gbx", ".mesh.gbx", ".material.gbx",
    ".texture.gbx", ".vegettreemodel.gbx", ".edclassic.gbx", ".gamectnblockinfoclassic.gbx", ".blockinfo.gbx" };

bool Followed(const string &in path)
{
    string lower = path.ToLower();
    for (uint i = 0; i < FOLLOWED_KINDS.Length; i++) if (lower.EndsWith(FOLLOWED_KINDS[i])) return true;
    return false;
}

// Openplanet aborts a script that runs 1 s without yielding; big textures take ~8 ms each
void DumpYield()
{
    if (Time::Now - g_dumpLastYield < 100) return;
    yield();
    g_dumpLastYield = Time::Now;
}

void RenderDumpMenu()
{
    if (UI::MenuItem("\\$9cf" + Icons::Snowflake + "\\$z Extract everything (GameData)", "", false, !g_running)) {
        startnew(RunDumpAll);
    }
    if (UI::MenuItem("\\$9cf" + Icons::Snowflake + "\\$z Extract hook-list files (full size)", "", false, !g_running)) {
        startnew(RunHookList);
    }
    if (g_running && UI::MenuItem("\\$f99" + Icons::Stop + "\\$z Stop extracting")) g_dumpStop = true;
}

bool FullOnDisk(CSystemFidFile@ fid, const string &in path)
{
    uint size = DiskSize(path);
    return size > 0 && (fid.Compressed || size == fid.ByteSize);
}

void DumpFile(CSystemFidFile@ fid, const string &in path)
{
    if (FullOnDisk(fid, path)) { g_dumpSkipped++; return; }
    if (IO::FileExists(OnDisk(path))) IO::Delete(OnDisk(path));
    Fids::Extract(fid);
    if (FullOnDisk(fid, path)) { g_dumpDone++; return; }
    uint size = DiskSize(path);
    if (size > 0) g_dumpCut++; else g_dumpFailed++;
    // written at once: a crash later must not lose the list
    IO::File f(IO::FromDataFolder("Plugins/IceConverterExtract/dump-cut.txt"), IO::FileMode::Append);
    f.WriteLine(path + " bytesize=" + fid.ByteSize + " disk=" + size);
    f.Close();
}

void DumpFolder(CSystemFidsFolder@ folder, const string &in path)
{
    if (g_dumpStop) return;
    if (folder.Leaves.Length == 0 && folder.Trees.Length == 0) g_dumpEmptyFolders.InsertLast(path);
    for (uint i = 0; i < folder.Leaves.Length && !g_dumpStop; i++) {
        string file = path + "/" + folder.Leaves[i].FileName;
        DumpFile(folder.Leaves[i], file);
        if (Followed(file)) g_dumpFollow.InsertLast(file);
        DumpYield();
    }
    for (uint i = 0; i < folder.Trees.Length && !g_dumpStop; i++) DumpFolder(folder.Trees[i], path + "/" + folder.Trees[i].DirName);
}

void RunDumpAll()
{
    g_running = true;
    g_dumpStop = false;
    g_dumpDone = 0; g_dumpSkipped = 0; g_dumpCut = 0; g_dumpFailed = 0;
    string cutList = IO::FromDataFolder("Plugins/IceConverterExtract/dump-cut.txt");
    if (IO::FileExists(cutList)) IO::Delete(cutList);
    g_dumpEmptyFolders.RemoveRange(0, g_dumpEmptyFolders.Length);
    g_dumpFollow.RemoveRange(0, g_dumpFollow.Length);
    g_followed.DeleteAll();
    g_byTable = 0;
    g_stillMissing.RemoveRange(0, g_stillMissing.Length);
    auto root = Fids::GetGameFolder("GameData");
    if (root is null) {
        UI::ShowNotification("Ice Converter", "No GameData folder");
        g_running = false;
        return;
    }
    // pack folders list nothing until opened
    Fids::UpdateTree(root);
    uint64 start = Time::Now;
    g_dumpLastYield = start;
    startnew(DumpProgress);
    DumpFolder(root, "GameData");
    for (uint i = 0; i < g_dumpFollow.Length && !g_dumpStop; i++) {
        FollowTable(g_dumpFollow[i], true);
        DumpYield();
    }

    IO::File f(IO::FromDataFolder("Plugins/IceConverterExtract/dump-log.txt"), IO::FileMode::Write);
    string summary = (g_dumpStop ? "STOPPED" : "DONE") + " extracted=" + g_dumpDone + " already=" + g_dumpSkipped
        + " byReference=" + g_byTable + " unavailableReferences=" + g_stillMissing.Length
        + " cut=" + g_dumpCut + " failed=" + g_dumpFailed + " emptyFolders=" + g_dumpEmptyFolders.Length + " seconds=" + ((Time::Now - start) / 1000);
    f.WriteLine(summary);
    for (uint i = 0; i < g_dumpEmptyFolders.Length; i++) f.WriteLine("empty " + g_dumpEmptyFolders[i]);
    for (uint i = 0; i < g_stillMissing.Length; i++) f.WriteLine("unavailable " + g_stillMissing[i]);
    f.Close();
    print(summary + " (dump-log.txt, cut files in dump-cut.txt)");
    UI::ShowNotification("Ice Converter", summary);
    g_running = false;
}

void DumpProgress()
{
    while (g_running) {
        UI::ShowNotification("Ice Converter", "Extracting: " + g_dumpDone + " new, " + g_dumpSkipped + " already there", 4000);
        sleep(15000);
    }
}

// one game path per line; only reviewed, safe file kinds go in here
void RunHookList()
{
    g_running = true;
    string listFile = IO::FromDataFolder("Plugins/IceConverterExtract/hook-list.txt");
    if (!IO::FileExists(listFile)) {
        UI::ShowNotification("Ice Converter", "No hook-list.txt");
        g_running = false;
        return;
    }
    IO::File list(listFile, IO::FileMode::Read);
    auto paths = list.ReadToEnd().Split("\n");
    list.Close();
    int full = 0, cut = 0, missing = 0;
    array<string> lines;
    for (uint i = 0; i < paths.Length; i++) {
        string path = paths[i].Trim();
        if (path.Length == 0) continue;
        auto fid = Fids::GetGame(path);
        if (fid is null) { missing++; lines.InsertLast("missing " + path); continue; }
        if (FullOnDisk(fid, path)) { full++; continue; }
        if (IO::FileExists(OnDisk(path))) IO::Delete(OnDisk(path));
        Fids::Extract(fid, true);
        bool ok = FullOnDisk(fid, path);
        if (ok) full++; else { cut++; lines.InsertLast("cut " + path + " bytesize=" + fid.ByteSize + " disk=" + DiskSize(path)); }
        yield();
    }
    IO::File f(IO::FromDataFolder("Plugins/IceConverterExtract/hook-log.txt"), IO::FileMode::Write);
    for (uint i = 0; i < lines.Length; i++) f.WriteLine(lines[i]);
    f.Close();
    string summary = "Hook list: " + full + " full, " + cut + " cut, " + missing + " missing";
    print(summary + " (hook-log.txt)");
    UI::ShowNotification("Ice Converter", summary);
    g_running = false;
}

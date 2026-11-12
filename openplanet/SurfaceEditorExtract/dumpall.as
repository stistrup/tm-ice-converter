// the whole GameData tree minus dump-exclude.txt, plain extract only; files already on disk at full size are skipped, so a rerun resumes.
// The hook method loads the file into the game (a Collection crashed it): only tree models get it, other cut files go to dump-cut.txt.
// Unopened packs list no files, but load by path: a second phase follows the ref tables of every file.

bool g_dumpStop = false;
int g_dumpExcludedCount = 0, g_dumpHooked = 0;
array<string> g_dumpUnavailable;
int g_dumpDone = 0, g_dumpSkipped = 0, g_dumpCut = 0, g_dumpFailed = 0;
array<string> g_dumpEmptyFolders;
uint64 g_dumpLastYield = 0;
array<string> g_dumpFollow;
array<string> g_dumpExcludeFolders, g_dumpExcludeKinds;

void LoadDumpExclude()
{
    g_dumpExcludeFolders.RemoveRange(0, g_dumpExcludeFolders.Length);
    g_dumpExcludeKinds.RemoveRange(0, g_dumpExcludeKinds.Length);
    IO::FileSource source("dump-exclude.txt");
    auto lines = source.ReadToEnd().Split("\n");
    for (uint i = 0; i < lines.Length; i++) {
        string line = lines[i].Trim().ToLower();
        if (line.Length == 0 || line.StartsWith("#")) continue;
        if (line.StartsWith("*")) g_dumpExcludeKinds.InsertLast(line.SubStr(1));
        else g_dumpExcludeFolders.InsertLast(line.EndsWith("/") ? line : line + "/");
    }
}

bool DumpExcluded(const string &in path, bool folder)
{
    string lower = path.ToLower() + (folder ? "/" : "");
    for (uint i = 0; i < g_dumpExcludeFolders.Length; i++) if (lower.StartsWith(g_dumpExcludeFolders[i])) return true;
    if (folder) return false;
    for (uint i = 0; i < g_dumpExcludeKinds.Length; i++) if (lower.EndsWith(g_dumpExcludeKinds[i])) return true;
    return false;
}

// every game file's ref table: block kinds are many (EDFrontier, EDTransition, ...)
bool Followed(const string &in path) { return path.ToLower().EndsWith(".gbx"); }

// Openplanet aborts a script that runs 1 s without yielding; big textures take ~8 ms each
void DumpYield()
{
    if (Time::Now - g_dumpLastYield < 100) return;
    yield();
    g_dumpLastYield = Time::Now;
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
    IO::File f(IO::FromDataFolder("Plugins/SurfaceEditorExtract/dump-cut.txt"), IO::FileMode::Append);
    f.WriteLine(path + " bytesize=" + fid.ByteSize + " disk=" + size);
    f.Close();
}

void DumpFolder(CSystemFidsFolder@ folder, const string &in path)
{
    if (g_dumpStop || DumpExcluded(path, true)) return;
    if (folder.Leaves.Length == 0 && folder.Trees.Length == 0) g_dumpEmptyFolders.InsertLast(path);
    for (uint i = 0; i < folder.Leaves.Length && !g_dumpStop; i++) {
        string file = path + "/" + folder.Leaves[i].FileName;
        if (DumpExcluded(file, false)) { g_dumpExcludedCount++; continue; }
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
    LoadDumpExclude();
    g_dumpExcludedCount = 0; g_dumpHooked = 0;
    g_dumpUnavailable.RemoveRange(0, g_dumpUnavailable.Length);
    g_dumpDone = 0; g_dumpSkipped = 0; g_dumpCut = 0; g_dumpFailed = 0;
    string cutList = IO::FromDataFolder("Plugins/SurfaceEditorExtract/dump-cut.txt");
    if (IO::FileExists(cutList)) IO::Delete(cutList);
    g_dumpEmptyFolders.RemoveRange(0, g_dumpEmptyFolders.Length);
    g_dumpFollow.RemoveRange(0, g_dumpFollow.Length);
    g_followed.DeleteAll();
    g_byTable = 0;
    g_stillMissing.RemoveRange(0, g_stillMissing.Length);
    auto root = Fids::GetGameFolder("GameData");
    if (root is null) {
        UI::ShowNotification("Surface Editor", "No GameData folder");
        g_running = false;
        return;
    }
    // pack folders list nothing until opened
    Fids::UpdateTree(root);
    uint64 start = Time::Now;
    g_dumpLastYield = start;
    g_dumpTotal = 0;
    g_dumpPhase = "Counting files";
    startnew(DumpProgress);
    g_dumpTotal = CountFiles(root, "GameData");
    g_dumpPhase = "Extracting files";
    DumpFolder(root, "GameData");
    g_dumpPhase = "Following references";
    for (uint i = 0; i < g_dumpFollow.Length && !g_dumpStop; i++) {
        FollowTable(g_dumpFollow[i], true);
        DumpYield();
    }
    g_dumpPhase = "Fetching listed files";
    if (!g_dumpStop) ExtractListed();
    g_dumpPhase = "Fetching tree models";
    if (!g_dumpStop) HookTreeModels();

    IO::File f(IO::FromDataFolder("Plugins/SurfaceEditorExtract/dump-log.txt"), IO::FileMode::Write);
    string summary = (g_dumpStop ? "STOPPED" : "DONE") + " extracted=" + g_dumpDone + " already=" + g_dumpSkipped + " excluded=" + g_dumpExcludedCount
        + " byReference=" + g_byTable + " unavailableReferences=" + g_stillMissing.Length
        + " listedUnavailable=" + g_dumpUnavailable.Length + " treesHooked=" + g_dumpHooked + " cut=" + g_dumpCut + " failed=" + g_dumpFailed + " emptyFolders=" + g_dumpEmptyFolders.Length + " seconds=" + ((Time::Now - start) / 1000);
    f.WriteLine(summary);
    for (uint i = 0; i < g_dumpEmptyFolders.Length; i++) f.WriteLine("empty " + g_dumpEmptyFolders[i]);
    for (uint i = 0; i < g_stillMissing.Length; i++) f.WriteLine("unavailable " + g_stillMissing[i]);
    for (uint i = 0; i < g_dumpUnavailable.Length; i++) f.WriteLine("listed unavailable " + g_dumpUnavailable[i]);
    f.Close();
    print(summary + " (dump-log.txt, cut files in dump-cut.txt)");
    UI::ShowNotification("Surface Editor", summary);
    g_running = false;
    WriteDumpProgress(g_dumpStop ? "stopped" : "done");
}

// the GUI starts and stops it through these files and follows extract-progress.txt
void Main()
{
    // a request left from before the plugin loaded doesn't count: the user presses Extract again
    if (IO::FileExists(PluginFile("extract-request.txt"))) IO::Delete(PluginFile("extract-request.txt"));
    while (true) {
        // the GUI tells from this that the plugin is loaded
        IO::File alive(PluginFile("alive.txt"), IO::FileMode::Write);
        alive.Write("" + Time::Stamp);
        alive.Close();
        if (IO::FileExists(PluginFile("extract-stop.txt"))) {
            IO::Delete(PluginFile("extract-stop.txt"));
            if (g_running) g_dumpStop = true;
        }
        if (!g_running && IO::FileExists(PluginFile("extract-request.txt"))) {
            IO::Delete(PluginFile("extract-request.txt"));
            startnew(RunDumpAll);
        }
        sleep(1000);
    }
}

string PluginFile(const string &in name) { return IO::FromDataFolder("Plugins/SurfaceEditorExtract/" + name); }

string g_dumpPhase = "";
uint g_dumpTotal = 0;

uint CountFiles(CSystemFidsFolder@ folder, const string &in path)
{
    if (DumpExcluded(path, true)) return 0;
    uint count = 0;
    for (uint i = 0; i < folder.Leaves.Length; i++) if (!DumpExcluded(path + "/" + folder.Leaves[i].FileName, false)) count++;
    for (uint i = 0; i < folder.Trees.Length; i++) count += CountFiles(folder.Trees[i], path + "/" + folder.Trees[i].DirName);
    DumpYield();
    return count;
}

void WriteDumpProgress(const string &in state)
{
    IO::File f(PluginFile("extract-progress.txt"), IO::FileMode::Write);
    f.WriteLine("state=" + state);
    f.WriteLine("phase=" + g_dumpPhase);
    f.WriteLine("total=" + g_dumpTotal);
    f.WriteLine("handled=" + (g_dumpDone + g_dumpSkipped + g_dumpCut + g_dumpFailed));
    f.WriteLine("new=" + g_dumpDone);
    f.WriteLine("referenced=" + g_byTable);
    f.Close();
}

void DumpProgress()
{
    uint64 notified = 0;
    while (g_running) {
        WriteDumpProgress("running");
        if (Time::Now - notified > 15000) {
            UI::ShowNotification("Surface Editor", g_dumpPhase + ": " + g_dumpDone + " new, " + g_dumpSkipped + " already there", 4000);
            notified = Time::Now;
        }
        sleep(1000);
    }
}

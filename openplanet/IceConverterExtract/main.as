// extracts what the converter needs, plus everything it references

bool g_running = false;

void RenderMenu()
{
    if (UI::MenuItem("\\$9cf" + Icons::Snowflake + "\\$z Extract block meshes", "", false, !g_running)) {
        startnew(Run);
    }
    if (UI::MenuItem("\\$9cf" + Icons::Snowflake + "\\$z Extract block definitions of the loaded map", "", false, !g_running)) {
        startnew(RunMapBlocks);
    }
    if (UI::MenuItem("\\$9cf" + Icons::Snowflake + "\\$z Extract the loaded map's environment folder", "", false, !g_running)) {
        startnew(RunEnvironment);
    }
    if (UI::MenuItem("\\$9cf" + Icons::Snowflake + "\\$z Extract vegetation models (full size)", "", false, !g_running)) {
        startnew(RunVeget);
    }
    RenderDumpMenu();
    RenderShadowsMenu();
}

uint DiskSize(const string &in gamePath)
{
    string disk = OnDisk(gamePath);
    if (!IO::FileExists(disk)) return 0;
    IO::File f(disk, IO::FileMode::Read);
    uint size = uint(f.Size());
    f.Close();
    return size;
}

// the plain extract cuts tree models at 4/8 KB; the hook method may not
void RunVeget()
{
    g_running = true;
    // pack folders list nothing until opened: every tree model already on disk (cut or not), looked up by path;
    // only <env>/Media/VegetTreeModel, the whole tree times out
    array<string> paths;
    auto envs = IO::IndexFolder(OnDisk("GameData"), false);
    for (uint e = 0; e < envs.Length; e++) {
        string env = envs[e].Replace("\\", "/");
        if (env.EndsWith("/")) env = env.SubStr(0, env.Length - 1);
        string name = env.SubStr(env.LastIndexOf("/") + 1);
        string folder = OnDisk("GameData/" + name + "/Media/VegetTreeModel");
        if (!IO::FolderExists(folder)) continue;
        auto files = IO::IndexFolder(folder, false);
        for (uint i = 0; i < files.Length; i++) {
            string file = files[i].Replace("\\", "/");
            if (file.ToLower().EndsWith(".vegettreemodel.gbx"))
                paths.InsertLast("GameData/" + name + "/Media/VegetTreeModel/" + file.SubStr(file.LastIndexOf("/") + 1));
        }
        yield();
    }
    print("vegetation: " + paths.Length + " tree models");
    array<string> lines;
    int full = 0, cut = 0;
    {
        for (uint i = 0; i < paths.Length; i++) {
            string path = paths[i].Trim();
            if (path.Length == 0) continue;
            auto fid = Fids::GetGame(path);
            if (fid is null) { lines.InsertLast(path + " NOT FOUND"); continue; }
            if (IO::FileExists(OnDisk(path))) IO::Delete(OnDisk(path));
            Fids::Extract(fid);
            uint plain = DiskSize(path);
            uint hooked = 0;
            if (plain != fid.ByteSize) {
                if (IO::FileExists(OnDisk(path))) IO::Delete(OnDisk(path));
                Fids::Extract(fid, true);
                hooked = DiskSize(path);
            }
            bool ok = (hooked > 0 ? hooked : plain) == fid.ByteSize;
            if (ok) full++; else cut++;
            lines.InsertLast(path + " bytesize=" + fid.ByteSize + " compressed=" + fid.Compressed + " plain=" + plain + " hook=" + hooked + (ok ? " ok" : " CUT"));
            yield();
        }
    }
    IO::File f(IO::FromDataFolder("Plugins/IceConverterExtract/veget-sizes.txt"), IO::FileMode::Write);
    for (uint i = 0; i < lines.Length; i++) f.WriteLine(lines[i]);
    f.Close();
    print("DONE vegetation full=" + full + " cut=" + cut + " (veget-sizes.txt)");
    UI::ShowNotification("Ice Converter", "Vegetation models: " + full + " full, " + cut + " cut");
    g_running = false;
}

int g_ok = 0, g_failed = 0;

void ExtractFolder(CSystemFidsFolder@ folder, const string &in path)
{
    for (uint i = 0; i < folder.Leaves.Length; i++) {
        if (Fids::Extract(folder.Leaves[i])) g_ok++; else g_failed++;
        if ((g_ok + g_failed) % 100 == 0) yield();
    }
    for (uint i = 0; i < folder.Trees.Length; i++) ExtractFolder(folder.Trees[i], path + "/" + folder.Trees[i].DirName);
    for (uint i = 0; i < folder.Leaves.Length; i++) FollowTable(path + "/" + folder.Leaves[i].FileName, true);
}

void RunEnvironment()
{
    g_running = true;
    auto map = GetApp().RootMap;
    if (map is null) {
        UI::ShowNotification("Ice Converter", "Load a map first (editor or play)");
        g_running = false;
        return;
    }
    string env = map.CollectionName;
    auto folder = Fids::GetGameFolder("GameData/" + env);
    if (folder is null) {
        UI::ShowNotification("Ice Converter", "No GameData/" + env + " folder");
        g_running = false;
        return;
    }
    g_ok = 0; g_failed = 0; g_byTable = 0;
    g_followed.DeleteAll();
    g_stillMissing.RemoveRange(0, g_stillMissing.Length);
    ExtractFolder(folder, "GameData/" + env);
    print("DONE " + env + " extracted=" + g_ok + " (+" + g_byTable + " referenced) failed=" + g_failed);
    UI::ShowNotification("Ice Converter", env + ": extracted " + g_ok + " (+" + g_byTable + " referenced), failed " + g_failed);
    g_running = false;
}

// env block definitions: take them from the loaded map
void RunMapBlocks()
{
    g_running = true;
    auto map = GetApp().RootMap;
    if (map is null) {
        UI::ShowNotification("Ice Converter", "Load a map first (editor or play)");
        g_running = false;
        return;
    }
    dictionary seen;
    array<string> lines;
    int ok = 0, failed = 0;
    for (uint i = 0; i < map.Blocks.Length; i++) {
        auto info = map.Blocks[i].BlockInfo;
        if (info is null) continue;
        auto fid = GetFidFromNod(info);
        if (fid is null) continue;
        // pack files all say "<virtual>"
        string path = fid.FileName;
        if (seen.Exists(path)) continue;
        seen.Set(path, true);
        bool done = Fids::Extract(fid);
        if (done) ok++; else failed++;
        lines.InsertLast((done ? "ok " : "failed ") + path);
        if (i % 200 == 0) yield();
    }
    IO::File f(IO::FromDataFolder("Plugins/IceConverterExtract/blockinfo-paths.txt"), IO::FileMode::Write);
    for (uint i = 0; i < lines.Length; i++) f.WriteLine(lines[i]);
    f.Close();
    print("DONE block definitions extracted=" + ok + " failed=" + failed);
    UI::ShowNotification("Ice Converter", "Block definitions: extracted " + ok + ", failed " + failed);
    g_running = false;
}

dictionary g_followed;
int g_deeper = 0;

void ExtractNod(CMwNod@ nod)
{
    if (nod is null) return;
    auto fid = GetFidFromNod(nod);
    if (fid is null) return;
    if (Fids::Extract(fid)) g_deeper++;
    FollowRefs(fid, nod);
}

void FollowRefs(CSystemFidFile@ fid, CMwNod@ nod = null)
{
    string key = fid.FileName;
    if (g_followed.Exists(key)) return;
    g_followed.Set(key, true);
    if (nod is null) @nod = Fids::Preload(fid);
    auto prefab = cast<CPlugPrefab>(nod);
    if (prefab !is null) {
        for (uint i = 0; i < prefab.Ents.Length; i++) ExtractNod(prefab.Ents[i].Model);
        yield();
        return;
    }
    auto staticObject = cast<CPlugStaticObjectModel>(nod);
    if (staticObject !is null) ExtractNod(staticObject.Mesh);
}

// follow each file's reference table all the way down
int g_byTable = 0;
array<string> g_stillMissing;

string OnDisk(const string &in gamePath) { return IO::FromDataFolder("Extract/" + gamePath); }

bool Extracted(const string &in gamePath)
{
    string disk = OnDisk(gamePath);
    if (!IO::FileExists(disk)) return false;
    IO::File f(disk, IO::FileMode::Read);
    bool empty = f.Size() == 0;
    f.Close();
    if (empty) IO::Delete(disk);
    return !empty;
}

string Normalize(const string &in path)
{
    auto parts = path.Replace("\\", "/").Split("/");
    array<string> kept;
    for (uint i = 0; i < parts.Length; i++) {
        if (parts[i] == "" || parts[i] == ".") continue;
        if (parts[i] == "..") { if (kept.Length > 0) kept.RemoveLast(); continue; }
        kept.InsertLast(parts[i]);
    }
    return Text::Join(kept, "/");
}

string DirOf(const string &in path)
{
    int slash = path.LastIndexOf("/");
    return slash < 0 ? "" : path.SubStr(0, slash);
}

void Skip(MemoryBuffer@ buf, uint bytes) { buf.Seek(buf.GetPosition() + bytes); }

string ReadGbxString(MemoryBuffer@ buf)
{
    uint length = buf.ReadUInt32();
    return length == 0 ? "" : buf.ReadString(length);
}

void ReadFolders(MemoryBuffer@ buf, const string &in prefix, array<string>@ folders)
{
    uint count = buf.ReadUInt32();
    for (uint i = 0; i < count; i++) {
        string folder = prefix + ReadGbxString(buf) + "/";
        folders.InsertLast(folder);
        ReadFolders(buf, folder, folders);
    }
}

array<string> ReferencedFiles(const string &in gamePath)
{
    array<string> result;
    IO::File f(OnDisk(gamePath), IO::FileMode::Read);
    auto buf = f.Read(f.Size());
    f.Close();
    if (buf.GetSize() < 13 || buf.ReadString(3) != "GBX") return result;
    uint16 version = buf.ReadUInt16();
    if (version >= 3) Skip(buf, 3);
    if (version >= 4) Skip(buf, 1);
    buf.ReadUInt32();
    if (version >= 6) Skip(buf, buf.ReadUInt32());
    buf.ReadUInt32();
    uint external = buf.ReadUInt32();
    if (external == 0) return result;
    uint ancestorLevel = buf.ReadUInt32();
    array<string> folders;
    ReadFolders(buf, "", folders);
    string base = DirOf(gamePath);
    for (uint i = 0; i < ancestorLevel; i++) base = DirOf(base);
    for (uint i = 0; i < external; i++) {
        uint flags = buf.ReadUInt32();
        bool byName = (flags & 4) == 0;
        string name = byName ? ReadGbxString(buf) : "";
        if (!byName) buf.ReadUInt32();
        buf.ReadUInt32();
        if (version >= 5) buf.ReadUInt32();
        if (!byName) continue;
        uint folder = buf.ReadUInt32();
        string relative = (folder > 0 && folder <= folders.Length ? folders[folder - 1] : "") + name;
        result.InsertLast(Normalize(base + "/" + relative));
    }
    return result;
}


void FollowTable(const string &in gamePath, bool extractedNow = false)
{
    string key = gamePath.ToLower();
    if (g_followed.Exists(key)) return;
    g_followed.Set(key, true);
    if (!Extracted(gamePath)) {
        auto fid = Fids::GetGame(gamePath);
        if (fid is null || !Fids::Extract(fid) || !Extracted(gamePath)) {
            g_stillMissing.InsertLast(gamePath);
            return;
        }
        if (!extractedNow) g_byTable++;
    }
    if (!gamePath.ToLower().EndsWith(".gbx")) return;
    array<string> refs;
    try {
        refs = ReferencedFiles(gamePath);
    } catch {
        warn("can't read the references of " + gamePath + ": " + getExceptionInfo());
    }
    DumpYield();
    for (uint i = 0; i < refs.Length; i++) FollowTable(refs[i]);
}

// 0 ok, 1 not found, 2 failed
int Extract(const string &in path)
{
    array<string> candidates = { "GameData/Stadium/" + path, "Stadium/" + path, "GameData/" + path };
    for (uint i = 0; i < candidates.Length; i++) {
        if (Extracted(candidates[i])) { FollowTable(candidates[i], true); return 0; }
    }
    bool found = false;
    for (uint i = 0; i < candidates.Length; i++) {
        auto fid = Fids::GetGame(candidates[i]);
        if (fid is null) continue;
        found = true;
        if (!Fids::Extract(fid) || !Extracted(candidates[i])) continue;
        if (path.EndsWith(".Prefab.Gbx")) FollowRefs(fid);
        FollowTable(candidates[i], true);
        return 0;
    }
    return found ? 2 : 1;
}

void Run()
{
    g_running = true;
    IO::FileSource meshRefs("mesh-refs.txt");
    auto paths = meshRefs.ReadToEnd().Split("\n");
    string viewRefs = IO::FromDataFolder("Plugins/IceConverterExtract/view-refs.txt");
    if (IO::FileExists(viewRefs)) {
        IO::File f(viewRefs, IO::FileMode::Read);
        auto more = f.ReadToEnd().Split("\n");
        for (uint i = 0; i < more.Length; i++) paths.InsertLast(more[i]);
    }

    int ok = 0, missing = 0, failed = 0;
    g_followed.DeleteAll();
    g_deeper = 0; g_byTable = 0;
    g_stillMissing.RemoveRange(0, g_stillMissing.Length);
    for (uint i = 0; i < paths.Length; i++) {
        string path = paths[i].Trim();
        if (path.Length == 0) continue;

        int result = Extract(path);
        if (result == 0) {
            ok++;
        } else if (result == 1) {
            missing++;
            if (missing <= 10) warn("not found: " + path);
        } else {
            failed++;
            if (failed <= 10) warn("extract failed: " + path);
        }

        if (i % 50 == 0) {
            trace("progress " + i + "/" + paths.Length);
            yield();
        }
    }
    IO::File list(IO::FromDataFolder("Plugins/IceConverterExtract/still-missing.txt"), IO::FileMode::Write);
    for (uint i = 0; i < g_stillMissing.Length; i++) list.WriteLine(g_stillMissing[i]);
    list.Close();
    int referenced = g_deeper + g_byTable;
    print("DONE extracted=" + ok + " (+" + referenced + " referenced) missing=" + missing + " failed=" + failed
        + " unavailable references=" + g_stillMissing.Length + " (still-missing.txt)");
    UI::ShowNotification("Ice Converter", "Extracted " + ok + " (+" + referenced + " referenced), missing " + missing + ", failed " + failed
        + ", " + g_stillMissing.Length + " references unavailable");
    g_running = false;
}

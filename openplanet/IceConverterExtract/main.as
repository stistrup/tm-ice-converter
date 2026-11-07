// extracts what the converter needs, plus everything it references

bool g_running = false;

void RenderMenu()
{
    if (UI::MenuItem("\\$9cf" + Icons::Snowflake + "\\$z Extract game files", "", false, !g_running)) startnew(RunDumpAll);
    if (g_running && UI::MenuItem("\\$f99" + Icons::Stop + "\\$z Stop extracting")) g_dumpStop = true;
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

// the plain extract cuts tree models at 4/8 KB; the hook method gets them whole
void HookTreeModels()
{
    // pack folders list nothing until opened: tree models already on disk, looked up by path
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
    }
    for (uint i = 0; i < paths.Length && !g_dumpStop; i++) {
        auto fid = Fids::GetGame(paths[i]);
        // compressed or not, a whole tree model is ByteSize on disk
        if (fid is null || DiskSize(paths[i]) == fid.ByteSize) continue;
        if (IO::FileExists(OnDisk(paths[i]))) IO::Delete(OnDisk(paths[i]));
        Fids::Extract(fid, true);
        if (DiskSize(paths[i]) == fid.ByteSize) g_dumpHooked++; else g_dumpCut++;
        yield();
    }
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

// mesh-refs.txt and the files conversions asked for (view-refs.txt): found by path even where packs list nothing
void ExtractListed()
{
    IO::FileSource meshRefs("mesh-refs.txt");
    auto paths = meshRefs.ReadToEnd().Split("\n");
    string viewRefs = IO::FromDataFolder("Plugins/IceConverterExtract/view-refs.txt");
    if (IO::FileExists(viewRefs)) {
        IO::File f(viewRefs, IO::FileMode::Read);
        auto more = f.ReadToEnd().Split("\n");
        for (uint i = 0; i < more.Length; i++) paths.InsertLast(more[i]);
    }
    for (uint i = 0; i < paths.Length && !g_dumpStop; i++) {
        string path = paths[i].Trim();
        if (path.Length == 0) continue;
        if (Extract(path) != 0) g_dumpUnavailable.InsertLast(path);
        DumpYield();
    }
}

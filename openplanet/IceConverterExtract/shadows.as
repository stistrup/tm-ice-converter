// computes shadows for every map in a folder under Maps and saves each in place

[Setting hidden]
string S_ShadowFolder = "";

[SettingsTab name="Shadow folder"]
void RenderShadowFolderSetting()
{
    if (!UI::BeginCombo("Folder under Maps", S_ShadowFolder)) return;
    auto folders = MapFolders();
    for (uint i = 0; i < folders.Length; i++)
        if (UI::Selectable(folders[i], folders[i] == S_ShadowFolder)) S_ShadowFolder = folders[i];
    UI::EndCombo();
}

[Setting category="Shadows" name="Quality"]
CGameEditorPluginMap::EShadowsQuality S_ShadowQuality = CGameEditorPluginMap::EShadowsQuality::High;

[Setting category="Shadows" name="Skip maps that already have this quality"]
bool S_SkipShadowed = true;

array<string> g_mapFolders;
uint64 g_mapFoldersAt = 0;

void RenderShadowsMenu()
{
    if (!UI::BeginMenu("\\$9cf" + Icons::Snowflake + "\\$z Compute shadows for every map in", !g_running)) return;
    if (g_mapFolders.Length == 0 || Time::Now - g_mapFoldersAt > 2000) {
        g_mapFolders = MapFolders();
        g_mapFoldersAt = Time::Now;
    }
    for (uint i = 0; i < g_mapFolders.Length; i++) {
        if (UI::MenuItem(g_mapFolders[i], "", g_mapFolders[i] == S_ShadowFolder)) {
            S_ShadowFolder = g_mapFolders[i];
            startnew(RunShadows);
        }
    }
    UI::EndMenu();
}

// folders under Maps that hold maps
array<string> MapFolders()
{
    string root = IO::FromUserGameFolder("Maps/").Replace("\\", "/");
    auto files = IO::IndexFolder(root, true);
    array<string> folders;
    for (uint i = 0; i < files.Length; i++) {
        string file = files[i].Replace("\\", "/");
        if (!file.ToLower().EndsWith(".map.gbx") || !file.StartsWith(root)) continue;
        string folder = Path::GetDirectoryName(file.SubStr(root.Length));
        while (folder.EndsWith("/")) folder = folder.SubStr(0, folder.Length - 1);
        if (folder.Length > 0 && folders.Find(folder) < 0) folders.InsertLast(folder);
    }
    folders.SortAsc();
    return folders;
}

void RunShadows()
{
    g_running = true;
    string folder = S_ShadowFolder.Trim();
    string dir = IO::FromUserGameFolder("Maps/" + folder);
    if (folder.Length == 0 || !IO::FolderExists(dir)) {
        UI::ShowNotification("Ice Converter", "Pick a folder in the plugin settings (Shadow folder) first");
        g_running = false;
        return;
    }

    auto files = IO::IndexFolder(dir, false);
    array<string> maps;
    for (uint i = 0; i < files.Length; i++)
        if (files[i].ToLower().EndsWith(".map.gbx")) maps.InsertLast(folder + "/" + Path::GetFileName(files[i]));
    maps.SortAsc();

    int done = 0, skipped = 0, failed = 0;
    for (uint i = 0; i < maps.Length; i++) {
        trace("shadows " + (i + 1) + "/" + maps.Length + ": " + maps[i]);
        int result = ShadowMap(maps[i]);
        if (result == 0) done++;
        else if (result == 1) skipped++;
        else { failed++; warn("shadows failed: " + maps[i]); }
    }
    BackToMenu();
    print("SHADOWS DONE computed=" + done + " skipped=" + skipped + " failed=" + failed);
    UI::ShowNotification("Ice Converter", "Shadows: " + done + " computed, " + skipped + " skipped, " + failed + " failed");
    g_running = false;
}

// 0 computed and saved, 1 skipped, 2 failed
int ShadowMap(const string &in map)
{
    auto app = cast<CTrackMania>(GetApp());
    BackToMenu();
    app.ManiaTitleControlScriptAPI.EditMap(map, "", "");

    auto editor = WaitForEditor(120000);
    if (editor is null) return 2;
    auto plugin = editor.PluginMapType;
    if (S_SkipShadowed && plugin.CurrentShadowsQuality == S_ShadowQuality) return 1;

    plugin.ComputeShadows1(S_ShadowQuality);
    uint start = Time::Now;
    while (plugin.CurrentShadowsQuality != S_ShadowQuality || !plugin.IsEditorReadyForRequest) {
        if (Time::Now - start > 1800000) return 2;
        yield();
    }

    string path = IO::FromUserGameFolder("Maps/" + map);
    int64 before = IO::FileModifiedTime(path);
    plugin.SaveMap(map);
    start = Time::Now;
    while (IO::FileModifiedTime(path) == before) {
        if (Time::Now - start > 60000) return 2;
        yield();
    }
    return 0;
}

CGameCtnEditorFree@ WaitForEditor(uint timeout)
{
    auto app = cast<CTrackMania>(GetApp());
    uint start = Time::Now;
    while (Time::Now - start < timeout) {
        auto editor = cast<CGameCtnEditorFree>(app.Editor);
        if (editor !is null && editor.PluginMapType !is null && editor.PluginMapType.IsEditorReadyForRequest) return editor;
        yield();
    }
    return null;
}

void BackToMenu()
{
    auto app = cast<CTrackMania>(GetApp());
    if (app.Editor !is null) app.BackToMainMenu();
    while (app.Editor !is null || app.ManiaTitleControlScriptAPI is null || !app.ManiaTitleControlScriptAPI.IsReady) yield();
}

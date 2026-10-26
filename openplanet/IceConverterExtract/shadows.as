// computes shadows for every map in a folder under Maps and saves each in place

[Setting category="Shadows" name="Folder under Maps"]
string S_ShadowFolder = "";

[Setting category="Shadows" name="Quality"]
CGameEditorPluginMap::EShadowsQuality S_ShadowQuality = CGameEditorPluginMap::EShadowsQuality::High;

[Setting category="Shadows" name="Skip maps that already have this quality"]
bool S_SkipShadowed = true;

void RunShadows()
{
    g_running = true;
    string folder = S_ShadowFolder.Trim();
    string dir = IO::FromUserGameFolder("Maps/" + folder);
    if (folder.Length == 0 || !IO::FolderExists(dir)) {
        UI::ShowNotification("Ice Converter", "Set Shadows > Folder under Maps in the plugin settings first");
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

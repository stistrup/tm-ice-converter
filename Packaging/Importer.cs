using System.Diagnostics;

namespace TmIceConverter;

// pieces -> FBX (Blender) -> item (NadeoImporter), then the fixes the importer can't do
internal static class Importer
{
    private sealed record FbxObject(string Name, string Material, int Triangles);

    public static string? Import(string name, List<MeshPiece> pieces, string? waypoint, string? environment, string meshDir, string json, string buildScript, out Dictionary<GBX.NET.Engines.Plug.CPlugSurface.MaterialId, int> surfaces)
    {
        surfaces = [];
        var materials = pieces.Select(p => p.Material).Distinct().Order().ToList();
        // the importer ignores PhysicsId unless set explicitly
        var physics = MaterialLib.Surfaces(materials.Where(m => !m.StartsWith(MaterialLib.NativePrefix)));
        foreach (var m in materials.Where(m => m.StartsWith(MaterialLib.NativePrefix))) physics[m] = MaterialLib.NativeSurface(m, environment);
        if (materials.FirstOrDefault(m => !m.StartsWith(MaterialLib.NativePrefix) && MaterialLib.NotIce.Contains(physics[m])) is { } dry)
            return $"material {dry} drives as {physics[dry]}; map it to an ice material in data/ice-materials.jsonc";
        var standIn = MaterialLib.NativeStandIns(materials, physics);
        var lightmapOnly = string.Join(",", materials.Where(MaterialLib.LightmapOnly));
        if (!RunQuiet("blender", ["--background", "--factory-startup", "--python", buildScript, "--", json, Path.Combine(meshDir, name + ".fbx"), lightmapOnly], out var blenderLog))
            return "Blender failed:\n" + blenderLog;

        File.WriteAllText(Path.Combine(meshDir, name + ".MeshParams.xml"), $"""
            <MeshParams MeshType="Static" Collection="Stadium" Scale="1" FbxFile="{name}.fbx">
              <Materials>
            {string.Join("\n", materials.Select(m => $"    <Material Name=\"{m}\" Link=\"{(m.StartsWith(MaterialLib.NativePrefix) ? standIn[physics[m]] : m)}\" PhysicsId=\"{physics[m]}\"/>"))}
              </Materials>
            </MeshParams>
            """);
        File.WriteAllText(Path.Combine(Paths.Work, name + ".Item.xml"), $"""
            <Item Type="StaticObject" Collection="Stadium" AuthorName="IceConverter">
              <MeshParamsLink File="Mesh/{name}.MeshParams.xml"/>{(waypoint is null ? "" : $"\n  <Waypoint Type=\"{waypoint}\"/>")}
              <GridSnap HStep="32" VStep="8"/>
              <Levitation VStep="8" GhostMode="false"/>
              <Options OneAxisRotation="true"/>
            </Item>
            """);

        if (RunImporter($"Items\\IceConverter\\{name}.Item.xml") is { } importError) return importError;

        var itemPath = Path.Combine(Paths.Items, name + ".Item.gbx");
        var collision = Jsonc.Load<List<FbxObject>>(Path.Combine(meshDir, name + ".objects.json"))
            .Where(o => !o.Name.StartsWith("_notcollidable_") && !o.Name.StartsWith("_trigger_"))
            .Select(o => (o.Name, o.Name.StartsWith("_notvisible_pad") ? $"RoadIce:{Pads.PadGameplay(o.Name)}" : physics[o.Material], o.Triangles)).ToList();
        surfaces = ItemPhysics.Fix(itemPath, collision);
        if (materials.Any(m => m.StartsWith(MaterialLib.NativePrefix))) ItemPatches.PointNative(itemPath, environment);
        if (waypoint is not null) ItemPatches.SetWaypoint(itemPath, waypoint);
        ItemPatches.AddVertexColours(itemPath);
        return null;
    }

    private static string? RunImporter(string itemXml)
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Paths.Game, "NadeoImporter.exe"), ["Item", itemXml])
            : new ProcessStartInfo("wine", ["NadeoImporter.exe", "Item", itemXml]);
        psi.WorkingDirectory = Paths.Game;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        if (!OperatingSystem.IsWindows())
        {
            psi.Environment["WINEPREFIX"] = Paths.ImporterWinePrefix;
            psi.Environment["WINEDLLOVERRIDES"] = "mscoree,mshtml=";
            psi.Environment["WINEDEBUG"] = "-all";
        }
        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        var output = proc.StandardOutput.ReadToEnd() + stderr.Result;
        proc.WaitForExit();
        return output.Contains("Created :") ? null : "NadeoImporter failed:\n" + output.Trim();
    }

    public static bool RunQuiet(string file, string[] args, out string log)
    {
        var psi = new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var proc = Process.Start(psi)!;
        var stderr = proc.StandardError.ReadToEndAsync();
        log = proc.StandardOutput.ReadToEnd() + stderr.Result;
        proc.WaitForExit();
        return proc.ExitCode == 0;
    }
}

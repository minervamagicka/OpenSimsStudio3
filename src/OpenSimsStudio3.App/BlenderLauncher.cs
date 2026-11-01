using System.Diagnostics;
using OpenSimsStudio3.Formats.Install;

namespace OpenSimsStudio3.App;

/// <summary>
/// Opens a .simgeom in Blender through GEOM Tools: starts the preferred Blender (settings, then the Blender Launcher
/// favourite, then the newest with GEOM Tools) and runs a small script, written next to the file, that enables the
/// add-on for the session and imports the file. Nothing in the player's add-on files or Blender preferences changes.
/// </summary>
public static class BlenderLauncher
{
    /// <summary>Starts Blender on the file; returns the install used, or null when none with GEOM Tools is found.</summary>
    public static BlenderInstall? Open(string simgeom, string? chosenBlender)
    {
        var blender = ToolInstalls.PreferredBlender(chosenBlender);
        if (blender == null) return null;
        var script = Path.Combine(Path.GetDirectoryName(simgeom)!, "oss3_open_in_blender.py");
        File.WriteAllText(script, $$"""
            # Written by OpenSimsStudio³: imports one mesh group for editing. Export it back with
            # File > Export > Sims 3 GEOM (.simgeom) over the same file, then press Re-import in OSS3.
            import bpy, addon_utils

            def patch_geom_tools_for_blender_4():
                # GEOM Tools 2.1.x predates Blender 4. For this session only:
                # - its add_prop stores the GEOM's RCOL chunks, shader data and TGIs through rna_idprop_ui_create, which
                #   Blender 4 rejects for list/dict values, so export later fails. Store them as plain custom properties.
                # - Blender 4.1 removed Mesh.calc_normals_split(); loop normals are always available, so a no-op is enough.
                import io_simgeom.io.geom_import as gi
                def add_prop(self, obj, key, value, minmax=[0, 2147483647], descript="prop"):
                    obj[key] = value
                    if isinstance(value, (int, float)) and not isinstance(value, bool):
                        try:
                            obj.id_properties_ui(key).update(min=minmax[0], max=minmax[1], soft_min=minmax[0], soft_max=minmax[1], description=descript)
                        except Exception:
                            pass
                for cls in vars(gi).values():
                    if isinstance(cls, type) and hasattr(cls, "add_prop"):
                        cls.add_prop = add_prop
                if not hasattr(bpy.types.Mesh, "calc_normals_split"):
                    bpy.types.Mesh.calc_normals_split = lambda self: None

            addon_utils.enable("io_simgeom", default_set=False, persistent=False)
            bpy.ops.wm.read_homefile(use_empty=True)
            addon_utils.enable("io_simgeom", default_set=False, persistent=False)
            if bpy.app.version >= (4, 0, 0):
                patch_geom_tools_for_blender_4()
            bpy.ops.simgeom.import_geom(filepath=r"{{simgeom}}")
            """);
        Process.Start(new ProcessStartInfo(blender.Executable) { ArgumentList = { "--python", script }, UseShellExecute = false });
        return blender;
    }
}

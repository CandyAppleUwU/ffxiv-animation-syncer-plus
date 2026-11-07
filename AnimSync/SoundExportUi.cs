using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;

namespace AnimSync;

public sealed partial class AnimSync {
    private int exportingSound;
    private volatile string soundExportStatus = string.Empty;
    private volatile string soundExportPath = string.Empty;
    private void DrawSoundExport(string source, string characterName = "Preview", bool compact = false) {
        ImGui.BeginDisabled(Volatile.Read(ref exportingSound) != 0);
        if(ImGui.Button("Export Sound")) {
            if(Interlocked.CompareExchange(ref exportingSound, 1, 0) == 0) {
                soundExportStatus = "Exporting WAV…"; soundExportPath = string.Empty;
                var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Animation Syncer Plus", "Exported Sounds");
                _ = Task.Run(() => {
                    try {
                        var path = SoundExport.Wav(source, directory, characterName);
                        soundExportPath = path;
                        soundExportStatus = "Saved: " + path;
                    } catch(Exception ex) { soundExportStatus = "Export failed: " + ex.Message; }
                    finally { Volatile.Write(ref exportingSound, 0); }
                });
            }
        }
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered()) ImGui.SetTooltip("Saves the full original sound as a standard 16-bit WAV in Documents/Animation Syncer Plus/Exported Sounds. Playback, volume and game effects do not change the exported file.");
        if(compact && ImGui.IsItemHovered()) {
            ImGui.SetTooltip(string.IsNullOrEmpty(soundExportStatus) ? "Export original sound as WAV." : soundExportStatus + "\nRight-click to open the export folder.");
            if(!string.IsNullOrEmpty(soundExportPath) && ImGui.IsMouseClicked(ImGuiMouseButton.Right))
                Dalamud.Utility.Util.OpenLink(Path.GetDirectoryName(soundExportPath)!);
        }
        if(!compact && !string.IsNullOrEmpty(soundExportStatus)) {
            ImGui.TextWrapped(soundExportStatus);
            if(!string.IsNullOrEmpty(soundExportPath) && ImGui.SmallButton("Open folder"))
                Dalamud.Utility.Util.OpenLink(Path.GetDirectoryName(soundExportPath)!);
        }
    }
}

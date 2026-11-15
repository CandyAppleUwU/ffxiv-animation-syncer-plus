using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AnimSync;

// The Objects tab of Bone Align: four presets - Wall, Table, Low Table/Chair, Pole - laid out like the Partner tab.
// Pick one, press Align with a looping animation playing, then nudge it with the offsets.
public sealed partial class AnimSync {
    private static readonly (string Name, string Tip)[] ObjectPresets = {
        ("Wall", "Puts the animation you're doing against the nearest wall."),
        ("Table", "Bends you over, or lays you on, the nearest table or counter."),
        ("Low Table/Chair", "The same for lower things: a low table, a chair, a couch, a bed."),
        ("Pole", "Puts your gripping hand on the nearest pole."),
    };
    private static readonly string[] WallSides = { "Auto", "Back to the wall", "Front to the wall" };

    private void RequestObject(int preset) {
        switch(preset) {
            case 0: RequestWall(config.ObjectWallSide == 1 ? false : config.ObjectWallSide == 2 ? true : null); break;
            case 1: RequestSnap(null); break;
            case 2: RequestSnap(null, SurfaceKind.Low); break;
            default: RequestSnap(null, SurfaceKind.Pole); break;
        }
    }

    private void DrawObjectsTab(float font) {
        var preset = Math.Clamp(config.ObjectPreset, 0, ObjectPresets.Length - 1);
        if(ImGui.BeginChild("##ObjectPresets", new Vector2(font * 9, font * 14), true)) {
            for(var i = 0; i < ObjectPresets.Length; i++) {
                if(ImGui.Selectable(ObjectPresets[i].Name, preset == i) && preset != i) {
                    config.ObjectPreset = preset = i;
                    Save();
                }
                if(ImGui.IsItemHovered()) ImGui.SetTooltip(ObjectPresets[i].Tip);
            }
        }
        ImGui.EndChild();
        ImGui.SameLine();
        ImGui.BeginGroup();
        ImGui.TextUnformatted(ObjectPresets[preset].Name);
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + font * 20);
        ImGui.TextWrapped(boneAlignStatus);
        ImGui.PopTextWrapPos();
        if(Client.IsGPosing && AlignMover() is { } mover) ImGui.TextDisabled($"Moving: {mover.Name.TextValue} (pick who in the Partner tab)");
        if(preset == 0) {
            var side = Math.Clamp(config.ObjectWallSide, 0, WallSides.Length - 1);
            ImGui.SetNextItemWidth(font * 10f);
            if(ImGui.BeginCombo("Side", WallSides[side])) {
                for(var i = 0; i < WallSides.Length; i++)
                    if(ImGui.Selectable(WallSides[i], side == i) && side != i) { config.ObjectWallSide = i; Save(); }
                ImGui.EndCombo();
            }
            if(ImGui.IsItemHovered()) ImGui.SetTooltip("Which side of you goes to the wall. Auto reads it from the pose.");
        }
        var aligned = alignIsSurface && heelsOffset != null;
        ImGui.BeginDisabled(BoneAlignBusy || snapPending || timelineWindow.IsOpen);
        if(ImGui.Button(aligned ? "Align Again" : "Align", new Vector2(font * 8, 0))) RequestObject(preset);
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(ObjectPresets[preset].Tip + "\nStart a looping animation first.");
        if(aligned) {
            ImGui.SameLine();
            if(ImGui.Button("Turn Off", new Vector2(font * 6, 0))) TurnOffBoneAlign();
        }
        if(aligned && !BoneAlignBusy) DrawFineTune(font, ImGui.GetTime());
        ImGui.EndGroup();
    }
}

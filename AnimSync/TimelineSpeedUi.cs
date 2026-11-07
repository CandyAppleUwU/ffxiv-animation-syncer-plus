using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AnimSync;

public sealed partial class AnimSync {
    // LivePose's animation speed button uses the Font Awesome glyph U+F3FD.
    private const FontAwesomeIcon SpeedIcon = (FontAwesomeIcon)0xF3FD;

    private void DrawTimelineSpeed(TimelineTrack track, bool linked) {
        ImGui.BeginDisabled(linked);
        var clicked = CompactIconButton("TimelineSpeed", SpeedIcon, string.Empty);
        ImGui.EndDisabled();
        if(ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip(linked ? "Unchain music to change animation speed." : "Animation Speed Control");
        if(clicked) ImGui.OpenPopup("TimelineSpeedPopup");
        if(!ImGui.BeginPopup("TimelineSpeedPopup")) return;

        if(track.timelineView is { } view && !linked) {
            if(ImGui.Button("Freeze")) RequestSpeed(track, view, 0);
            ImGui.SameLine();
            if(ImGui.Button("Reset")) RequestSpeed(track, view, 100);
            var percent = view.Playing ? track.SpeedPercent : 0;
            ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12);
            if(ImGui.SliderInt("##Speed", ref percent, -500, 500, "%d%%", ImGuiSliderFlags.AlwaysClamp))
                RequestSpeed(track, view, percent);
        }
        ImGui.EndPopup();
    }

    private static void RequestSpeed(TimelineTrack track, TimelineView view, int percent) {
        if(percent != 0) track.SpeedPercent = percent;
        track.timelineRequest = (view.Epoch, null, percent != 0);
    }
}

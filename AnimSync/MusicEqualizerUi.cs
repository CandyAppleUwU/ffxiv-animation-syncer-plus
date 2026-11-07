using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace AnimSync;

public sealed partial class AnimSync {
    private const string EqualizerLabel = "Equalizer";
    private static readonly (string Name, float[] Gains)[] EqualizerPresets = [
        ("Flat", [0, 0, 0, 0, 0, 0]),
        ("Bass Boost", [6, 4, 1, 0, 0, 0]),
        ("Bass Reducer", [-6, -4, -1, 0, 0, 0]),
        ("Vocal Boost", [-2, -2, 1, 3, 4, 0]),
        ("Treble Boost", [0, 0, 0, 0, 3, 5]),
        ("Electronic", [5, 3, -2, -1, 2, 4]),
    ];
    private int equalizerDragBand = -1;
    private float equalizerDragOffset;

    private static float EqualizerButtonWidth() => ImGui.CalcTextSize(EqualizerLabel).X + ImGui.GetStyle().FramePadding.X * 2;

    private void DrawMusicEqualizer() {
        if(ImGui.Button(EqualizerLabel)) ImGui.OpenPopup("MusicEqualizer");
        if(!ImGui.BeginPopup("MusicEqualizer")) return;

        var gains = config.MusicEqualizerGains;
        var current = Array.FindIndex(EqualizerPresets, preset => gains.AsSpan().SequenceEqual(preset.Gains));
        var changed = false;
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 11);
        if(ImGui.BeginCombo("Preset", current < 0 ? "Custom" : EqualizerPresets[current].Name)) {
            for(var i = 0; i < EqualizerPresets.Length; i++) {
                var preset = EqualizerPresets[i];
                if(ImGui.Selectable(preset.Name, current == i) && current != i) {
                    preset.Gains.CopyTo(gains, 0);
                    changed = true;
                }
                if(current == i) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }

        changed |= DrawEqualizerGraph(gains);
        var reverb = config.MusicReverbAmount * 100;
        ImGui.AlignTextToFramePadding();
        ImGui.TextUnformatted("Reverb");
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if(ImGui.SliderFloat("##MusicReverb", ref reverb, 0, 100, "%.0f%%", ImGuiSliderFlags.AlwaysClamp)) {
            config.MusicReverbAmount = MusicVolume.Clamp(reverb / 100);
            changed = true;
        }
        ImGui.TextDisabled("Drag a point to adjust its gain.");
        ImGui.EndPopup();

        if(changed) {
            foreach(var control in musicControls.Values) ApplyMusicEffects(control.Player);
            ScheduleAudioSettingsSave();
        }
    }

    private bool DrawEqualizerGraph(float[] gains) {
        var font = ImGui.GetFontSize();
        ImGui.InvisibleButton("##EqualizerGraph", new Vector2(font * 28, font * 11));
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var plotMin = min + new Vector2(font * 3.8f, font);
        var plotMax = max - new Vector2(font * 2.1f, font * 1.8f);
        var zeroY = (plotMin.Y + plotMax.Y) / 2;
        var pixelsPerDb = (plotMax.Y - zeroY) / MusicEqualizer.MaxBandGain;
        var radius = MathF.Max(3, font * .36f);
        var mouse = ImGui.GetIO().MousePos;
        var hovered = ImGui.IsItemHovered();
        var frequencies = MusicEqualizer.Frequencies;
        var logRange = MathF.Log((float)frequencies[^1] / frequencies[0]);
        Span<Vector2> points = stackalloc Vector2[frequencies.Length];
        var hoveredBand = -1;
        var hoverDistance = radius * radius * 4;
        for(var band = 0; band < points.Length; band++) {
            var fraction = MathF.Log((float)frequencies[band] / frequencies[0]) / logRange;
            points[band] = new Vector2(plotMin.X + fraction * (plotMax.X - plotMin.X), zeroY - gains[band] * pixelsPerDb);
            var distance = Vector2.DistanceSquared(points[band], mouse);
            if(hovered && distance < hoverDistance) {
                hoveredBand = band;
                hoverDistance = distance;
            }
        }
        if(ImGui.IsItemActivated()) {
            equalizerDragBand = hoveredBand;
            if(hoveredBand >= 0) equalizerDragOffset = mouse.Y - points[hoveredBand].Y;
        }
        var activeBand = ImGui.IsItemActive() ? equalizerDragBand : -1;
        var changed = false;
        if(activeBand >= 0 && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 0)) {
            var gain = Math.Clamp((zeroY - mouse.Y + equalizerDragOffset) / pixelsPerDb, -MusicEqualizer.MaxBandGain, MusicEqualizer.MaxBandGain);
            gain = MathF.Round(gain * 2) / 2;
            changed = gains[activeBand] != gain;
            gains[activeBand] = gain;
            points[activeBand].Y = zeroY - gain * pixelsPerDb;
        }

        var draw = ImGui.GetWindowDrawList();
        var grid = ImGui.GetColorU32(ImGuiCol.Text, .16f);
        var labels = ImGui.GetColorU32(ImGuiCol.TextDisabled);
        var curve = ImGui.GetColorU32(ImGuiCol.SliderGrabActive);
        draw.AddRectFilled(min, max, ImGui.GetColorU32(ImGuiCol.FrameBg), ImGui.GetStyle().FrameRounding);
        for(var step = -2; step <= 2; step++) {
            var db = step * MusicEqualizer.MaxBandGain / 2;
            var y = zeroY - db * pixelsPerDb;
            draw.AddLine(new Vector2(plotMin.X, y), new Vector2(plotMax.X, y), db == 0 ? labels : grid);
            var label = $"{db:+0;-0;0} dB";
            draw.AddText(new Vector2(plotMin.X - ImGui.CalcTextSize(label).X - font * .35f, y - font / 2), labels, label);
        }
        for(var band = 0; band < points.Length; band++) {
            var x = points[band].X;
            var frequency = frequencies[band];
            var label = frequency < 1000 ? $"{frequency} Hz" : $"{frequency / 1000f:0.#} kHz";
            draw.AddLine(new Vector2(x, plotMin.Y), new Vector2(x, plotMax.Y), grid);
            draw.AddText(new Vector2(x - ImGui.CalcTextSize(label).X / 2, plotMax.Y + font * .45f), labels, label);
        }
        for(var band = 1; band < points.Length; band++) draw.AddLine(points[band - 1], points[band], curve, 2);
        var highlightedBand = activeBand >= 0 ? activeBand : hoveredBand;
        for(var band = 0; band < points.Length; band++)
            draw.AddCircleFilled(points[band], radius, band == highlightedBand ? ImGui.GetColorU32(ImGuiCol.Text) : curve);
        if(highlightedBand >= 0) ImGui.SetTooltip($"{frequencies[highlightedBand]} Hz  {gains[highlightedBand]:+0.0;-0.0;0.0} dB");
        return changed;
    }
}

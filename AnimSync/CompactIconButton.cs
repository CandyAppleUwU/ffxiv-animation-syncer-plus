using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;

namespace AnimSync;

public sealed partial class AnimSync {
    private static bool CompactIconButton(string id, FontAwesomeIcon icon, string tooltip) {
        var height = ImGui.GetFrameHeight();
        var clicked = ImGui.Button("##" + id, new Vector2(height));
        // Draw the small transport symbols in screen pixels. Font scaling can shrink
        // AddText glyphs again in Dalamud, even when their measured size looked correct.
        var center = (ImGui.GetItemRectMin() + ImGui.GetItemRectMax()) / 2;
        var half = height * .36f;
        var stroke = MathF.Max(1, height * .1f);
        var color = ImGui.GetColorU32(ImGuiCol.Text);
        var draw = ImGui.GetWindowDrawList();
        Vector2 Point(float x, float y) => center + new Vector2(x, y) * half;
        void Arrow(float x, float width, float direction) => draw.AddTriangleFilled(
            Point(x - width * direction, -.82f), Point(x + width * direction, 0), Point(x - width * direction, .82f), color);
        void Bar(float x) => draw.AddLine(Point(x, -.82f), Point(x, .82f), color, stroke);
        switch(icon) {
            case FontAwesomeIcon.Play: Arrow(.05f, .72f, 1); break;
            case FontAwesomeIcon.Pause:
                draw.AddRectFilled(Point(-.62f, -.82f), Point(-.19f, .82f), color);
                draw.AddRectFilled(Point(.19f, -.82f), Point(.62f, .82f), color);
                break;
            case FontAwesomeIcon.StepForward: Arrow(-.17f, .56f, 1); Bar(.8f); break;
            case FontAwesomeIcon.StepBackward: Arrow(.17f, .56f, -1); Bar(-.8f); break;
            case FontAwesomeIcon.FastForward: Arrow(-.48f, .46f, 1); Arrow(.48f, .46f, 1); break;
            case FontAwesomeIcon.FastBackward: Arrow(-.48f, .46f, -1); Arrow(.48f, .46f, -1); break;
            case FontAwesomeIcon.Plus:
                draw.AddLine(Point(-.8f, 0), Point(.8f, 0), color, stroke);
                draw.AddLine(Point(0, -.8f), Point(0, .8f), color, stroke);
                break;
            case FontAwesomeIcon.Link:
                draw.AddCircle(Point(-.42f, .42f), half * .43f, color, 12, stroke);
                draw.AddCircle(Point(.42f, -.42f), half * .43f, color, 12, stroke);
                draw.AddLine(Point(-.26f, .26f), Point(.26f, -.26f), color, stroke);
                break;
            case SpeedIcon:
                // Fit the speedometer's arc, dial marks and needle inside the same square as the transport icons.
                var start = -MathF.PI * 1.15f;
                var sweep = MathF.PI * 1.3f;
                Vector2 DialPoint(float angle, float radius) => Point(MathF.Cos(angle) * radius, .22f + MathF.Sin(angle) * radius);
                for(var segment = 0; segment < 12; segment++) {
                    var angle = start + sweep * segment / 12;
                    draw.AddLine(DialPoint(angle, .86f), DialPoint(angle + sweep / 12, .86f), color, stroke);
                }
                for(var mark = 1; mark < 4; mark++) {
                    var angle = start + sweep * mark / 4;
                    draw.AddLine(DialPoint(angle, .52f), DialPoint(angle, .75f), color, MathF.Max(1, stroke * .55f));
                }
                draw.AddLine(Point(0, .22f), Point(.52f, -.25f), color, stroke);
                draw.AddCircleFilled(Point(0, .22f), stroke * .85f, color);
                break;
        }
        if(!string.IsNullOrEmpty(tooltip) && ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
        return clicked;
    }
}

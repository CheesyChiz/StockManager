using Pictomancy;
namespace ExplorersIcebox.Ui.DebugWindowTabs;

internal class PictoTestDebug
{
    private static Vector4 ImGuiCircleCol = FromUintABGR(C.PictoCircleColor); // ABGR Red
    private static Vector4 ImGuiDotColor = FromUintABGR(C.PictoWPColor);      // ABGR Red
    private static Vector4 ImGuiLineColor = FromUintABGR(C.PictoLineColor);   // ABGR Red
    private static Vector4 ImGuiTextColor = FromUintABGR(C.PictoTextCol);     // ABGR Red
    private static bool ShowDot;
    private static float DotRadius = C.DotRadius;
    private static bool ShowLine;
    private static float LineWidth = C.LineWidth;
    private static bool ShowCircle;
    private static bool ShowCircleOutline;
    private static bool ShowDonut;
    private static Vector2 DonutRadius = C.DonutRadius;
    private static Vector2 FanPosition = C.FanPosition;
    private static bool ShowVFX;
    private static bool ShowName;
    private static float FloatDistance = C.TextFloatPlus;
    private static float FloatTextScale;
    private static uint ToUintABGR(Vector4 col)
    {
        var a = (byte)(col.W * 255);
        var b = (byte)(col.Z * 255);
        var g = (byte)(col.Y * 255);
        var r = (byte)(col.X * 255);
        return (uint)((a << 24) | (b << 16) | (g << 8) | r);
    }

    private static Vector4 FromUintABGR(uint color)
    {
        var a = ((color >> 24) & 0xFF) / 255f;
        var b = ((color >> 16) & 0xFF) / 255f;
        var g = ((color >> 8) & 0xFF) / 255f;
        var r = (color & 0xFF) / 255f;
        return new(r, g, b, a);
    }

    public static void Draw()
    {
        ImGui.Text("Select a color:");

        if (ImGui.ColorEdit4("Circle Color", ref ImGuiCircleCol))
        {
            C.PictoCircleColor = ToUintABGR(ImGuiCircleCol);
            C.Save();
        }
        if (ImGui.ColorEdit4("Dot Color", ref ImGuiDotColor))
        {
            C.PictoWPColor = ToUintABGR(ImGuiDotColor);
            C.Save();
        }
        if (ImGui.ColorEdit4("Line Color", ref ImGuiLineColor))
        {
            C.PictoLineColor = ToUintABGR(ImGuiLineColor);
            C.Save();
        }
        if (ImGui.ColorEdit4("Text Color", ref ImGuiTextColor))
        {
            C.PictoTextCol = ToUintABGR(ImGuiTextColor);
            C.Save();
        }

        var target = Svc.Targets.Target;
        var PlayerPos = Svc.Objects.LocalPlayer?.Position ?? new Vector3(0);

        if (target != null)
        {
            using (var drawList = PctService.Draw())
            {
                if (drawList == null)
                    return;
                // Draw a circle around a GameObject's hitbox
                var worldPosition = target.Position;
                var radius = target.HitboxRadius;

                ImGui.Checkbox("Show Dot", ref ShowDot);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);

                if (ImGui.DragFloat("Dot Size", ref DotRadius, 0.2f))
                {
                    C.DotRadius = DotRadius;
                    C.Save();
                }
                ImGui.Checkbox("Show Line", ref ShowLine);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                if (ImGui.DragFloat("Line Width", ref LineWidth, 0.1f))
                {
                    C.LineWidth = LineWidth;
                    C.Save();
                }
                ImGui.Checkbox("Show Circle", ref ShowCircle);
                ImGui.Checkbox("Show Circle Outline", ref ShowCircleOutline);
                ImGui.Checkbox("Show Fan/Donut", ref ShowDonut);
                ImGui.SetNextItemWidth(100);
                if (ImGui.DragFloat2("Inner | Outer Radius", ref DonutRadius, 0.1f))
                {
                    C.DonutRadius = DonutRadius;
                    C.Save();
                }
                ImGui.SetNextItemWidth(100);
                if (ImGui.DragFloat2("Start/End Position", ref FanPosition, 0.1f))
                {
                    C.FanPosition = FanPosition;
                    C.Save();
                }
                ImGui.Checkbox("Show VFX", ref ShowVFX);
                ImGui.Checkbox("Show Name", ref ShowName);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                if (ImGui.DragFloat("Float Name", ref FloatDistance))
                {
                    C.TextFloatPlus = FloatDistance;
                    C.Save();
                }
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                ImGui.DragFloat("Scale", ref FloatTextScale);


                if (ShowDot)
                    drawList.AddDot(worldPosition, DotRadius, C.PictoWPColor);
                if (ShowLine && (PlayerPos != new Vector3(0)))
                    drawList.AddLine(PlayerPos, worldPosition, LineWidth, C.PictoLineColor);
                if (ShowCircle)
                    drawList.AddCircle(worldPosition, radius, C.PictoCircleColor);
                if (ShowCircleOutline)
                    drawList.AddCircleFilled(worldPosition, radius, C.PictoCircleColor);
                if (ShowDonut)
                    drawList.AddFanFilled(worldPosition, DonutRadius.X, DonutRadius.Y, FanPosition.X, FanPosition.Y, C.PictoCircleColor);
                if (ShowVFX)
                    PctService.VfxRenderer.AddFan("TestId", worldPosition, DonutRadius.X, DonutRadius.Y, FanPosition.X, FanPosition.Y, ImGuiCircleCol);
                if (ShowName)
                {
                    Vector3 textWorldPosition = new(worldPosition.X, worldPosition.Y + FloatDistance, worldPosition.Z);
                    drawList.AddText(textWorldPosition, C.PictoTextCol, $"{target.Name}", FloatTextScale);
                }
            }
        }
    }
}

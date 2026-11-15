using System.Numerics;
using FirstPersonCamera.Port.Camera;
using FirstPersonCamera.Port.Runtime;
using ImGuiNET;

namespace FirstPersonCamera.Port;

/// <summary>
/// On-screen controls and readout.
///
/// The original mod was keyboard/controller driven (F5, L3/R3) and had no UI beyond log lines.
/// On a phone there is neither key nor stick, so the same toggle is exposed as two touch buttons
/// (top-left and top-right, mirroring L3/R3) plus a compact status line. Everything else — the
/// camera itself, the hiding logic, the smoothing — is untouched by this file.
/// </summary>
internal static class Hud
{
    private const uint Accent = 0xFF66C0F4;   // ABGR
    private const uint Dim = 0xFFA0A0A0;
    private const uint Warn = 0xFF40A0FF;

    /// <summary>Buttons, status text and the view indicator.</summary>
    internal static void Draw(FirstPersonCameraPlugin plugin, FirstPersonCameraController controller,
        ImDrawListPtr drawList, int width, int height)
    {
        // ── Touch toggle buttons (both corners, so either thumb reaches one) ──
        ImGui.SetNextWindowBgAlpha(0.35f);
        ImGui.SetNextWindowPos(new Vector2(24f, 140f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(150f, 56f), ImGuiCond.Always);
        var flags = ImGuiWindowFlags.NoDecoration | ImGuiWindowFlags.NoSavedSettings |
                    ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoFocusOnAppearing;
        ImGui.Begin("##fpc_btn_l", flags);
        if (TouchLookButton(controller)) plugin.ToggleFromHud();
        ImGui.End();

        ImGui.SetNextWindowBgAlpha(0.35f);
        ImGui.SetNextWindowPos(new Vector2(width - 174f, 140f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new Vector2(150f, 56f), ImGuiCond.Always);
        ImGui.Begin("##fpc_btn_r", flags);
        if (TouchLookButton(controller)) plugin.ToggleFromHud();
        ImGui.End();

        // ── Status line ──
        var status = controller.Status;
        var colour = controller.IsActive ? (controller.InCutscene ? Warn : Accent) : Dim;
        drawList.AddText(new Vector2(28f, 96f), colour, $"FIRST-PERSON: {status.ToUpperInvariant()}");
    }

    private static bool TouchLookButton(FirstPersonCameraController controller)
    {
        var label = controller.IsActive ? "FP: ON" : "FP: OFF";
        return ImGui.Button(label, new Vector2(136f, 40f));
    }

    /// <summary>Crosshair — only while the first-person view owns the camera.</summary>
    internal static void DrawCrosshair(ImDrawListPtr drawList)
    {
        var io = ImGui.GetIO();
        var centre = new Vector2(io.DisplaySize.X * 0.5f, io.DisplaySize.Y * 0.5f);

        const float gap = 6f;
        const float len = 10f;
        uint colour = 0xC0FFFFFF;

        drawList.AddLine(new Vector2(centre.X - gap - len, centre.Y),
            new Vector2(centre.X - gap, centre.Y), colour, 2f);
        drawList.AddLine(new Vector2(centre.X + gap, centre.Y),
            new Vector2(centre.X + gap + len, centre.Y), colour, 2f);
        drawList.AddLine(new Vector2(centre.X, centre.Y - gap - len),
            new Vector2(centre.X, centre.Y - gap), colour, 2f);
        drawList.AddLine(new Vector2(centre.X, centre.Y + gap),
            new Vector2(centre.X, centre.Y + gap + len), colour, 2f);
    }
}

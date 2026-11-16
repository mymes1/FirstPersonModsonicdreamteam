using System;
using System.Linq;
using System.Text;
using FirstPersonCamera.Port.Runtime;
using StArray.ModManager.Manager;
using StArray.ModManager.RuntimeAbstractions;

namespace FirstPersonCamera.Port.Diagnostics;

/// <summary>
/// Startup self-check: reports which capabilities this build of the game actually exposes.
///
/// This is the piece that turns "it compiled" into "here is what will work on this device". It prints
/// the <i>real</i> image list instead of the desktop interop names, then each binding the camera needs,
/// so a stripping-related miss is a one-line log entry rather than a mystery.
///
/// Never throws — a missing binding is a degradation with a documented fallback, not a failure.
/// </summary>
internal static class SelfTest
{
    internal static void Run()
    {
        try
        {
            Logger.Info("SelfTest", $"backend={RuntimeManager.Backend} " +
                                    $"domain={(RuntimeManager.GetDomain()?.IsValid == true ? "ok" : "MISSING")}");

            ReportImages();
            ReportBindings();
        }
        catch (Exception ex)
        {
            Logger.Warn("SelfTest", $"self-check aborted: {ex.Message}");
        }
    }

    private static void ReportImages()
    {
        var images = ClassIndex.LoadedImages();
        if (images.Count == 0)
        {
            Logger.Warn("SelfTest", "no images enumerated — runtime not attached to this thread yet.");
            return;
        }

        // Engine images first, then gameplay, so the important lines are easy to find in logcat.
        var ordered = images
            .OrderBy(i => i.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(i => i, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Logger.Info("SelfTest", $"loaded images ({ordered.Count}): {string.Join(", ", ordered)}");

        foreach (var interesting in new[] { "Assembly-CSharp.dll", "Cinemachine.dll", "Unity.Cinemachine.dll" })
        {
            Logger.Info("SelfTest", $"image {interesting}: {(ClassIndex.HasImage(interesting) ? "present" : "absent")}");
        }
    }

    private static void ReportBindings()
    {
        var unity = GameApi.Initialise();
        Logger.Info("SelfTest", unity
            ? "unity bindings: Camera.main / Time / FindObjectsOfType resolved."
            : "unity bindings: INCOMPLETE — first-person camera stays idle until they resolve.");

        Logger.Info("SelfTest", "game types: " + string.Join(" ", new[]
        {
            Describe("Actor", GameApi.ActorClass),
            Describe("Character", GameApi.CharacterClass),
            Describe("CinemachineCameraManager", GameApi.CameraManagerClass),
            Describe("CinemachineBrain", GameApi.CinemachineBrainClass),
            Describe("ActorAttachPoints", GameApi.AttachPointsTryGetNodeTransform != 0 ? 1 : 0),
        }));

        Logger.Info("SelfTest", "input: " +
                                $"legacyKeyPolling={(GameApi.InputGetKeyDown != 0 ? "ok" : "missing")} " +
                                $"axes={(GameApi.InputGetAxisRaw != 0 ? "ok" : "missing")} " +
                                "touchDrag=always");

        // Prove the runtime is actually reachable from this thread with a string round-trip.
        var probe = RuntimeString.New("fp-probe");
        Logger.Info("SelfTest", probe.IsValid && probe.ToString() == "fp-probe"
            ? "runtime string marshalling: ok"
            : "runtime string marshalling: FAILED — IL2CPP runtime not reachable from this thread.");

        // If the Cinemachine types are missing, say what to look at instead of failing silently.
        if (GameApi.CinemachineBrainClass == 0 || GameApi.CameraManagerClass == 0)
        {
            var sb = new StringBuilder();
            foreach (var image in ClassIndex.LoadedImages().Take(40)) sb.Append(image).Append(", ");
            Logger.Warn("SelfTest",
                "Cinemachine types not found. Either the game uses a different namespace or the " +
                "build stripped them. Search these images for the type: " + sb);
        }
    }

    private static string Describe(string label, nint klass)
        => $"{label}={(klass != 0 ? ClassIndex.ImageOf(klass) ?? "ok" : "missing")}";
}

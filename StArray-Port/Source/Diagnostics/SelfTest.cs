using System;
using System.Text;
using FirstPersonCamera.Port.Runtime;
using StArray.ModManager.Manager;
using StArray.ModManager.RuntimeAbstractions;

namespace FirstPersonCamera.Port.Diagnostics;

/// <summary>
/// Startup self-check. Reports, for the current runtime, exactly which capabilities the port could
/// actually bind — this is what turns "it compiled" into "here is what will work on the device".
///
/// It deliberately never throws: a missing binding is a degradation, not a failure, and the mod has
/// a documented fallback path for each one.
/// </summary>
internal static class SelfTest
{
    internal static void Run()
    {
        try
        {
            var sb = new StringBuilder();

            sb.Append("backend=").Append(RuntimeManager.Backend);
            sb.Append(" domain=").Append(RuntimeManager.GetDomain()?.IsValid == true ? "ok" : "MISSING");

            var domain = RuntimeManager.GetDomain();
            foreach (var image in new[]
                     {
                         GameApi.CoreModule,
                         GameApi.InputModule,
                         GameApi.CinemachineModule,
                         GameApi.GameAssembly,
                     })
            {
                sb.Append(' ').Append(image).Append('=')
                  .Append(domain?.OpenAssembly(image) != null ? "ok" : "missing");
            }

            Logger.Info("SelfTest", sb.ToString());

            // Core bindings the camera cannot work without.
            var camera = GameApi.Initialise();
            Logger.Info("SelfTest", camera
                ? "unity bindings: Camera.main / Time / FindObjectsOfType resolved."
                : "unity bindings: INCOMPLETE — first-person camera stays idle until they resolve.");

            Logger.Info("SelfTest",
                $"game types: Actor={(GameApi.ActorClass != 0 ? "ok" : "missing")} " +
                $"Character={(GameApi.CharacterClass != 0 ? "ok" : "missing")} " +
                $"CameraManager={(GameApi.CameraManagerClass != 0 ? "ok" : "missing")} " +
                $"CinemachineBrain={(GameApi.CinemachineBrainClass != 0 ? "ok" : "missing")}");

            Logger.Info("SelfTest",
                $"input: legacyKeyPolling={(GameApi.InputGetKeyDown != 0 ? "ok" : "missing")} " +
                $"axes={(GameApi.InputGetAxisRaw != 0 ? "ok" : "missing")} " +
                "touchDrag=always");

            // Prove the internal-API bridge works by round-tripping a string through the runtime.
            var probe = RuntimeString.New("fp-probe");
            Logger.Info("SelfTest", probe.IsValid
                ? "runtime string marshalling: ok"
                : "runtime string marshalling: FAILED — IL2CPP runtime not reachable from this thread.");
        }
        catch (Exception ex)
        {
            Logger.Warn("SelfTest", $"self-check aborted: {ex.Message}");
        }
    }
}

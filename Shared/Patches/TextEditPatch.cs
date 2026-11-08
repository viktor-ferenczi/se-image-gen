using System.Diagnostics.CodeAnalysis;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.Entities.Cube;
using Shared.ImageGen;

namespace Shared.Patches;

// A player's text edit of a screen leaves the client in one of these, PanelComponent is the screen
// being edited. The client generates only the prompts its own player writes.
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
public static class TextEditPatch
{
    [HarmonyPatch(typeof(MyTextPanel), "SendChangeDescriptionMessage")]
    public static class TextPanel
    {
        public static void Prefix(MyTextPanel __instance, StringBuilder description, bool isPublic)
        {
            if (isPublic)
                ImageGenController.OnLocalEdit(__instance.PanelComponent, description);
        }
    }

    [HarmonyPatch(typeof(MyFunctionalBlock), "SendChangeDescriptionMessage")]
    public static class FunctionalBlock
    {
        public static void Prefix(MyFunctionalBlock __instance, StringBuilder description) =>
            ImageGenController.OnLocalEdit(__instance.PanelComponent, description);
    }

    [HarmonyPatch(typeof(MyCockpit), "SendChangeDescriptionMessage")]
    public static class Cockpit
    {
        public static void Prefix(MyCockpit __instance, StringBuilder description) =>
            ImageGenController.OnLocalEdit(__instance.PanelComponent, description);
    }
}

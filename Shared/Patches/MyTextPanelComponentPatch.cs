using System.Diagnostics.CodeAnalysis;
using System.Text;
using HarmonyLib;
using Sandbox.Game.Entities.Blocks;
using Shared.ImageGen;

namespace Shared.Patches;

// Every change of the text shown on an LCD surface ends in one of these: terminal edits,
// network sync, programmable blocks and loading the block from the world.
[HarmonyPatch(typeof(MyTextPanelComponent))]
[SuppressMessage("ReSharper", "UnusedType.Global")]
[SuppressMessage("ReSharper", "UnusedMember.Global")]
public static class MyTextPanelComponentPatch
{
    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyTextPanelComponent.UpdateText), typeof(string), typeof(bool))]
    public static void UpdateTextStringPostfix(MyTextPanelComponent __instance) =>
        ImageGenController.OnTextUpdated(__instance);

    [HarmonyPostfix]
    [HarmonyPatch(nameof(MyTextPanelComponent.UpdateText), typeof(StringBuilder), typeof(bool))]
    public static void UpdateTextBuilderPostfix(MyTextPanelComponent __instance) =>
        ImageGenController.OnTextUpdated(__instance);
}

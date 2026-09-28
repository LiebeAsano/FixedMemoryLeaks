using Menu.Remix.MixedUI;

namespace FixMemoryLeaks;

public static class ComboBoxUnassign
{
    public static void Hook()
    {
        On.Menu.Remix.MixedUI.UIconfig.Unload += UIconfig_Unload;
    }

    private static void UIconfig_Unload(On.Menu.Remix.MixedUI.UIconfig.orig_Unload orig, UIconfig self)
    {
        orig(self);

        if (self is OpComboBox comboBox)
            comboBox.Unassign();
    }
}

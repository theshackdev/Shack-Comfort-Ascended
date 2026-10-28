using System;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Jotunn.Utils;

namespace TheShack.ComfortAscended
{
    // Comfort objects link up when they're within the link range of each other (10 m, the game's own comfort radius),
    // and a linked chain counts as one: under a roof and within 10 m of any object in the chain, you get the comfort of
    // the whole chain, wherever you stand in the house.
    //
    //   - The game's own rules still make the number: 1, +1 under a roof, then the best object of each comfort group
    //     (fire, bed, chair, table, banner, carpet...). A chain can't stack two chairs, so it never beats standing next
    //     to everything at once.
    //   - Resting is unchanged: a fire nearby, a roof or a seat, no enemies. Only the comfort number (which sets how
    //     long Rested lasts) comes from the chain.
    //   - By default only objects under a roof link, so banners and fires out in the yard don't bridge two houses.
    //     They still count the vanilla way when they're within 10 m of you.
    //   - Looking at a comfort object (with the hammer out for rugs and banners) or placing one shows moving dots along
    //     the links, like the workbench and its extensions.
    //   - The hammer's build menu shows which pieces give comfort and how much (BuildMenu.cs).
    //
    // Comfort is worked out on each player's own PC, as in vanilla. The settings are admin-only and come from the server
    // when the server has this mod (Jotunn config sync).
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(Jotunn.Main.ModGuid)]
    [NetworkCompatibility(CompatibilityLevel.VersionCheckOnly, VersionStrictness.Minor)]
    [SynchronizationMode(AdminOnlyStrictness.IfOnServer)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "theshack.comfortascended";
        public const string PluginName = "Shack Comfort Ascended";
        public const string PluginVersion = "1.0.1";

        internal static ManualLogSource Log;
        private static bool ready;

        private void Awake()
        {
            Log = Logger;
            Settings.Bind(Config);

            // A mod that throws in Awake takes the chainloader down with it. If the game changed under us, say so and
            // change nothing.
            Harmony harmony = new Harmony(PluginGuid);
            try
            {
                Access.Init();
                harmony.PatchAll(Assembly.GetExecutingAssembly());
            }
            catch (Exception e)
            {
                harmony.UnpatchSelf();
                Log.LogError($"{PluginName} could not hook into this game version; comfort stays vanilla. {e}");
                return;
            }

            ready = true;
            Log.LogInfo($"{PluginName} {PluginVersion} ready (link range {Settings.LinkRange.Value} m, " +
                $"only roofed objects link: {Settings.RoofedOnly.Value}).");
        }

        private void Update()
        {
            if (ready)
            {
                Dots.Update();
            }
        }
    }

    internal static class Settings
    {
        public static ConfigEntry<bool> ShowDots;
        public static ConfigEntry<bool> ShowInBuildMenu;

        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> LinkRange;
        public static ConfigEntry<bool> RoofedOnly;

        public static void Bind(ConfigFile config)
        {
            int order = 100;
            ConfigEntry<T> Bind<T>(string section, string key, T value, string description, AcceptableValueBase range = null) =>
                config.Bind(section, key, value, new ConfigDescription(description, range,
                    new ConfigurationManagerAttributes { IsAdminOnly = true, Order = order-- }));
            ConfigEntry<T> Personal<T>(string section, string key, T value, string description, AcceptableValueBase range = null) =>
                config.Bind(section, key, value, new ConfigDescription(description, range,
                    new ConfigurationManagerAttributes { Order = order-- }));

            // Each player's own choice; not synced from the server.
            ShowDots = Personal("0 - This PC", "Show chain dots", true,
                "Moving dots along the links of a comfort chain while you look at one of its objects (with the hammer out " +
                "for rugs and banners) or place a comfort object, like the workbench and its extensions.");
            ShowInBuildMenu = Personal("0 - This PC", "Show comfort in build menu", true,
                "The hammer's build menu marks pieces that give comfort with \"+N\" on their icon, and the piece info says how much " +
                "comfort it gives and which kind it counts as (only the best of each kind counts).");

            Enabled = Bind("1 - General", "Enabled", true,
                "Comfort objects link into chains, and a chain counts as one. Off: vanilla comfort (only objects within 10 m).");
            LinkRange = Bind("1 - General", "Link range", 10f,
                "Two comfort objects link when they're closer than this, in meters. 10 = the game's own comfort radius.",
                new AcceptableValueRange<float>(1f, 30f));
            RoofedOnly = Bind("1 - General", "Only roofed objects link", true,
                "Only comfort objects under a roof join a chain, so banners and fires out in the yard don't bridge two " +
                "houses. Unroofed objects still count the vanilla way when they're within 10 m of you.");
        }
    }
}

using BepInEx.Configuration;

namespace EventRotator.ScourgeAi
{
    internal static class ScourgeAiConfig
    {
        public static bool Enabled { get; private set; }
        public static ConfigEntry<bool> Debug { get; private set; }
        public static ConfigEntry<float> ContactMemory { get; private set; }
        public static ConfigEntry<float> OpeningFire { get; private set; }
        public static ConfigEntry<float> AggressionMultiplier { get; private set; }
        public static ConfigEntry<float> ProtectionLeashRadius { get; private set; }
        public static ConfigEntry<float> ProtectionCloseGuardRadius { get; private set; }

        public const float DefaultAggressionMultiplier = 1.5f;
        // Configuration Manager recognizes this tag without a plugin reference.
        private const string AdvancedSettingTag = "Advanced";

        public static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("Scourge AI", "Enabled", true,
                "Enable Scourge squad tactics. Restart the game after changing this setting.").Value;
            ContactMemory = config.Bind("Scourge AI", "Contact memory seconds", 12f,
                new ConfigDescription("How long soldiers search a last observed enemy position.",
                    new AcceptableValueRange<float>(3f, 30f), AdvancedSettingTag));
            OpeningFire = config.Bind("Scourge AI", "Opening fire seconds", 2f,
                new ConfigDescription("Time to establish covering fire before a squad member maneuvers.",
                    new AcceptableValueRange<float>(0.5f, 6f), AdvancedSettingTag));
            AggressionMultiplier = config.Bind("Scourge AI", "Combat aggression multiplier", DefaultAggressionMultiplier,
                new ConfigDescription("Scales Scourge combat ranges, opening fire delay, and contact memory. 1 is the prior baseline.",
                    new AcceptableValueRange<float>(1f, 2f)));
            ProtectionLeashRadius = config.Bind("Scourge AI", "Protection leash radius",
                ScourgeProtectionSettings.DefaultLeashRadius,
                new ConfigDescription("Maximum navigation distance from the living Scourge boss.",
                    new AcceptableValueRange<float>(18f, 60f)));
            ProtectionCloseGuardRadius = config.Bind("Scourge AI", "Close guard radius",
                ScourgeProtectionSettings.DefaultCloseGuardRadius,
                new ConfigDescription("Distance within which one follower maintains close protection.",
                    new AcceptableValueRange<float>(4f, 18f)));
            Debug = config.Bind("Scourge AI", "Log decisions", false,
                new ConfigDescription("Log tactic changes and navigation failures for Scourge soldiers.",
                    null, AdvancedSettingTag));
        }

        public static ScourgeTacticalSettings TacticalSettings => new ScourgeTacticalSettings(
            ContactMemory?.Value ?? ScourgeTacticalSettings.DefaultContactMemorySeconds,
            OpeningFire?.Value ?? ScourgeTacticalSettings.DefaultInitialSuppressSeconds,
            AggressionMultiplier?.Value ?? DefaultAggressionMultiplier);

        public static ScourgeProtectionSettings ProtectionSettings => new ScourgeProtectionSettings(
            ProtectionLeashRadius?.Value ?? ScourgeProtectionSettings.DefaultLeashRadius,
            ProtectionCloseGuardRadius?.Value ?? ScourgeProtectionSettings.DefaultCloseGuardRadius);
    }
}



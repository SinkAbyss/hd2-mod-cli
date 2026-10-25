namespace Hd2Mod
{

    /// <summary>Nexus Mods: the game's name there and a mod's page.</summary>
    public static class Nexus
    {
        public const string Game = "helldivers2";

        /// <summary>A mod's page on Nexus Mods.</summary>
        public static string Page(NexusRef r) => "https://www.nexusmods.com/" + r.Game + "/mods/" + r.Mod;
    }
}

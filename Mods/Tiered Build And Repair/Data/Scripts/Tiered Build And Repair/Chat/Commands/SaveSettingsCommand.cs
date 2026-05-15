using STGTieredBuildAndRepair.Models;

namespace STGTieredBuildAndRepair.Chat.Commands
{
    public static class SaveSettingsCommand
    {
        public static ChatCommandResult Execute()
        {
            SyncModSettings.Save(Mod.Settings, true);
            return ChatCommandResult.Success("Settings saved to world folder. Filename: ModSettings.xml");
        }
    }
}

namespace Loupedeck.HardwareMonitorPlugin
{
    public class HardwareMonitorPlugin : Plugin
    {
        public override Boolean UsesApplicationApiOnly => true;

        public override Boolean HasNoApplication => true;

        public HardwareMonitorPlugin()
        {
            PluginLog.Init(this.Log);
            PluginResources.Init(this.Assembly);
        }

        public override void Load()
        {
            PluginLog.Info("HardwareMonitor plugin loaded");
        }

        public override void Unload()
        {
            PluginLog.Info("HardwareMonitor plugin unloaded");
        }
    }
}

namespace Loupedeck.HardwareMonitorPlugin
{
    public class HardwareMonitorPlugin : Plugin
    {
        public override Boolean UsesApplicationApiOnly => true;

        public override Boolean HasNoApplication => true;

        public HardwareMonitorPlugin()
        {
            PluginLog.Init(this.Log);
        }

        public override void Load()
        {
            PluginLog.Info("HardwareMonitor plugin loaded");
        }

        public override void Unload()
        {
            HardwareCardCommand.DisposeAll();
            HardwareMonitorClient.Instance.Dispose();
            PluginLog.Info("HardwareMonitor plugin unloaded");
        }
    }
}

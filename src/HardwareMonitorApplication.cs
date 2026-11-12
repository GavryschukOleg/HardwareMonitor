namespace Loupedeck.HardwareMonitorPlugin
{
    using System;

    // Not linked to any application, but do not delete: Logi Plugin Service refuses to load
    // a plugin assembly that has no ClientApplication subclass ("Cannot load plugin").

    public class HardwareMonitorApplication : ClientApplication
    {
        public HardwareMonitorApplication()
        {
        }

        // This method can be used to link the plugin to a Windows application.
        protected override String GetProcessName() => "";

        // This method can be used to link the plugin to a macOS application.
        protected override String GetBundleName() => "";

        // This method can be used to check whether the application is installed or not.
        public override ClientApplicationStatus GetApplicationStatus() => ClientApplicationStatus.Unknown;
    }
}

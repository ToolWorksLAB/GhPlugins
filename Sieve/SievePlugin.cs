using Sieve.Services;

namespace Sieve
{
    public class SievePlugin : Rhino.PlugIns.PlugIn
    {
        public SievePlugin()
        {
            Instance = this;
        }

        public static SievePlugin Instance { get; private set; }

        protected override void OnShutdown()
        {
            PluginGate.RestoreAllDisabled();
            base.OnShutdown();
        }
    }
}

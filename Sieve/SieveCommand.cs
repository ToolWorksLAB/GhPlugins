using Rhino;
using Rhino.Commands;
using Sieve.UI;

namespace Sieve
{
    [CommandStyle(Style.ScriptRunner)]
    public class SieveCommand : Command
    {
        public SieveCommand()
        {
            Instance = this;
        }

        public static SieveCommand Instance { get; private set; }

        public override string EnglishName => "Sieve";

        protected override Result RunCommand(RhinoDoc doc, RunMode mode)
        {
            var dialog = new SieveDialog();
            dialog.ShowModal(Rhino.UI.RhinoEtoApp.MainWindow);
            return Result.Success;
        }
    }
}

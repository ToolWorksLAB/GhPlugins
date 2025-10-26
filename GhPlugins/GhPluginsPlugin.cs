using Rhino;
using Rhino.PlugIns;
using System;

namespace GhPlugins
{
    ///<summary>
    /// <para>Every RhinoCommon .rhp assembly must have one and only one PlugIn-derived
    /// class. DO NOT create instances of this class yourself. It is the
    /// responsibility of Rhino to create an instance of this class.</para>
    /// <para>To complete plug-in information, please also see all PlugInDescription
    /// attributes in AssemblyInfo.cs (you might need to click "Project" ->
    /// "Show All Files" to see it in the "Solution Explorer" window).</para>
    ///</summary>
    public class GhPluginsPlugin : Rhino.PlugIns.PlugIn
    {
        protected override LoadReturnCode OnLoad(ref string errorMessage)
        {
            // Safety: if last session left things blocked (crash/kill), restore now.
            try { Services.GhPluginBlocker.UnblockEverything(); }
            catch (Exception ex) { RhinoApp.WriteLine("[GhPlugins] Startup restore failed: " + ex.Message); }

            return LoadReturnCode.Success;
        }

        protected override void OnShutdown()
        {
            // Always restore default Grasshopper (all plugins enabled) on Rhino exit
            try { Services.GhPluginBlocker.UnblockEverything(); }
            catch (Exception ex) { RhinoApp.WriteLine("[GhPlugins] Shutdown restore failed: " + ex.Message); }
            base.OnShutdown();
        }
        ///<summary>Gets the only instance of the GhPluginsPlugin plug-in.</summary>
        

        // You can override methods here to change the plug-in behavior on
        // loading and shut down, add options pages to the Rhino _Option command
        // and maintain plug-in wide options in a document.
    }
}
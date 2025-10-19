using System;
using Grasshopper.Kernel;
using GH_IO.Serialization;
using Rhino;
using Rhino.Commands;
using System.IO;

namespace GhPlugins.Services
{
    public class PluginReader
    {
        public static string GetGhpyName(string filePath)
        {
            string name = Path.GetFileName(filePath); // "Example.ghpy" or "Example.ghpy.disabled"

            if (name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".disabled".Length);

            if (name.EndsWith(".ghpy", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - ".ghpy".Length);

            return name; // "Example"
        }

        public static void ReadGHA(string path)
        {

        }
            public static string ReadUserObject(string path)
        {
            var archive = new GH_Archive();
            if (!archive.ReadFromFile(path))
            {
                RhinoApp.WriteLine("❌ Failed to read file: " + path);
                return null;
            }
          
            var userObject = new GH_UserObject(path);
      

            //RhinoApp.WriteLine("✅ Successfully loaded .ghuser:");
            //RhinoApp.WriteLine($"   Name: {userObject.Description.NickName}");
            //RhinoApp.WriteLine($"   Category: {userObject.Description.Category}");
            //RhinoApp.WriteLine($"   SubCategory: {userObject.Description.SubCategory}");
            //RhinoApp.WriteLine($"   Description: {userObject.Description.Description}");
            //RhinoApp.WriteLine($"   Path: {path}");

            return userObject.Description.Category; // or return whatever info you want
        }


    }
}

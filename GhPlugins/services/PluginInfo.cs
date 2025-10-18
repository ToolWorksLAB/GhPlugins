using System;
using System.Linq;
using System.Reflection;
using Grasshopper.Kernel;
using Rhino;

namespace GhPlugins.Services
{
    public class PluginInfo
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string AuthorName { get; set; }
        public string AuthorContact { get; set; }
        public Guid Id { get; set; }
        public string Location { get; set; }
    }

    public static class GhaInfoReader
    {
        public static PluginInfo ReadPluginInfo(string ghaPath)
        {
            try
            {
                // Load the GHA (must be running inside Rhino/Grasshopper so deps resolve)
                Assembly asm = Assembly.LoadFrom(ghaPath);

                // Pick a concrete, public subclass with a public parameterless ctor
                var infoType = asm.GetTypes()
                    .Where(t => typeof(GH_AssemblyInfo).IsAssignableFrom(t))
                    .Where(t => t.IsClass && !t.IsAbstract && t.IsPublic)
                    .FirstOrDefault(t => t.GetConstructor(Type.EmptyTypes) != null);

                if (infoType == null)
                {
                   // RhinoApp.WriteLine($"[GhaInfoReader] No usable GH_AssemblyInfo in: {ghaPath}");
                    return null;
                }

                var info = (GH_AssemblyInfo)Activator.CreateInstance(infoType);

                // Map to a lightweight DTO to avoid keeping plugin objects alive
                return new PluginInfo
                {
                    Name = info.Name,
                    Version = info.Version,
                    Description = info.Description,
                    AuthorName = info.AuthorName,
                    AuthorContact = info.AuthorContact,
                    Id = info.Id,
                    Location = info.Location
                };
            }
            catch (ReflectionTypeLoadException rtle)
            {
                // Useful to see which loader exceptions occurred
                //RhinoApp.WriteLine($"[GhaInfoReader] ReflectionTypeLoadException for {ghaPath}: {rtle.Message}");
                //foreach (var e in rtle.LoaderExceptions)
                //    RhinoApp.WriteLine("  -> " + e.Message);
                return null;
            }
            catch (Exception ex)
            {
               // RhinoApp.WriteLine($"[GhaInfoReader] Error reading {ghaPath}: {ex.Message}");
                return null;
            }
        }
    }
}

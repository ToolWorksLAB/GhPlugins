namespace GhPlugins.Models
{
    public class PluginItem
    {
        public string Name { get; set; }
        public string Path { get; set; }
        public bool IsSelected { get; set; }

        public PluginItem(string name, string path)
        {
            Name = name;
            Path = path;
            IsSelected = false;
        }

        public override string ToString()
        {
            return Name;
        }
    }
}

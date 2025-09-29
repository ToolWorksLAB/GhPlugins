using Eto.Forms;
using Eto.Drawing;

namespace GhPlugins.UI
{
    public class SelectListDialog : Dialog<DialogResult>
    {
        private ListBox listBox;
        public string SelectedItem => listBox.SelectedValue?.ToString();

        public SelectListDialog(string title, string[] items)
        {
            Title = title;
            ClientSize = new Size(300, 300);
            Resizable = false;

            listBox = new ListBox
            {
                DataStore = items,
                Width = 250,
                Height = 200
            };

            var okButton = new Button { Text = "OK" };
            okButton.Click += (s, e) =>
            {
                if (!string.IsNullOrEmpty(SelectedItem))
                {
                    Close(DialogResult.Ok);
                }
            };

            Content = new StackLayout
            {
                Padding = new Padding(10),
                Spacing = 10,
                Items =
                {
                    listBox,
                    okButton
                }
            };
        }
    }
}

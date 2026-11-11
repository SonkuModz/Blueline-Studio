using System.Windows;

namespace Blueline.App.Shell;

public partial class NewProjectDialog : Window
{
    private static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Blueline Projects");
    private string _parent = Root;

    public NewProjectDialog()
    {
        InitializeComponent();
        NameBox.Text = "My Mod";
        NameBox.SelectAll();
        Loaded += (_, _) => NameBox.Focus();
    }

    public string ProjectName => NameBox.Text.Trim();
    public string ProjectFolder => FolderBox.Text.Trim();

    private void OnNameChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        var safe = string.Concat(NameBox.Text.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        FolderBox.Text = Path.Combine(_parent, safe);
    }

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        if (AppServices.PickFolder("Choose where to keep this project") is { } folder)
        {
            _parent = folder;
            OnNameChanged(sender, null!);
        }
    }

    private void OnCreate(object sender, RoutedEventArgs e)
    {
        var problem = ProjectName.Length == 0 ? "A project needs a name."
            : ProjectFolder.Length == 0 ? "A project needs a folder."
            : File.Exists(Path.Combine(ProjectFolder, "project.json")) ? "That folder already holds a project."
            : null;
        Problem.Text = problem ?? "";
        Problem.Visibility = problem == null ? Visibility.Collapsed : Visibility.Visible;
        if (problem == null)
            DialogResult = true;
    }
}

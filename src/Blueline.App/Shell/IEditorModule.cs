using System.Windows;

namespace Blueline.App.Shell;

/// <summary>A workspace in the top tab bar. Modules know nothing about each other, only the shared services.</summary>
public interface IEditorModule
{
    string Title { get; }
    FrameworkElement View { get; }

    /// <summary>Called each time the tab is shown.</summary>
    void Activated();

    /// <summary>Called when another module asks to open a game path here.</summary>
    void OpenPath(string path);
}

using System.Text;
using System.Windows;
using System.Windows.Controls;
using Blueline.App.Controls;
using Blueline.App.Shell;
using Blueline.Core;
using Blueline.Formats.Models;
using Microsoft.Win32;

namespace Blueline.App.Modules;

/// <summary>Model inspection: 3D preview and OBJ export of the game's `.mcd` files.</summary>
public partial class ModelsModule : UserControl, IEditorModule
{
    private readonly AppServices _services;
    private McdFile? _model;
    private int _generation;

    public ModelsModule(AppServices services)
    {
        _services = services;
        InitializeComponent();
        Browser.SetScope(r => r.Category == AssetCategory.Model);
        Browser.SelectionChanged += ShowSelection;
        ShowSelection();
    }

    public string Title => "Models";
    public FrameworkElement View => this;
    public AssetBrowser BrowserControl => Browser;
    public bool IsLoading { get; private set; }

    public void Activated() => Browser.Load(_services.Index);

    public void OpenPath(string path)
    {
        Browser.Load(_services.Index);
        Browser.Reveal(path);
    }

    private void ShowSelection()
    {
        var generation = ++_generation;
        var row = Browser.Selected;
        ExportObj.IsEnabled = false;
        ExportSelected.IsEnabled = Browser.SelectedRows.Count > 1;
        ApplyHide.IsEnabled = false;
        Shapes.ItemsSource = null;
        _model = null;

        if (row == null)
        {
            NameText.Text = "None selected";
            FactsText.Text = "";
            Viewport.ShowMessage("Select a model to preview it.");
            return;
        }

        NameText.Text = row.Name;
        FactsText.Text = "Reading";
        Viewport.ShowMessage("Loading");
        IsLoading = true;
        var workspace = _services.Workspace;
        var path = row.Path;

        Task.Run(() =>
        {
            var model = McdFile.Parse(workspace.ReadCurrent(path) ?? []);
            return (model, ModelViewport.Prepare(model));
        }).ContinueWith(task =>
        {
            if (generation != _generation)
                return;
            IsLoading = false;
            if (task.IsFaulted)
            {
                FactsText.Text = "";
                Viewport.ShowMessage("This model could not be read: " + task.Exception!.GetBaseException().Message);
                return;
            }
            var (model, prepared) = task.Result;
            _model = model;
            Shapes.ItemsSource = model.Shapes.Select(s => s.Name).Distinct().ToList();
            ExportObj.IsEnabled = ApplyHide.IsEnabled = model.Shapes.Count > 0;
            FactsText.Text = $"{model.Shapes.Count} shape{(model.Shapes.Count == 1 ? "" : "s")}, {model.TriangleCount:N0} triangles, {model.VertexCount:N0} vertices, {model.Formats.Count} vertex format{(model.Formats.Count == 1 ? "" : "s")}"
                + (model.ShapesWerePaired ? ". Shapes were inferred from buffer order, not binding records." : "")
                + (prepared.Truncated ? ". Too large to preview whole, so some shapes are left out; OBJ export includes everything." : "");
            if (model.Shapes.Count == 0)
                Viewport.ShowMessage("This file holds no drawable shapes.");
            else
                Viewport.Show(prepared);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnApplyHide(object sender, RoutedEventArgs e)
    {
        if (_model == null)
            return;
        var model = _model;
        var hidden = Shapes.SelectedItems.Cast<string>().ToHashSet(StringComparer.Ordinal);
        Task.Run(() => ModelViewport.Prepare(model, hidden)).ContinueWith(task =>
        {
            if (!task.IsFaulted && ReferenceEquals(model, _model))
                Viewport.Show(task.Result);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void OnExportObj(object sender, RoutedEventArgs e)
    {
        if (Browser.Selected is not { } row || _model == null)
            return;
        var dialog = new SaveFileDialog { Title = "Export OBJ", FileName = Path.ChangeExtension(row.Name, ".obj"), Filter = "Wavefront OBJ|*.obj" };
        if (dialog.ShowDialog() != true)
            return;
        var model = _model;
        var destination = dialog.FileName;
        _ = _services.Jobs.RunAsync($"Exporting {row.Name}", _ =>
        {
            using var writer = new StreamWriter(destination, false, new UTF8Encoding(false));
            model.WriteObj(writer, row.Path);
            return destination;
        }, path => _services.Notify($"Saved {path}"));
    }

    private void OnExportSelected(object sender, RoutedEventArgs e)
    {
        var rows = Browser.SelectedRows;
        if (AppServices.PickFolder($"Export {rows.Count} models as OBJ") is not { } folder)
            return;
        var workspace = _services.Workspace;
        _ = _services.Jobs.RunAsync($"Exporting {rows.Count} models", context =>
        {
            for (var i = 0; i < rows.Count; i++)
            {
                context.Cancel.ThrowIfCancellationRequested();
                var target = Path.Combine(folder, Path.ChangeExtension(rows[i].Name, ".obj"));
                using var writer = new StreamWriter(target, false, new UTF8Encoding(false));
                McdFile.Parse(workspace.ReadCurrent(rows[i].Path) ?? []).WriteObj(writer, rows[i].Path);
                context.Progress($"Exported {rows[i].Name}", i + 1, rows.Count);
            }
            return rows.Count;
        }, count => _services.Notify($"Exported {count} models to {folder}."));
    }
}

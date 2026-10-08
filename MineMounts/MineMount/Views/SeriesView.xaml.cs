using System.Windows;
using System.Windows.Controls;
using MineMount.Services;
using MineMount.ViewModels;

namespace MineMount.Views;

public partial class SeriesView : UserControl
{
    public SeriesView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateColumns();
        Loaded += (_, _) => UpdateColumns();
    }

    /// <summary>Grilla responsive: 1 columna bajo 800px, hasta 4 en 4K.</summary>
    private void UpdateColumns()
    {
        if (DataContext is not SeriesViewModel vm) return;
        var w = ActualWidth;
        vm.GridColumns = w < 800 ? 1 : (int)System.Math.Clamp(w / 340, 1, 4);
    }

    /// <summary>Menú ⋯: Reparar, Abrir carpeta, Desinstalar.</summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.Tag is not SeriesCard card) return;
        if (DataContext is not SeriesViewModel vm) return;

        var menu = new ContextMenu();
        var repair = new MenuItem { Header = Loc.T("S.Series.MenuRepair") };
        repair.Click += (_, _) => { if (vm.RepairCardCommand.CanExecute(card)) vm.RepairCardCommand.Execute(card); };
        var open = new MenuItem { Header = Loc.T("S.Series.MenuOpen") };
        open.Click += (_, _) => { if (vm.OpenFolderCommand.CanExecute(card)) vm.OpenFolderCommand.Execute(card); };
        var uninstall = new MenuItem { Header = Loc.T("S.Series.MenuUninstall") };
        uninstall.Click += (_, _) => { if (vm.UninstallCardCommand.CanExecute(card)) vm.UninstallCardCommand.Execute(card); };
        menu.Items.Add(repair);
        menu.Items.Add(open);
        menu.Items.Add(uninstall);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;

namespace AppShelf.App;

public sealed class PlacementDialog : ShelfDialog
{
    public PlacementDialog(ShelfItem folder) : base("Разместить папку", 550)
    {
        Heading(folder.Name, "Показывайте папку на рабочем столе, панели задач или в обоих местах.");
        var preview = new Image { Width = 96, Height = 96, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 0, 0, 10) };
        Body.Children.Add(preview);
        var contents = FolderPreview.Applications(folder);
        var names = new TextBlock { Text = contents.Count == 0 ? "Папка пока пуста" : string.Join(" · ", contents.Select(x => x.Name)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 20) };
        names.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(names);
        var desktop = new Button { Content = "Добавить на рабочий стол", HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10) };
        var taskbar = new Button { Content = (folder.Settings.ShowOnTaskbar ? "Убрать с панели задач" : "Показать на панели задач"), HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(14, 12, 14, 12) };
        Body.Children.Add(desktop); Body.Children.Add(taskbar);
        var status = new TextBlock { Text = "Иконка повторяет содержимое папки и обновляется автоматически.", TextWrapping = TextWrapping.Wrap, LineHeight = 20, Margin = new Thickness(0, 16, 0, 0) };
        status.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); Body.Children.Add(status);
        Body.Children.Add(new TextBlock { Text = "Размещение на панели задач восстанавливается при запуске AppShelf.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) });
        Actions.Children.Clear(); Accept.Content = "Готово"; Accept.IsDefault = true; Accept.Click += (_, _) => Close(); Actions.Children.Add(Accept);
        string? desktopPath = null;
        Loaded += async (_, _) =>
        {
            try
            {
                desktopPath = await App.Current.Publisher.DesktopShortcutAsync(folder.Id);
                desktop.Content = desktopPath is null ? "Добавить на рабочий стол" : "Убрать с рабочего стола";
                preview.Source = (await FolderIconWriter.PrepareAsync(folder)).Preview;
            }
            catch (Exception ex) { status.Text = ex.Message; }
        };
        desktop.Click += async (_, _) =>
        {
            desktop.IsEnabled = false;
            try
            {
                if (desktopPath is null)
                {
                    desktopPath = await App.Current.Publisher.AddDesktopAsync(folder.Id);
                    status.Text = "Папка добавлена на рабочий стол.";
                    desktop.Content = "Убрать с рабочего стола";
                }
                else
                {
                    await App.Current.Publisher.RemoveDesktopAsync(folder.Id);
                    desktopPath = null;
                    status.Text = "Папка убрана с рабочего стола.";
                    desktop.Content = "Добавить на рабочий стол";
                }
            }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { desktop.IsEnabled = true; }
        };
        taskbar.Click += (_, _) =>
        {
            taskbar.IsEnabled = false;
            try { App.Current.Session.Change(config => { var current = ShelfTree.Find(config, folder.Id)!; current.Settings.ShowOnTaskbar = !current.Settings.ShowOnTaskbar; }); Close(); }
            catch (Exception ex) { status.Text = ex.Message; taskbar.IsEnabled = true; }
        };
    }
}



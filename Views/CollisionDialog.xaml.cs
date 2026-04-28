using System.IO;
using System.Windows;
using FileSorter.Services;

namespace FileSorter.Views;

public partial class CollisionDialog : Window
{
    public CollisionDecision Decision { get; private set; } = CollisionDecision.Suffix;
    public bool ApplyToAll => ApplyToAllCheck.IsChecked == true;

    public CollisionDialog(string sourcePath)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ThemeService.Current.ApplyChromeTo(this);
        var name = Path.GetFileName(sourcePath);
        MessageText.Text = string.Format(
            LocalizationService.Current.T("CollisionDialogText"), name);
    }

    private void Suffix_Click (object sender, RoutedEventArgs e) { Decision = CollisionDecision.Suffix;  DialogResult = true; }
    private void Skip_Click   (object sender, RoutedEventArgs e) { Decision = CollisionDecision.Skip;    DialogResult = true; }
    private void Replace_Click(object sender, RoutedEventArgs e) { Decision = CollisionDecision.Replace; DialogResult = true; }
}

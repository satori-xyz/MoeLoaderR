using System.Linq;
using System.Windows;
using MoeLoaderR.Core;

namespace MoeLoaderR.Wpf;

public partial class DuplicateDeleteWindow : Window
{
    public DuplicateDeleteWindow(DuplicateDeletePlan plan)
    {
        InitializeComponent();
        SummaryText.Text = $"共 {plan.Groups.Count:N0} 组，保留 {plan.Groups.Count:N0} 个文件，删除 {plan.DeleteCount:N0} 个重复文件。";
        ConfirmButton.Content = $"永久删除 {plan.DeleteCount:N0} 个文件";
        PlanGrid.ItemsSource = plan.Groups.SelectMany(group => group.Remove.Prepend(group.Keep)
            .Select(row => new { Action = row == group.Keep ? "保留" : "删除", Group = row.GroupText, Name = row.Name, Path = row.Path, Size = row.SizeText })).ToArray();
    }
    private void ConfirmDelete(object sender, RoutedEventArgs e) => DialogResult = true;
}

using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using MoeLoaderR.Core;

namespace MoeLoaderR.Wpf;

public partial class ImageCheckWindow : Window
{
    private readonly ObservableCollection<ImageCheckIssue> _issues = new();
    private CancellationTokenSource _scan;
    private bool _closed;
    private ImageCheckProgress _last = new(0, 0, 0, "");
    private DuplicateCheckProgress _lastDuplicate = new("", 0, 0, 0, 0, 0, 0, "");
    private string _completedDuplicateRoot;
    private readonly Settings _settings;

    public ImageCheckWindow(string initialFolder = null, Settings settings = null)
    {
        _settings = settings;
        InitializeComponent();
        FolderText.Text = initialFolder ?? "";
        ResultsGrid.ItemsSource = _issues;
        ModeChanged(this, new RoutedEventArgs());
        Closed += (_, _) => { _closed = true; _scan?.Cancel(); _issues.Clear(); };
    }

    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (FooterHint == null || _scan != null) return;
        var duplicate = DuplicateMode.IsChecked == true;
        _issues.Clear();
        _completedDuplicateRoot = null;
        DeleteDuplicatesButton.Visibility = duplicate ? Visibility.Visible : Visibility.Collapsed;
        DeleteDuplicatesButton.IsEnabled = false;
        RepairButton.Visibility = duplicate ? Visibility.Collapsed : Visibility.Visible;
        RepairButton.IsEnabled = false;
        ScanPolicyText.Text = "扫描只读 · 修改前需确认";
        GroupColumn.Visibility = duplicate ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Header = duplicate ? "路径 / 说明" : "说明";
        FooterHint.Text = duplicate
            ? "按大小筛选，再用 MD5 和逐字节核对确认。同组文件内容完全相同；重新压缩或修改元数据的图片不算重复。"
            : "只检查 JPG、PNG、WebP 的文件结构和结束标记，不解码整张图片。通过检查不代表文件绝对完整。";
        StatusText.Text = duplicate
            ? "支持 JPG、PNG、WebP、GIF、BMP、TIFF、AVIF、HEIC、JXL 等常见图片格式，结果按重复组列出。"
            : "选择文件夹后开始检查，结果仅列出疑似异常和无法读取的文件。";
        CurrentPathText.Text = "";
        StartButton.Content = duplicate ? "开始查重" : "开始检查";
        ExportButton.IsEnabled = false;
        SelectAllButton.IsEnabled = false;
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "选择要检查的图片文件夹", Multiselect = false };
        if (dialog.ShowDialog(this) == true) FolderText.Text = dialog.FolderName;
    }

    private async void StartScan(object sender, RoutedEventArgs e)
    {
        if (_scan != null) return;
        var folder = FolderText.Text.Trim();
        if (string.IsNullOrWhiteSpace(folder)) { StatusText.Text = "请先选择要检查的文件夹。"; return; }
        var recursive = RecursiveCheckBox.IsChecked == true;
        var duplicate = DuplicateMode.IsChecked == true;
        using var cts = new CancellationTokenSource();
        _scan = cts;
        _completedDuplicateRoot = null;
        _issues.Clear();
        _last = new(0, 0, 0, "");
        _lastDuplicate = new("统计文件大小", 0, 0, 0, 0, 0, 0, "");
        SetRunning(true);
        StatusText.Text = "正在检查…";
        CurrentPathText.Text = "";
        var progress = new Progress<ImageCheckProgress>(value =>
        {
            if (_closed || _scan != cts) return;
            _last = value;
            StatusText.Text = Describe("正在检查", value);
            CurrentPathText.Text = value.CurrentPath;
        });
        try
        {
            void ReportIssue(ImageCheckIssue issue)
            {
                if (cts.IsCancellationRequested) return;
                Dispatcher.Invoke(() => { if (!_closed && _scan == cts) _issues.Add(issue); });
            }
            if (duplicate)
            {
                var duplicateProgress = new Progress<DuplicateCheckProgress>(value =>
                {
                    if (_closed || _scan != cts) return;
                    _lastDuplicate = value;
                    StatusText.Text = DescribeDuplicate(value.Phase, value);
                    CurrentPathText.Text = value.CurrentPath;
                });
                _lastDuplicate = await Task.Run(() => DuplicateImageScanner.Scan(folder, recursive, ReportIssue, duplicateProgress, cts.Token), cts.Token);
                if (!cts.IsCancellationRequested) _completedDuplicateRoot = Path.GetFullPath(folder);
                if (!_closed) StatusText.Text = DescribeDuplicate("查重完成", _lastDuplicate);
            }
            else
            {
                _last = await Task.Run(() => ImageIntegrityScanner.Scan(folder, recursive, ReportIssue, progress, cts.Token), cts.Token);
                if (!_closed) StatusText.Text = Describe("检查完成", _last);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closed) StatusText.Text = duplicate
                ? DescribeDuplicate("已停止（结果不完整）", _lastDuplicate) : Describe("已停止（结果不完整）", _last);
        }
        catch (Exception ex)
        {
            if (!_closed) StatusText.Text = "检查未完成：" + ex.Message;
        }
        finally
        {
            _scan = null;
            if (!_closed) SetRunning(false);
        }
    }

    private static string Describe(string state, ImageCheckProgress value) =>
        $"{state} · 已检查 {value.Checked:N0} 张 · 疑似不完整 {value.Suspected:N0} 张 · 无法读取/需重查 {value.Unreadable:N0} 项";

    private static string DescribeDuplicate(string state, DuplicateCheckProgress value) =>
        $"{state} · 图片 {value.Files:N0} 张 · MD5 {value.Hashed:N0}/{value.Candidates:N0} · 重复 {value.Groups:N0} 组（{value.DuplicateFiles:N0} 个文件） · 无法读取/需重查 {value.Unreadable:N0} 项";

    private void SetRunning(bool running)
    {
        StartButton.IsEnabled = BrowseButton.IsEnabled = FolderText.IsEnabled = RecursiveCheckBox.IsEnabled = !running;
        ModeChoices.IsEnabled = !running;
        CancelButton.IsEnabled = running;
        ScanProgress.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        ScanProgress.IsIndeterminate = running;
        ExportButton.IsEnabled = !running && _issues.Count > 0;
        SelectAllButton.IsEnabled = !running && _issues.Count > 0;
        DeleteDuplicatesButton.IsEnabled = !running && _completedDuplicateRoot != null && _issues.Any(row => row.Kind == "重复文件" && row.GroupId != null);
        UpdateRepairButton();
    }

    private void UpdateRepairButton()
    {
        if (RepairButton == null) return;
        RepairButton.IsEnabled = _scan == null && _settings != null && IntegrityMode.IsChecked == true &&
            ResultsGrid.SelectedItems.OfType<ImageCheckIssue>().Any(row => row.Kind == "疑似不完整");
    }

    private void SelectAllResults(object sender, RoutedEventArgs e)
    {
        if (_scan == null) ResultsGrid.SelectAll();
    }

    private async void RepairSelected(object sender, RoutedEventArgs e)
    {
        if (_scan != null || _settings == null || IntegrityMode.IsChecked != true) return;
        var selected = ResultsGrid.SelectedItems.OfType<ImageCheckIssue>().ToArray();
        var candidates = selected.Where(row => row.Kind == "疑似不完整" && ImageRepair.Identify(row.Path) != null).ToArray();
        if (candidates.Length == 0)
        {
            MessageBox.Show(this, "只能识别文件名中含站点及图片 ID 的图片，例如 yande 12345、konachan 12345、konachan-g 12345。\n此文件未修改。", "无法识别来源", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this, $"已选 {selected.Length} 项，将重新下载 {candidates.Length} 张，跳过 {selected.Length - candidates.Length} 项（非疑似损坏或来源无法识别）。\n\n逐张下载网站原图，校验成功后删除旧文件；不覆盖已有同名文件。单张失败会继续处理下一张，可随时停止。是否继续？", "批量删除并重新下载", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await RepairBatchAsync(selected, candidates);
    }

    private async Task RepairBatchAsync(ImageCheckIssue[] selected, ImageCheckIssue[] candidates)
    {
        using var cts = new CancellationTokenSource();
        _scan = cts;
        SetRunning(true);
        var succeeded = 0;
        var failed = 0;
        var skipped = selected.Length - candidates.Length;
        var active = 0;
        try
        {
            foreach (var row in selected.Except(candidates))
            {
                if (row.Kind != "疑似不完整") continue;
                var index = _issues.IndexOf(row);
                if (index >= 0) _issues[index] = row with { Detail = "已跳过：文件名无法识别 Yande / Konachan 站点和图片 ID。" };
            }
            foreach (var issue in candidates)
            {
                if (cts.IsCancellationRequested) break;
                var number = ++active;
                CurrentPathText.Text = issue.Path;
                var progress = new Progress<string>(value =>
                {
                    if (!_closed && _scan == cts && active == number && !cts.IsCancellationRequested)
                        StatusText.Text = $"{number}/{candidates.Length} · 成功 {succeeded} · 失败 {failed} · {value}";
                });
                try
                {
                    var savedPath = await Task.Run(() => ImageRepair.RepairAsync(issue.Path, _settings, progress, cts.Token));
                    succeeded++;
                    if (!_closed)
                    {
                        var index = _issues.IndexOf(issue);
                        if (index >= 0) _issues[index] = issue with { Path = savedPath, Kind = "已重新下载", Size = new FileInfo(savedPath).Length, Detail = "原图下载并校验成功，旧文件已替换。" };
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    failed++;
                    Ex.Log("图片检查重新下载失败", ex);
                    if (!_closed)
                    {
                        var index = _issues.IndexOf(issue);
                        if (index >= 0) _issues[index] = issue with { Detail = "重下失败：" + (ex is OperationCanceledException ? "下载超时" : ex.Message) };
                    }
                }
            }
            active = -1; // Ignore progress callbacks queued by the final download.
            if (!_closed) StatusText.Text = $"{(cts.IsCancellationRequested ? "已停止" : "批量处理完成")} · 成功 {succeeded} · 失败 {failed} · 跳过 {skipped} · 未完成 {candidates.Length - succeeded - failed}";
        }
        finally { _scan = null; if (!_closed) SetRunning(false); }
    }

    private async void DeleteDuplicates(object sender, RoutedEventArgs e)
    {
        if (_scan != null || _completedDuplicateRoot == null) return;
        DuplicateDeletePlan plan;
        try { plan = DuplicateImageDeletion.CreatePlan(_completedDuplicateRoot, _issues); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "无法准备删除清单", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (plan.DeleteCount == 0) return;
        var confirm = new DuplicateDeleteWindow(plan) { Owner = this };
        if (confirm.ShowDialog() != true) return;
        using var cts = new CancellationTokenSource();
        _scan = cts;
        _completedDuplicateRoot = null; // A new scan is required before another deletion attempt.
        SetRunning(true);
        StatusText.Text = "正在重新核对并删除重复文件…";
        var deleted = 0;
        var skipped = 0;
        var progress = new Progress<string>(path =>
        {
            if (!_closed && _scan == cts) CurrentPathText.Text = path;
        });
        try
        {
            var summary = await Task.Run(() => DuplicateImageDeletion.Delete(plan, result =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (_closed || _scan != cts) return;
                    if (result.Deleted) deleted++; else skipped++;
                    var index = _issues.IndexOf(result.File);
                    if (index >= 0) _issues[index] = result.File with { Kind = result.Deleted ? "已删除" : "未删除", Detail = result.Message };
                    StatusText.Text = $"正在处理 · 已删除 {deleted:N0} 个 · 跳过 {skipped:N0} 个";
                });
            }, progress, cts.Token));
            if (!_closed)
                StatusText.Text = $"{(summary.Canceled ? "已停止" : "处理完成")} · 已删除 {summary.Deleted:N0} 个 · 跳过 {summary.Skipped:N0} 个 · 再次删除前请重新查重";
        }
        catch (Exception ex)
        {
            if (!_closed) StatusText.Text = $"操作未完成 · 已删除 {deleted:N0} 个：{ex.Message}";
        }
        finally
        {
            _scan = null;
            if (!_closed) SetRunning(false);
        }
    }

    private void CancelScan(object sender, RoutedEventArgs e)
    {
        _scan?.Cancel();
        CancelButton.IsEnabled = false;
        StatusText.Text = "正在停止，等待当前文件读取结束…";
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OpenLocationButton != null) OpenLocationButton.IsEnabled = ResultsGrid.SelectedItem is ImageCheckIssue;
        UpdateRepairButton();
    }

    private void OpenSelectedLocation(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not ImageCheckIssue issue) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{issue.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法打开文件位置", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private async void ExportResults(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "CSV 清单 (*.csv)|*.csv", FileName = $"图片检查-{DateTime.Now:yyyyMMdd-HHmmss}.csv" };
        if (dialog.ShowDialog(this) != true) return;
        var issues = _issues.ToArray();
        var status = StatusText.Text;
        try
        {
            await Task.Run(() =>
            {
                static string Quote(string value)
                {
                    // Avoid interpreting user-controlled names as spreadsheet formulas.
                    if (value.Length > 0 && "=+-@\t\r\n".Contains(value[0])) value = "'" + value;
                    return "\"" + value.Replace("\"", "\"\"") + "\"";
                }
                using var writer = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true));
                writer.WriteLine("重复组,文件路径,大小（字节）,MD5,结果,说明,扫描状态");
                foreach (var issue in issues)
                    writer.WriteLine(string.Join(",", issue.GroupId?.ToString() ?? "", Quote(issue.Path), issue.Size?.ToString() ?? "", Quote(issue.Md5), Quote(issue.Kind), Quote(issue.Detail), Quote(status)));
            });
            if (!_closed) MessageBox.Show(this, "检查清单已导出。", "图片检查", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            if (!_closed) MessageBox.Show(this, ex.Message, "导出失败", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

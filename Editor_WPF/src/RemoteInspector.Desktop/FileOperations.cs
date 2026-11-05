using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using RemoteInspector.Commands;
using RemoteInspector.Core;
using RemoteInspector.Protocol;

namespace RemoteInspector.Desktop;

public partial class MainWindow
{
    private string currentRemoteDirectory = "";
    private bool CanModifyRemoteFiles => !closing && snapshot == null && operation == null && Selected?.Session.IsConnected == true;
    private void RemoteFilesMenu_Opened(object sender, RoutedEventArgs e)
    {
        CreateRemoteFolder.IsEnabled = UploadRemoteFiles.IsEnabled = CanModifyRemoteFiles;
        DownloadRemoteSelection.IsEnabled = DeleteRemoteSelection.IsEnabled = CanModifyRemoteFiles && FilesGrid.SelectedItem is FileRow;
    }
    private static string[] DroppedFiles(IDataObject data) => data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files ? files : Array.Empty<string>();
    private void Files_DragOver(object sender, DragEventArgs e)
    {
        var paths = DroppedFiles(e.Data);
        e.Effects = CanModifyRemoteFiles && paths.Length > 0 && paths.All(File.Exists) && e.AllowedEffects.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private async void Files_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true; e.Effects = DragDropEffects.None;
        if (!CanModifyRemoteFiles) { StatusLabel.Text = "请先连接设备，并等待当前操作完成。"; return; }
        var paths = DroppedFiles(e.Data);
        if (paths.Length == 0 || paths.Any(p => !File.Exists(p))) { StatusLabel.Text = "请拖入本地文件；暂不支持拖入文件夹。"; return; }
        e.Effects = DragDropEffects.Copy;
        await UploadFilesAsync(paths);
    }
    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (!CanModifyRemoteFiles) { StatusLabel.Text = "请先连接设备，并等待当前操作完成。"; return; }
        var open = new OpenFileDialog { Multiselect = true, Title = "上传文件到当前远端目录" };
        if (open.ShowDialog(this) == true) await UploadFilesAsync(open.FileNames);
    }
    private Task UploadFilesAsync(string[] paths)
    {
        // Capture the displayed directory, rather than an edited but unsubmitted path field.
        var target = Selected?.Session; string directory = currentRemoteDirectory;
        return Run(async token =>
        {
            if (target == null || !IsCurrent(target) || !target.IsConnected || snapshot != null) throw new InvalidOperationException("请先连接设备。");
            if (paths.Length == 0 || paths.Any(p => !File.Exists(p))) throw new ArgumentException("请选择本地文件，暂不支持上传文件夹。");
            if (paths.Select(Path.GetFileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Length) throw new ArgumentException("所选文件存在同名文件，请分批上传。");
            for (int i = 0; i < paths.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                string remote = Join(directory, Path.GetFileName(paths[i]));
                string prefix = $"[{i + 1}/{paths.Length}] {remote} · ";
                TransferStatus.Text = "上传到：" + remote;
                await target.UploadAsync(paths[i], remote, new Progress<string>(m =>
                { if (IsCurrent(target)) TransferStatus.Text = prefix + m; }), token);
            }
            if (!IsCurrent(target)) return;
            await Browse(target, directory, token);
            StatusLabel.Text = TransferStatus.Text = $"已上传 {paths.Length} 个文件到：" + (directory.Length == 0 ? "PersistentRoot" : directory);
        }, "Upload_Click");
    }
    private async void Mkdir_Click(object sender, RoutedEventArgs e)
    {
        if (!CanModifyRemoteFiles) { StatusLabel.Text = "请先连接设备，并等待当前操作完成。"; return; }
        var input = new TextBox { Margin = new Thickness(0, 10, 0, 12), MinWidth = 320 };
        var ok = new Button { Content = "创建", IsDefault = true, MinWidth = 75 };
        var cancel = new Button { Content = "取消", IsCancel = true, MinWidth = 75 };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(cancel); buttons.Children.Add(ok);
        var panel = new StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new TextBlock { Text = "文件夹名称", FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "当前目录：" + (currentRemoteDirectory.Length == 0 ? "PersistentRoot" : currentRemoteDirectory), Margin = new Thickness(0, 6, 0, 0), TextWrapping = TextWrapping.Wrap });
        var error = new TextBlock { Foreground = System.Windows.Media.Brushes.LightCoral, TextWrapping = TextWrapping.Wrap, MaxWidth = 360 };
        panel.Children.Add(input); panel.Children.Add(error); panel.Children.Add(buttons);
        var dialog = new Window { Owner = this, Title = "新建远端文件夹", SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
        ok.Click += (_, _) => { if (IsValidFolderName(input.Text.Trim())) dialog.DialogResult = true; else { error.Text = "请输入单个文件夹名，不能包含路径或特殊字符。"; input.Focus(); input.SelectAll(); } };
        dialog.Loaded += (_, _) => input.Focus();
        if (dialog.ShowDialog() == true) await CreateDirectoryAsync(input.Text.Trim());
    }
    private static bool IsValidFolderName(string name) => !string.IsNullOrWhiteSpace(name) && name is not "." and not ".." && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    private Task CreateDirectoryAsync(string name)
    {
        var target = Selected?.Session; string directory = currentRemoteDirectory;
        return Run(async token =>
        {
            if (target == null || !IsCurrent(target) || !target.IsConnected || snapshot != null) throw new InvalidOperationException("请先连接设备。");
            if (!IsValidFolderName(name)) throw new ArgumentException("请输入单个有效文件夹名称。");
            var result = Wire.Body<FileBrowseMkdirResponse>(await target.RequestAsync(Command.FILE_BROWSE_MKDIR,
                new FileBrowseMkdirRequest { relativePath = Join(directory, name) }, token, 15, Command.FILE_BROWSE_MKDIR_RESPONSE));
            if (!result.success) throw new IOException(result.error);
            if (IsCurrent(target)) await Browse(target, directory, token);
        }, "Mkdir_Click");
    }
}

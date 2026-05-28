using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ModEngine
{
    public class QueueItem
    {
        public string Id { get; set; } = "";
        public string Display { get; set; } = "";
        public override string ToString() => Display;
    }

    public class InstalledAddon
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Description { get; set; } = "";
        public string LogoUrl { get; set; } = "";
        public string InstallPath { get; set; } = "";
        public long SizeBytes { get; set; }
        public DateTime InstalledDate { get; set; }
        public List<string> Conflicts { get; set; } = new();
        public string? BackupPath { get; set; }
        public long? WorkshopTimeUpdated { get; set; }
        public List<string> Tags { get; set; } = new();

        public string FormattedSize
        {
            get
            {
                if (SizeBytes >= 1_073_741_824) return $"{SizeBytes / 1_073_741_824.0:F2} GB";
                if (SizeBytes >= 1_048_576) return $"{SizeBytes / 1_048_576.0:F2} MB";
                if (SizeBytes >= 1_024) return $"{SizeBytes / 1_024.0:F2} KB";
                return $"{SizeBytes} bytes";
            }
        }

        public bool HasConflicts => Conflicts.Count > 0;
        public bool HasBackup => !string.IsNullOrEmpty(BackupPath);
        public bool UpdateAvailable
        {
            get
            {
                if (WorkshopTimeUpdated == null || InstalledDate == DateTime.MinValue) return false;
                var workshopDt = DateTimeOffset.FromUnixTimeSeconds(WorkshopTimeUpdated.Value).DateTime;
                return workshopDt > InstalledDate;
            }
        }
    }

    public class BackupEntry
    {
        public string BackupPath { get; set; } = "";
        public string AddonTitle { get; set; } = "";
        public string AddonId { get; set; } = "";
        public DateTime BackupDate { get; set; }
        public long SizeBytes { get; set; }
        public bool IsOrphaned { get; set; }
        public string FormattedSize
        {
            get
            {
                if (SizeBytes >= 1_073_741_824) return $"{SizeBytes / 1_073_741_824.0:F2} GB";
                if (SizeBytes >= 1_048_576) return $"{SizeBytes / 1_048_576.0:F2} MB";
                if (SizeBytes >= 1_024) return $"{SizeBytes / 1_024.0:F2} KB";
                return $"{SizeBytes} bytes";
            }
        }
        public string FormattedDate => BackupDate.ToString("yyyy-MM-dd HH:mm");
        public string DisplayText => IsOrphaned ? $"[Orphaned] {AddonTitle} ({FormattedDate})" : $"{AddonTitle} ({FormattedDate})";
    }

    public enum ManagerSortMode { NameAsc, NameDesc, DateNewest, DateOldest, SizeLargest, SizeSmallest }

    public partial class MainWindow : Window
    {
        private readonly ObservableCollection<QueueItem> _queue = new();
        private Process? _process;
        private CancellationTokenSource? _cts;
        private static readonly HttpClient _http = new();

        private ObservableCollection<InstalledAddon> _installedAddons = new();
        private InstalledAddon? _selectedAddon;
        private bool _descriptionExpanded;
        private bool _isCheckingUpdates;
        private ManagerSortMode _sortMode = ManagerSortMode.NameAsc;
        private string _activeTag = "All";

        private ObservableCollection<BackupEntry> _backups = new();
        private BackupEntry? _selectedBackup;

        private static readonly string BaseDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string PythonScript = Path.Combine(BaseDir, "Python", "main.py");
        private static readonly string InstalledAddonsFile = Path.Combine(BaseDir, "installed_addons.json");
        private static readonly string OldInstalledFile = Path.Combine(BaseDir, "Python", "installed.json");
        private static readonly string BackupDir = Path.Combine(BaseDir, "Python", "backups");
        private static readonly string QueueFile = Path.Combine(BaseDir, "queue.json");
        private static readonly string PresetsDir = Path.Combine(BaseDir, "presets");
        private static readonly string ConfigPath = Path.Combine(BaseDir, "Python", "config.json");

        public MainWindow()
        {
            InitializeComponent();

            QueueList.ItemsSource = _queue;
            _queue.CollectionChanged += (_, _) =>
                QueueCountLabel.Text = $"{_queue.Count} item{(_queue.Count == 1 ? "" : "s")} in queue";
            QueueList.PreviewMouseRightButtonUp += QueueList_PreviewMouseRightButtonUp;
            QueueList.AllowDrop = true;
            QueueList.DragEnter += QueueList_DragEnter;
            QueueList.Drop += QueueList_Drop;
            QueueList.KeyDown += QueueList_KeyDown;
            WorkshopInput.KeyDown += WorkshopInput_KeyDown;

            BackupListBox.DisplayMemberPath = "DisplayText";
            BackupListBox.ItemsSource = _backups;

            LoadQueue();
            LoadInstalledAddons();

            ManagerListBox.DisplayMemberPath = "Title";
            ManagerListBox.ItemsSource = _installedAddons;
            ManagerListBox.MouseDoubleClick += ManagerListBox_MouseDoubleClick;
            ManagerListBox.PreviewMouseRightButtonUp += ManagerListBox_PreviewMouseRightButtonUp;
            ManagerListBox.KeyDown += ManagerListBox_KeyDown;
            ManagerListBox.SelectionChanged += ManagerListBox_SelectionChanged;

            BuildTagTabs();
            UpdateManagerStats();
            UpdateEmptyStates();
            _installedAddons.CollectionChanged += (_, _) => { UpdateManagerStats(); BuildTagTabs(); UpdateEmptyStates(); };
            _queue.CollectionChanged += (_, _) => UpdateEmptyStates();

            KeyDown += Window_KeyDown;
            Closing += (_, _) => SaveQueue();

            _ = MigrateOldInstalledFile();
            LoadPresetList();
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            if (Keyboard.Modifiers == ModifierKeys.Control)
            {
                switch (e.Key)
                {
                    case Key.D1: case Key.NumPad1: InstallerTab_Click(sender, e); break;
                    case Key.D2: case Key.NumPad2: ManagerTab_Click(sender, e); break;
                    case Key.D3: case Key.NumPad3: BackupsTab_Click(sender, e); break;
                    case Key.D4: case Key.NumPad4: SettingsTab_Click(sender, e); break;
                }
            }
        }

        private void InstallerTab_Click(object sender, RoutedEventArgs e)
        {
            InstallerTab.Visibility = Visibility.Visible;
            ManagerTab.Visibility = Visibility.Collapsed;
            BackupsTab.Visibility = Visibility.Collapsed;
            SettingsTab.Visibility = Visibility.Collapsed;
            SetActiveTab(InstallerTabBtn);
        }

        private void ManagerTab_Click(object sender, RoutedEventArgs e)
        {
            InstallerTab.Visibility = Visibility.Collapsed;
            ManagerTab.Visibility = Visibility.Visible;
            BackupsTab.Visibility = Visibility.Collapsed;
            SettingsTab.Visibility = Visibility.Collapsed;
            SetActiveTab(ManagerTabBtn);
            RefreshManagerList();
            LoadPresetList();
        }

        private void BackupsTab_Click(object sender, RoutedEventArgs e)
        {
            InstallerTab.Visibility = Visibility.Collapsed;
            ManagerTab.Visibility = Visibility.Collapsed;
            BackupsTab.Visibility = Visibility.Visible;
            SettingsTab.Visibility = Visibility.Collapsed;
            SetActiveTab(BackupsTabBtn);
            RefreshBackupList();
        }

        private void SettingsTab_Click(object sender, RoutedEventArgs e)
        {
            InstallerTab.Visibility = Visibility.Collapsed;
            ManagerTab.Visibility = Visibility.Collapsed;
            BackupsTab.Visibility = Visibility.Collapsed;
            SettingsTab.Visibility = Visibility.Visible;
            SetActiveTab(SettingsTabBtn);
            LoadSettings();
        }

        private void SetActiveTab(Button active)
        {
            foreach (var btn in new[] { InstallerTabBtn, ManagerTabBtn, BackupsTabBtn, SettingsTabBtn })
            {
                bool isActive = btn == active;
                btn.Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(isActive ? (byte)0xE0 : (byte)0x88, isActive ? (byte)0xE0 : (byte)0x88, isActive ? (byte)0xE0 : (byte)0x88));
                btn.BorderBrush = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(isActive ? (byte)0x3A : (byte)0x00, isActive ? (byte)0x7B : (byte)0x00, isActive ? (byte)0xD5 : (byte)0x00));
            }
        }

        private void UpdateEmptyStates()
        {
            QueueEmptyText.Visibility = _queue.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ManagerEmptyText.Visibility = _installedAddons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void WorkshopInput_KeyDown(object sender, KeyEventArgs e) { if (e.Key == Key.Enter) AddButton_Click(sender, e); }

        private void QueueList_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && QueueList.SelectedItems.Count > 0)
            {
                var toRemove = QueueList.SelectedItems.Cast<QueueItem>().ToList();
                foreach (var qi in toRemove) _queue.Remove(qi);
                SetStatus($"Removed {toRemove.Count} item{(toRemove.Count == 1 ? "" : "s")}");
            }
        }

        private void ManagerListBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Delete && ManagerListBox.SelectedItems.Count > 0) DeleteSelectedManagerItems();
            else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) { ManagerSearchBox.Focus(); ManagerSearchBox.SelectAll(); e.Handled = true; }
        }

        private void SaveQueue() { try { File.WriteAllText(QueueFile, JsonSerializer.Serialize(_queue.Select(q => q.Id).ToList())); } catch { } }

        private void LoadQueue()
        {
            try { if (File.Exists(QueueFile)) { var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(QueueFile)); if (ids != null) foreach (var id in ids) _ = AddToQueue(id); } } catch { }
        }

        private void QueueList_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var list = sender as ListBox; if (list == null) return;
            var item = list.ContainerFromElement((DependencyObject)e.OriginalSource) as ListBoxItem;
            if (item != null && !item.IsSelected) { list.SelectedItems.Clear(); item.IsSelected = true; }
            if (list.SelectedItems.Count == 0) return;
            var menu = new ContextMenu();
            var count = list.SelectedItems.Count;
            var delete = new MenuItem { Header = count == 1 ? "Remove from queue" : $"Remove {count} items from queue" };
            delete.Click += (_, _) => { var toRemove = list.SelectedItems.Cast<QueueItem>().ToList(); foreach (var qi in toRemove) _queue.Remove(qi); SetStatus($"Removed {toRemove.Count} item{(toRemove.Count == 1 ? "" : "s")}"); };
            menu.Items.Add(delete);
            item!.ContextMenu = menu; menu.IsOpen = true;
        }

        private async void AddButton_Click(object sender, RoutedEventArgs e) { string raw = WorkshopInput.Text.Trim(); if (string.IsNullOrWhiteSpace(raw)) return; await AddToQueue(raw); }

        private async Task AddToQueue(string raw)
        {
            string id = ExtractId(raw);
            if (id == "") { SetStatus("Invalid ID or URL."); return; }
            SetStatus($"Checking {id}…");
            var collectionItems = await FetchCollectionItemsAsync(id);
            if (collectionItems.Count > 0)
            {
                int added = 0;
                foreach (var childId in collectionItems)
                {
                    if (_queue.Any(q => q.Id == childId)) continue;
                    var item = new QueueItem { Id = childId, Display = $"{childId}  (resolving…)" }; _queue.Add(item); added++;
                    var capturedId = childId;
                    _ = Task.Run(async () => { string name = await FetchWorkshopTitleAsync(capturedId); Dispatcher.BeginInvoke(() => { int idx = _queue.IndexOf(item); if (idx >= 0) _queue[idx] = new QueueItem { Id = capturedId, Display = string.IsNullOrEmpty(name) ? capturedId : $"{name}  [{capturedId}]" }; }); });
                }
                WorkshopInput.Clear(); SetStatus($"Added {added} items from collection {id}"); Log($"[COLLECTION] Resolved collection {id} → {added} items"); SaveQueue(); return;
            }
            if (_queue.Any(q => q.Id == id)) { SetStatus($"{id} is already in the queue."); return; }
            var queueItem = new QueueItem { Id = id, Display = $"{id}  (resolving…)" }; _queue.Add(queueItem);
            SetStatus($"Resolving name for {id}…");
            string name = await FetchWorkshopTitleAsync(id);
            int idx = _queue.IndexOf(queueItem);
            if (idx >= 0) _queue[idx] = new QueueItem { Id = id, Display = string.IsNullOrEmpty(name) ? id : $"{name}  [{id}]" };
            WorkshopInput.Clear(); SetStatus(string.IsNullOrEmpty(name) ? $"Added {id} (name not found)" : $"Added: {name}"); SaveQueue();
        }

        private void QueueList_DragEnter(object sender, System.Windows.DragEventArgs e) { e.Effects = (e.Data.GetDataPresent(System.Windows.DataFormats.Text) || e.Data.GetDataPresent(System.Windows.DataFormats.StringFormat) || e.Data.GetDataPresent(System.Windows.DataFormats.UnicodeText)) ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; e.Handled = true; }

        private async void QueueList_Drop(object sender, System.Windows.DragEventArgs e)
        {
            string? text = e.Data.GetDataPresent(System.Windows.DataFormats.Text) ? e.Data.GetData(System.Windows.DataFormats.Text) as string : e.Data.GetDataPresent(System.Windows.DataFormats.StringFormat) ? e.Data.GetData(System.Windows.DataFormats.StringFormat) as string : e.Data.GetDataPresent(System.Windows.DataFormats.UnicodeText) ? e.Data.GetData(System.Windows.DataFormats.UnicodeText) as string : null;
            if (string.IsNullOrWhiteSpace(text)) return;
            var parts = text.Split(new[] { ' ', '\n', '\r', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int added = 0;
            foreach (var part in parts)
            {
                string id = ExtractId(part.Trim()); if (string.IsNullOrEmpty(id)) continue;
                var collectionItems = await FetchCollectionItemsAsync(id);
                if (collectionItems.Count > 0) { foreach (var childId in collectionItems) { if (_queue.Any(q => q.Id == childId)) continue; var item = new QueueItem { Id = childId, Display = $"{childId}  (resolving…)" }; _queue.Add(item); added++; var capturedId = childId; _ = Task.Run(async () => { string name = await FetchWorkshopTitleAsync(capturedId); Dispatcher.BeginInvoke(() => { int idx = _queue.IndexOf(item); if (idx >= 0) _queue[idx] = new QueueItem { Id = capturedId, Display = string.IsNullOrEmpty(name) ? capturedId : $"{name}  [{capturedId}]" }; }); }); } Log($"[COLLECTION] Resolved collection {id} → {collectionItems.Count} items"); }
                else { if (_queue.Any(q => q.Id == id)) continue; var item = new QueueItem { Id = id, Display = $"{id}  (resolving…)" }; _queue.Add(item); added++; var capturedId = id; _ = Task.Run(async () => { string name = await FetchWorkshopTitleAsync(capturedId); Dispatcher.BeginInvoke(() => { int idx = _queue.IndexOf(item); if (idx >= 0) _queue[idx] = new QueueItem { Id = capturedId, Display = string.IsNullOrEmpty(name) ? capturedId : $"{name}  [{capturedId}]" }; }); }); }
            }
            if (added > 0) { WorkshopInput.Clear(); SetStatus($"Added {added} item{(added == 1 ? "" : "s")} via drag & drop"); }
            SaveQueue();
        }

        private void InstallButton_Click(object sender, RoutedEventArgs e)
        {
            if (_queue.Count == 0) { SetStatus("Queue is empty."); return; }
            if (!File.Exists(PythonScript)) { Log($"[ERR] main.py not found"); SetStatus("main.py not found"); return; }
            _cts = new CancellationTokenSource(); SetBusy(true); SetProgress(0, "Starting…"); Log("─── Starting ModEngine ───");
            bool force = ForceReinstallCheckbox.IsChecked == true;
            var ids = new List<string>(); foreach (var item in _queue) ids.Add(force ? $"!{item.Id}" : item.Id);
            int maxWorkers = (int)SettingsMaxWorkersSlider.Value;
            string args = $"--max-workers {maxWorkers} " + string.Join(" ", ids);
            if (force) Log("[!] Force reinstall enabled");
            var psi = new ProcessStartInfo { FileName = "python", Arguments = $"-u \"{PythonScript}\" {args}", RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.OutputDataReceived += (s, ev) => { if (ev.Data == null) return; Dispatcher.BeginInvoke(() => { if (ev.Data.StartsWith("INSTALL_DATA:")) HandleInstallData(ev.Data.Substring("INSTALL_DATA:".Length)); else if (ev.Data.StartsWith("CONFLICT_DATA:")) HandleConflictData(ev.Data.Substring("CONFLICT_DATA:".Length)); else if (ev.Data.StartsWith("BACKUP_DATA:")) HandleBackupData(ev.Data.Substring("BACKUP_DATA:".Length)); else { Log(ev.Data); UpdateProgressFromLog(ev.Data); } }); };
            _process.ErrorDataReceived += (s, ev) => { if (ev.Data != null) Dispatcher.BeginInvoke(() => Log("[ERR] " + ev.Data)); };
            _process.Exited += (s, ev) => { Dispatcher.BeginInvoke(() => { Log("─── Engine finished ───"); SetBusy(false); SetProgress(100, "Done"); SetStatus("Installation complete."); SaveInstalledAddons(); FlashWindow(); if (SettingsAutoClearCheckbox.IsChecked == true) { _queue.Clear(); SaveQueue(); Log("[SETTINGS] Queue auto-cleared"); } }); };
            _process.Start(); _process.BeginOutputReadLine(); _process.BeginErrorReadLine();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e) { try { if (_process != null && !_process.HasExited) { _process.Kill(true); Log("[!] Cancelled"); SetStatus("Cancelled."); } } catch (Exception ex) { Log($"[ERR] Cancel failed: {ex.Message}"); } finally { SetBusy(false); SetProgress(0, "Idle"); SaveInstalledAddons(); } }
        private void ClearQueueButton_Click(object sender, RoutedEventArgs e) { _queue.Clear(); SaveQueue(); SetStatus("Queue cleared."); }
        private void ClearLogButton_Click(object sender, RoutedEventArgs e) { LogBox.Clear(); }
        private void FlashWindow() { if (WindowState == WindowState.Minimized) FlashWindowHelper.Flash(this); }

        private async void HandleInstallData(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
                string id = root.GetProperty("id").GetString() ?? "", path = root.GetProperty("path").GetString() ?? "";
                long sizeBytes = root.TryGetProperty("size_bytes", out var sp) ? sp.GetInt64() : 0;
                var m = await FetchWorkshopDetailsAsync(id);
                if (!string.IsNullOrEmpty(m.title) && Path.GetFileName(path).StartsWith("Workshop_"))
                {
                    string? gameDir = GetGameDir();
                    if (gameDir != null) { string newPath = Path.Combine(gameDir, CleanFolderName(m.title)); if (!Directory.Exists(newPath) && Directory.Exists(path)) { try { Directory.Move(path, newPath); path = newPath; } catch { } } }
                }
                var existing = _installedAddons.FirstOrDefault(a => a.Id == id);
                if (existing != null) { existing.InstallPath = path; existing.SizeBytes = sizeBytes; existing.InstalledDate = DateTime.Now; existing.Title = string.IsNullOrEmpty(m.title) ? existing.Title : m.title; existing.Description = string.IsNullOrEmpty(m.description) ? existing.Description : StripBBCode(m.description); existing.LogoUrl = string.IsNullOrEmpty(m.previewUrl) ? existing.LogoUrl : m.previewUrl; existing.WorkshopTimeUpdated = m.timeUpdated; existing.Tags = m.tags; SaveInstalledAddons(); }
                else { _installedAddons.Add(new InstalledAddon { Id = id, Title = m.title, Description = StripBBCode(m.description), LogoUrl = m.previewUrl, InstallPath = path, SizeBytes = sizeBytes, InstalledDate = DateTime.Now, WorkshopTimeUpdated = m.timeUpdated, Tags = m.tags }); SaveInstalledAddons(); }
            }
            catch (Exception ex) { Log($"[MANAGER] Parse error: {ex.Message}"); }
        }

        private void HandleConflictData(string json) { try { using var doc = JsonDocument.Parse(json); var root = doc.RootElement; string id = root.GetProperty("id").GetString() ?? ""; var conflicts = new List<string>(); if (root.TryGetProperty("conflicts_with", out var arr)) foreach (var i in arr.EnumerateArray()) conflicts.Add(i.GetString() ?? ""); var existing = _installedAddons.FirstOrDefault(a => a.Id == id); if (existing != null) { existing.Conflicts = conflicts; if (conflicts.Count > 0) Log($"[CONFLICT] {existing.Title} vs {string.Join(", ", conflicts)}"); SaveInstalledAddons(); } } catch { } }
        private void HandleBackupData(string json) { try { using var doc = JsonDocument.Parse(json); var root = doc.RootElement; string id = root.GetProperty("id").GetString() ?? "", bp = root.GetProperty("backup_path").GetString() ?? ""; var existing = _installedAddons.FirstOrDefault(a => a.Id == id); if (existing != null) { existing.BackupPath = bp; Log($"[BACKUP] {bp}"); SaveInstalledAddons(); } } catch { } }

        private static async Task<string> FetchWorkshopTitleAsync(string id) { var d = await FetchWorkshopDetailsAsync(id); return d.title; }

        private static async Task<(string title, string description, string previewUrl, long? timeUpdated, List<string> tags)> FetchWorkshopDetailsAsync(string id)
        {
            try
            {
                var c = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("itemcount", "1"), new KeyValuePair<string, string>("publishedfileids[0]", id) });
                var r = await _http.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", c);
                if (!r.IsSuccessStatusCode) return ("", "", "", null, new());
                var j = await r.Content.ReadAsStringAsync(); using var d = JsonDocument.Parse(j);
                var f = d.RootElement.GetProperty("response").GetProperty("publishedfiledetails")[0];
                var tags = new List<string>(); if (f.TryGetProperty("tags", out var arr)) foreach (var t in arr.EnumerateArray()) if (t.TryGetProperty("tag", out var tp)) tags.Add(tp.GetString() ?? "");
                return (f.TryGetProperty("title", out var tip) ? tip.GetString() ?? "" : "", f.TryGetProperty("description", out var dp) ? dp.GetString() ?? "" : "", f.TryGetProperty("preview_url", out var pp) ? pp.GetString() ?? "" : "", f.TryGetProperty("time_updated", out var up) ? up.GetInt64() : null, tags);
            }
            catch { return ("", "", "", null, new()); }
        }

        private static async Task<Dictionary<string, (string, string, string, long?, List<string>)>> FetchWorkshopDetailsBatchAsync(List<string> ids)
        {
            var result = new Dictionary<string, (string, string, string, long?, List<string>)>();
            for (int i = 0; i < ids.Count; i += 50)
            {
                var batch = ids.Skip(i).Take(50).ToList(); var kvps = new List<KeyValuePair<string, string>> { new("itemcount", batch.Count.ToString()) };
                for (int j = 0; j < batch.Count; j++) kvps.Add(new($"publishedfileids[{j}]", batch[j]));
                try
                {
                    var r = await _http.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/", new FormUrlEncodedContent(kvps));
                    if (!r.IsSuccessStatusCode) continue;
                    var j = await r.Content.ReadAsStringAsync(); using var d = JsonDocument.Parse(j);
                    foreach (var f in d.RootElement.GetProperty("response").GetProperty("publishedfiledetails").EnumerateArray())
                    {
                        string fid = f.TryGetProperty("publishedfileid", out var ip) ? ip.GetString() ?? "" : ""; if (string.IsNullOrEmpty(fid)) continue;
                        var tags = new List<string>(); if (f.TryGetProperty("tags", out var arr)) foreach (var t in arr.EnumerateArray()) if (t.TryGetProperty("tag", out var tp)) tags.Add(tp.GetString() ?? "");
                        result[fid] = (f.TryGetProperty("title", out var tip) ? tip.GetString() ?? "" : "", f.TryGetProperty("description", out var dp) ? dp.GetString() ?? "" : "", f.TryGetProperty("preview_url", out var pp) ? pp.GetString() ?? "" : "", f.TryGetProperty("time_updated", out var up) ? up.GetInt64() : null, tags);
                    }
                }
                catch { }
            }
            return result;
        }

        private static async Task<List<string>> FetchCollectionItemsAsync(string cid)
        {
            var items = new List<string>();
            try
            {
                var c = new FormUrlEncodedContent(new[] { new KeyValuePair<string, string>("collectioncount", "1"), new KeyValuePair<string, string>("publishedfileids[0]", cid) });
                var r = await _http.PostAsync("https://api.steampowered.com/ISteamRemoteStorage/GetCollectionDetails/v1/", c);
                if (!r.IsSuccessStatusCode) return items;
                var j = await r.Content.ReadAsStringAsync(); using var d = JsonDocument.Parse(j);
                var cd = d.RootElement.GetProperty("response").GetProperty("collectiondetails")[0];
                if (cd.TryGetProperty("children", out var children)) foreach (var child in children.EnumerateArray()) if (child.TryGetProperty("publishedfileid", out var ip)) { var cid2 = ip.GetString() ?? ""; if (!string.IsNullOrEmpty(cid2)) items.Add(cid2); }
            }
            catch { }
            return items;
        }

        private static string ExtractId(string raw) { var m = Regex.Match(raw, @"[?&]id=(\d+)"); if (m.Success) return m.Groups[1].Value; if (Regex.IsMatch(raw, @"^\d+$")) return raw; return ""; }

        private static string StripBBCode(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            text = Regex.Replace(text, @"\[img\].*?\[/img\]", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            text = Regex.Replace(text, @"\[url=.*?\]", "", RegexOptions.IgnoreCase); text = Regex.Replace(text, @"\[/url\]", "", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"\[/?\w+\]", "", RegexOptions.IgnoreCase); text = text.Replace("[*]", "• ");
            text = Regex.Replace(text, @"\\r\\n", "\n"); text = Regex.Replace(text, @"\\r", "\n"); text = Regex.Replace(text, @"\\n", "\n");
            text = Regex.Replace(text, @"\r\n", "\n"); text = Regex.Replace(text, @"\n{3,}", "\n\n"); return text.Trim();
        }

        private void LoadInstalledAddons()
        {
            try { if (File.Exists(InstalledAddonsFile)) { string json = File.ReadAllText(InstalledAddonsFile); if (string.IsNullOrWhiteSpace(json)) return; var list = JsonSerializer.Deserialize<List<InstalledAddon>>(json); if (list != null) _installedAddons = new ObservableCollection<InstalledAddon>(list); } }
            catch (Exception ex) { Log($"[MANAGER] Load failed: {ex.Message}"); try { if (File.Exists(InstalledAddonsFile)) File.Move(InstalledAddonsFile, InstalledAddonsFile + ".bak"); } catch { } }
        }

        private void SaveInstalledAddons() { try { File.WriteAllText(InstalledAddonsFile, JsonSerializer.Serialize(_installedAddons.ToList(), new JsonSerializerOptions { WriteIndented = true })); } catch (Exception ex) { Log($"[MANAGER] Save failed: {ex.Message}"); } }

        private async Task MigrateOldInstalledFile()
        {
            try
            {
                if (!File.Exists(OldInstalledFile) || _installedAddons.Count > 0) return;
                var oldIds = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(OldInstalledFile)); if (oldIds == null || oldIds.Count == 0) return;
                foreach (var id in oldIds) { if (_installedAddons.Any(a => a.Id == id)) continue; var m = await FetchWorkshopDetailsAsync(id); string? ip = FindAddonFolder(id, m.title); _installedAddons.Add(new InstalledAddon { Id = id, Title = m.title, Description = StripBBCode(m.description), LogoUrl = m.previewUrl, InstallPath = ip ?? "(not found)", SizeBytes = ip != null ? GetDirectorySize(ip) : 0, InstalledDate = ip != null ? Directory.GetCreationTime(ip) : DateTime.MinValue, WorkshopTimeUpdated = m.timeUpdated, Tags = m.tags }); }
                SaveInstalledAddons();
            }
            catch (Exception ex) { Log($"[MANAGER] Migration failed: {ex.Message}"); }
        }

        private void RefreshManagerList() { if (ManagerListBox == null) return; ManagerListBox.ItemsSource = null; var filtered = FilterByTag(_installedAddons, _activeTag); var sorted = SortAddons(filtered, _sortMode); ManagerListBox.ItemsSource = sorted; UpdateEmptyStates(); }

        private void UpdateManagerStats()
        {
            long totalSize = _installedAddons.Sum(a => a.SizeBytes); string sizeStr;
            if (totalSize >= 1_073_741_824) sizeStr = $"{totalSize / 1_073_741_824.0:F2} GB"; else if (totalSize >= 1_048_576) sizeStr = $"{totalSize / 1_048_576.0:F2} MB"; else if (totalSize >= 1_024) sizeStr = $"{totalSize / 1_024.0:F2} KB"; else sizeStr = $"{totalSize} bytes";
            ManagerStatsLabel.Text = $"{_installedAddons.Count} addon{(_installedAddons.Count == 1 ? "" : "s")} — {sizeStr}";
        }

        private ObservableCollection<InstalledAddon> SortAddons(IEnumerable<InstalledAddon> source, ManagerSortMode mode) { var sorted = mode switch { ManagerSortMode.NameAsc => source.OrderBy(a => a.Title.ToLowerInvariant()), ManagerSortMode.NameDesc => source.OrderByDescending(a => a.Title.ToLowerInvariant()), ManagerSortMode.DateNewest => source.OrderByDescending(a => a.InstalledDate), ManagerSortMode.DateOldest => source.OrderBy(a => a.InstalledDate), ManagerSortMode.SizeLargest => source.OrderByDescending(a => a.SizeBytes), ManagerSortMode.SizeSmallest => source.OrderBy(a => a.SizeBytes), _ => source.OrderBy(a => a.Title.ToLowerInvariant()) }; return new ObservableCollection<InstalledAddon>(sorted); }
        private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { _sortMode = SortComboBox.SelectedIndex switch { 1 => ManagerSortMode.NameDesc, 2 => ManagerSortMode.DateNewest, 3 => ManagerSortMode.DateOldest, 4 => ManagerSortMode.SizeLargest, 5 => ManagerSortMode.SizeSmallest, _ => ManagerSortMode.NameAsc }; RefreshManagerList(); }

        private void BuildTagTabs()
        {
            TagTabsPanel.Children.Clear(); var allTags = new HashSet<string>(); foreach (var a in _installedAddons) foreach (var t in a.Tags) allTags.Add(t);
            var sortedTags = allTags.OrderBy(t => t).ToList(); var allTagsList = new List<string> { "All" }; allTagsList.AddRange(sortedTags);
            foreach (var tag in allTagsList)
            {
                string displayTag = tag == "All" ? "All" : System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(tag.ToLower());
                var btn = new Button { Content = displayTag, FontSize = 10, Height = 22, MinWidth = 40, Margin = new Thickness(0, 0, 3, 3), Padding = new Thickness(7, 0, 7, 0), Tag = tag };
                if (tag == _activeTag) { btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x7B, 0xD5)); btn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Colors.White); btn.FontWeight = FontWeights.SemiBold; }
                else { btn.Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x2D, 0x2D, 0x2D)); btn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x88, 0x88, 0x88)); }
                btn.Click += TagFilterButton_Click; TagTabsPanel.Children.Add(btn);
            }
        }

        private void TagFilterButton_Click(object sender, RoutedEventArgs e) { if (sender is Button btn && btn.Tag is string tag) { _activeTag = tag; BuildTagTabs(); RefreshManagerList(); } }
        private IEnumerable<InstalledAddon> FilterByTag(IEnumerable<InstalledAddon> source, string tag) => tag == "All" ? source : source.Where(a => a.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase));

        private void ManagerSearchBox_TextChanged(object sender, TextChangedEventArgs e) { string q = ManagerSearchBox.Text.Trim().ToLowerInvariant(); var baseList = FilterByTag(_installedAddons, _activeTag); var source = string.IsNullOrEmpty(q) ? baseList : new ObservableCollection<InstalledAddon>(baseList.Where(a => a.Title.ToLowerInvariant().Contains(q) || a.Id.Contains(q))); ManagerListBox.ItemsSource = SortAddons(source, _sortMode); }

        private async void ScanForAddonsButton_Click(object sender, RoutedEventArgs e)
        {
            ScanForAddonsButton.IsEnabled = false; ScanForAddonsButton.Content = "Scanning…";
            try
            {
                string? gd = GetGameDir(); if (gd == null) return; int added = 0, skipped = 0;
                foreach (var folder in Directory.GetDirectories(gd)) { string fn = Path.GetFileName(folder); if (_installedAddons.Any(a => string.Equals(a.InstallPath, folder, StringComparison.OrdinalIgnoreCase))) { skipped++; continue; } string? wsid = TryExtractWorkshopId(folder); if (!string.IsNullOrEmpty(wsid)) { if (_installedAddons.Any(a => a.Id == wsid)) { skipped++; continue; } var m = await FetchWorkshopDetailsAsync(wsid); _installedAddons.Add(new InstalledAddon { Id = wsid, Title = string.IsNullOrEmpty(m.title) ? fn : m.title, Description = StripBBCode(m.description), LogoUrl = m.previewUrl, InstallPath = folder, SizeBytes = GetDirectorySize(folder), InstalledDate = Directory.GetCreationTime(folder), WorkshopTimeUpdated = m.timeUpdated, Tags = m.tags }); added++; } else { _installedAddons.Add(new InstalledAddon { Id = $"local:{fn}", Title = fn.Replace("_", " "), Description = "Local addon", InstallPath = folder, SizeBytes = GetDirectorySize(folder), InstalledDate = Directory.GetCreationTime(folder) }); added++; } }
                SaveInstalledAddons(); RefreshManagerList(); SetStatus($"Scan done: {added} added, {skipped} skipped");
            }
            catch (Exception ex) { Log($"[MANAGER] Scan failed: {ex.Message}"); }
            finally { ScanForAddonsButton.IsEnabled = true; ScanForAddonsButton.Content = "Scan"; }
        }

        private async void CheckForUpdatesButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isCheckingUpdates) return; _isCheckingUpdates = true; CheckForUpdatesButton.IsEnabled = false; CheckForUpdatesButton.Content = "Checking…";
            try { var ids = _installedAddons.Where(a => !a.Id.StartsWith("local:")).Select(a => a.Id).ToList(); if (ids.Count == 0) return; var results = await FetchWorkshopDetailsBatchAsync(ids); int found = 0; foreach (var a in _installedAddons) if (results.TryGetValue(a.Id, out var d)) { a.WorkshopTimeUpdated = d.Item4; a.Tags = d.Item5; if (a.UpdateAvailable) found++; } SaveInstalledAddons(); RefreshManagerList(); SetStatus(found > 0 ? $"{found} update(s) available" : "All up to date"); }
            catch (Exception ex) { Log($"[UPDATE] Failed: {ex.Message}"); }
            finally { _isCheckingUpdates = false; CheckForUpdatesButton.IsEnabled = true; CheckForUpdatesButton.Content = "Updates"; }
        }

        private void UpdateAllButton_Click(object sender, RoutedEventArgs e) { var outdated = _installedAddons.Where(a => a.UpdateAvailable && !a.Id.StartsWith("local:")).ToList(); if (outdated.Count == 0) { SetStatus("No updates to install."); return; } int added = 0; foreach (var a in outdated) { if (!_queue.Any(q => q.Id == a.Id)) { _queue.Add(new QueueItem { Id = a.Id, Display = $"{a.Title}  [{a.Id}]" }); added++; } } SaveQueue(); SetStatus($"Queued {added} for update"); InstallerTab_Click(sender, e); }

        private void ManagerListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e) { var item = (sender as ListBox)?.SelectedItem as InstalledAddon; if (item == null || item.Id.StartsWith("local:")) return; if (!_queue.Any(q => q.Id == item.Id)) { _queue.Add(new QueueItem { Id = item.Id, Display = $"{item.Title}  [{item.Id}]" }); SaveQueue(); } SetStatus($"Queued: {item.Title}"); InstallerTab_Click(sender, e); }

        private void ManagerListBox_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            var list = sender as ListBox; if (list == null) return;
            var item = list.ContainerFromElement((DependencyObject)e.OriginalSource) as ListBoxItem; if (item != null && !item.IsSelected) { list.SelectedItems.Clear(); item.IsSelected = true; }
            if (list.SelectedItems.Count == 0) return; var menu = new ContextMenu(); int count = list.SelectedItems.Count;
            var reinstall = new MenuItem { Header = count == 1 ? "Reinstall" : $"Reinstall {count} items" }; reinstall.Click += (_, _) => { foreach (InstalledAddon a in list.SelectedItems) { if (!a.Id.StartsWith("local:") && !_queue.Any(q => q.Id == a.Id)) _queue.Add(new QueueItem { Id = a.Id, Display = $"{a.Title}  [{a.Id}]" }); } SaveQueue(); SetStatus($"Queued for reinstall"); InstallerTab_Click(sender, e); }; menu.Items.Add(reinstall);
            var copyUrl = new MenuItem { Header = count == 1 ? "Copy Workshop URL" : "Copy Workshop URLs" }; copyUrl.Click += (_, _) => { var urls = new List<string>(); foreach (InstalledAddon a in list.SelectedItems) if (!a.Id.StartsWith("local:")) urls.Add($"https://steamcommunity.com/sharedfiles/filedetails/?id={a.Id}"); if (urls.Count > 0) System.Windows.Clipboard.SetText(string.Join("\n", urls)); }; menu.Items.Add(copyUrl); menu.Items.Add(new Separator());
            var deleteFromDisk = new MenuItem { Header = count == 1 ? "Delete from disk" : $"Delete {count} items from disk" }; deleteFromDisk.Click += (_, _) => { var selected = list.SelectedItems.Cast<InstalledAddon>().ToList(); if (System.Windows.MessageBox.Show($"Delete {selected.Count} addon(s) from disk?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; foreach (var a in selected) DeleteAddonInternal(a); SaveInstalledAddons(); RefreshManagerList(); }; menu.Items.Add(deleteFromDisk);
            var removeFromList = new MenuItem { Header = count == 1 ? "Remove from list" : $"Remove {count} items from list" }; removeFromList.Click += (_, _) => { var selected = list.SelectedItems.Cast<InstalledAddon>().ToList(); foreach (var a in selected) _installedAddons.Remove(a); SaveInstalledAddons(); RefreshManagerList(); }; menu.Items.Add(removeFromList);
            item!.ContextMenu = menu; menu.IsOpen = true;
        }

        private void DeleteSelectedManagerItems() { var selected = ManagerListBox.SelectedItems.Cast<InstalledAddon>().ToList(); if (selected.Count == 0) return; if (System.Windows.MessageBox.Show($"Delete {selected.Count} addon(s) from disk?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; foreach (var a in selected) DeleteAddonInternal(a); SaveInstalledAddons(); RefreshManagerList(); }

        private void DeleteAddonInternal(InstalledAddon a) { if (!string.IsNullOrEmpty(a.InstallPath) && Directory.Exists(a.InstallPath)) try { Directory.Delete(a.InstallPath, true); } catch { } if (a.HasBackup && !string.IsNullOrEmpty(a.BackupPath) && Directory.Exists(a.BackupPath)) try { Directory.Delete(a.BackupPath, true); } catch { } _installedAddons.Remove(a); RefreshManagerList(); }

        private void DeleteAllButton_Click(object sender, RoutedEventArgs e) { if (_installedAddons.Count == 0) return; if (System.Windows.MessageBox.Show($"Delete ALL {_installedAddons.Count} addons from disk?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; foreach (var a in _installedAddons.ToList()) DeleteAddonInternal(a); SaveInstalledAddons(); RefreshManagerList(); }
        private void ClearListButton_Click(object sender, RoutedEventArgs e) { if (_installedAddons.Count == 0) return; if (System.Windows.MessageBox.Show($"Remove all {_installedAddons.Count} from list? Folders stay.", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return; _installedAddons.Clear(); SaveInstalledAddons(); RefreshManagerList(); }

        private void ManagerListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded || ManagerListBox == null) return;
            _selectedAddon = ManagerListBox.SelectedItem as InstalledAddon;
            if (_selectedAddon == null) { NoSelectionText.Visibility = Visibility.Visible; DetailContent.Visibility = Visibility.Collapsed; return; }
            NoSelectionText.Visibility = Visibility.Collapsed; DetailContent.Visibility = Visibility.Visible;
            DetailTitle.Text = _selectedAddon.Title; DetailId.Text = _selectedAddon.Id.StartsWith("local:") ? "Local addon" : $"Workshop ID: {_selectedAddon.Id}";
            DetailSize.Text = $"Size: {_selectedAddon.FormattedSize}"; DetailDate.Text = _selectedAddon.InstalledDate == DateTime.MinValue ? "Installed: Unknown" : $"Installed: {_selectedAddon.InstalledDate:yyyy-MM-dd HH:mm}"; DetailPath.Text = _selectedAddon.InstallPath;
            bool isLocal = _selectedAddon.Id.StartsWith("local:"); ViewOnWorkshopLink.Visibility = isLocal ? Visibility.Collapsed : Visibility.Visible;
            UpdateAvailableWarning.Visibility = (!isLocal && _selectedAddon.UpdateAvailable) ? Visibility.Visible : Visibility.Collapsed;
            ConflictWarning.Visibility = _selectedAddon.HasConflicts ? Visibility.Visible : Visibility.Collapsed; if (_selectedAddon.HasConflicts) ConflictList.Text = "Conflicts with: " + string.Join(", ", _selectedAddon.Conflicts);
            RestoreBackupButton.Visibility = _selectedAddon.HasBackup ? Visibility.Visible : Visibility.Collapsed;
            _descriptionExpanded = false; DetailDescription.Text = TruncateDescription(_selectedAddon.Description, 150); DetailDescription.MaxHeight = 60;
            ExpandDescriptionBtn.Text = string.IsNullOrEmpty(_selectedAddon.Description) || _selectedAddon.Description.Length <= 150 ? "" : "Show full description";
            if (!string.IsNullOrEmpty(_selectedAddon.LogoUrl)) { try { DetailLogo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri(_selectedAddon.LogoUrl)); } catch { DetailLogo.Source = null; } } else DetailLogo.Source = null;
            DetailLogo.Visibility = string.IsNullOrEmpty(_selectedAddon.LogoUrl) ? Visibility.Collapsed : Visibility.Visible;
        }

        private static string TruncateDescription(string d, int m) => string.IsNullOrEmpty(d) ? "No description." : d.Length <= m ? d : d.Substring(0, m).TrimEnd() + "…";
        private void ExpandDescription_Click(object sender, MouseButtonEventArgs e) { if (_selectedAddon == null) return; _descriptionExpanded = !_descriptionExpanded; if (_descriptionExpanded) { DetailDescription.Text = _selectedAddon.Description; DetailDescription.MaxHeight = double.PositiveInfinity; ExpandDescriptionBtn.Text = "Collapse"; } else { DetailDescription.Text = TruncateDescription(_selectedAddon.Description, 150); DetailDescription.MaxHeight = 60; ExpandDescriptionBtn.Text = "Show full description"; } }
        private void ViewOnWorkshop_Click(object sender, MouseButtonEventArgs e) { if (_selectedAddon != null && !_selectedAddon.Id.StartsWith("local:")) try { Process.Start(new ProcessStartInfo { FileName = $"https://steamcommunity.com/sharedfiles/filedetails/?id={_selectedAddon.Id}", UseShellExecute = true }); } catch { } }
        private void OpenFolderButton_Click(object sender, RoutedEventArgs e) { if (_selectedAddon == null) return; if (Directory.Exists(_selectedAddon.InstallPath)) Process.Start("explorer.exe", _selectedAddon.InstallPath); else System.Windows.MessageBox.Show("Folder not found.", "Error", MessageBoxButton.OK, MessageBoxImage.Warning); }

        private void DeleteAddonButton_Click(object sender, RoutedEventArgs e) { if (_selectedAddon == null) return; var a = _selectedAddon; if (System.Windows.MessageBox.Show($"Delete \"{a.Title}\"?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; DeleteAddonInternal(a); SaveInstalledAddons(); RefreshManagerList(); }
        private void RemoveFromListButton_Click(object sender, RoutedEventArgs e) { if (_selectedAddon == null) return; var a = _selectedAddon; if (System.Windows.MessageBox.Show($"Remove \"{a.Title}\" from list?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return; _installedAddons.Remove(a); SaveInstalledAddons(); RefreshManagerList(); }

        private void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
        {
            if (_selectedAddon == null || !_selectedAddon.HasBackup) return; var a = _selectedAddon;
            if (!Directory.Exists(a.BackupPath)) { a.BackupPath = null; SaveInstalledAddons(); return; }
            if (System.Windows.MessageBox.Show($"Restore backup of \"{a.Title}\"?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            try { if (Directory.Exists(a.InstallPath)) Directory.Delete(a.InstallPath, true); if (string.Equals(Path.GetPathRoot(a.BackupPath) ?? "", Path.GetPathRoot(a.InstallPath) ?? "", StringComparison.OrdinalIgnoreCase)) Directory.Move(a.BackupPath, a.InstallPath); else { CopyDirectory(a.BackupPath, a.InstallPath); Directory.Delete(a.BackupPath, true); } a.SizeBytes = GetDirectorySize(a.InstallPath); a.BackupPath = null; a.InstalledDate = DateTime.Now; SaveInstalledAddons(); ManagerListBox_SelectionChanged(null!, null!); }
            catch (Exception ex) { System.Windows.MessageBox.Show($"Failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error); }
        }

        private static void CopyDirectory(string src, string dst) { Directory.CreateDirectory(dst); foreach (string f in Directory.GetFiles(src)) File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true); foreach (string d in Directory.GetDirectories(src)) CopyDirectory(d, Path.Combine(dst, Path.GetFileName(d))); }

        private void ExportButton_Click(object sender, RoutedEventArgs e) { var dlg = new Microsoft.Win32.SaveFileDialog { Filter = "JSON|*.json", FileName = "modengine_addons.json" }; if (dlg.ShowDialog() == true) try { File.WriteAllText(dlg.FileName, JsonSerializer.Serialize(_installedAddons.Select(a => new { a.Id, a.Title, a.InstallPath }), new JsonSerializerOptions { WriteIndented = true })); } catch { } }

        private async void ImportButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json" }; if (dlg.ShowDialog() != true) return;
            try { var ids = new List<string>(); using var doc = JsonDocument.Parse(File.ReadAllText(dlg.FileName)); if (doc.RootElement.ValueKind == JsonValueKind.Array) foreach (var item in doc.RootElement.EnumerateArray()) if (item.TryGetProperty("id", out var ip)) ids.Add(ip.GetString() ?? ""); InstallerTab_Click(sender, e); foreach (var id in ids) if (!_queue.Any(q => q.Id == id)) await AddToQueue(id); } catch { }
        }

        private async void ManualAddButton_Click(object sender, RoutedEventArgs e)
        {
            string raw = WorkshopInput.Text.Trim(); if (string.IsNullOrWhiteSpace(raw)) raw = Microsoft.VisualBasic.Interaction.InputBox("Workshop ID:", "Add to Manager", "");
            string id = ExtractId(raw); if (string.IsNullOrEmpty(id)) return; if (_installedAddons.Any(a => a.Id == id)) { SetStatus("Already in list."); return; }
            var m = await FetchWorkshopDetailsAsync(id); if (string.IsNullOrEmpty(m.title)) return;
            string? ip = FindAddonFolder(id, m.title);
            _installedAddons.Add(new InstalledAddon { Id = id, Title = m.title, Description = StripBBCode(m.description), LogoUrl = m.previewUrl, InstallPath = ip ?? "(not found)", SizeBytes = ip != null ? GetDirectorySize(ip) : 0, InstalledDate = ip != null ? Directory.GetCreationTime(ip) : DateTime.Now, WorkshopTimeUpdated = m.timeUpdated, Tags = m.tags });
            SaveInstalledAddons(); RefreshManagerList();
        }

        private void LoadPresetList() { PresetComboBox.Items.Clear(); if (!Directory.Exists(PresetsDir)) return; foreach (var file in Directory.GetFiles(PresetsDir, "*.json")) PresetComboBox.Items.Add(Path.GetFileNameWithoutExtension(file)); if (PresetComboBox.Items.Count > 0) PresetComboBox.SelectedIndex = 0; }

        private void SavePresetButton_Click(object sender, RoutedEventArgs e) { string name = Microsoft.VisualBasic.Interaction.InputBox("Preset name:", "Save Preset", ""); if (string.IsNullOrWhiteSpace(name)) return; Directory.CreateDirectory(PresetsDir); var ids = _installedAddons.Where(a => !a.Id.StartsWith("local:")).Select(a => a.Id).ToList(); File.WriteAllText(Path.Combine(PresetsDir, $"{CleanFileName(name)}.json"), JsonSerializer.Serialize(ids, new JsonSerializerOptions { WriteIndented = true })); LoadPresetList(); }

        private async void LoadPresetButton_Click(object sender, RoutedEventArgs e)
        {
            string? name = PresetComboBox.SelectedItem?.ToString(); if (string.IsNullOrEmpty(name)) return; var presetPath = Path.Combine(PresetsDir, $"{name}.json"); if (!File.Exists(presetPath)) return;
            var ids = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(presetPath)); if (ids == null || ids.Count == 0) return;
            if (System.Windows.MessageBox.Show($"Load preset \"{name}\"?\n\nThis will DELETE all current addons and install {ids.Count} preset addons.", "Load Preset", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
            foreach (var a in _installedAddons.ToList()) DeleteAddonInternal(a); SaveInstalledAddons(); RefreshManagerList(); _queue.Clear();
            foreach (var id in ids) if (!_queue.Any(q => q.Id == id)) _queue.Add(new QueueItem { Id = id, Display = $"{id}  (resolving…)" }); SaveQueue(); InstallerTab_Click(sender, e);
        }

        private void DeletePresetButton_Click(object sender, RoutedEventArgs e) { string? name = PresetComboBox.SelectedItem?.ToString(); if (string.IsNullOrEmpty(name)) return; var path = Path.Combine(PresetsDir, $"{name}.json"); if (File.Exists(path)) File.Delete(path); LoadPresetList(); }
        private static string CleanFileName(string n) { foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_'); return n; }

        private void RefreshBackupList()
        {
            _backups.Clear(); if (!Directory.Exists(BackupDir)) return;
            foreach (var dir in Directory.GetDirectories(BackupDir)) { string fn = Path.GetFileName(dir); var match = Regex.Match(fn, @"^(.+)_(\d{4}-\d{2}-\d{2}_\d{6})$"); string addonName = match.Success ? match.Groups[1].Value.Replace("_", " ") : fn; DateTime date = match.Success && DateTime.TryParseExact(match.Groups[2].Value, "yyyy-MM-dd_HHmmss", null, System.Globalization.DateTimeStyles.None, out var p) ? p : Directory.GetCreationTime(dir); var addon = _installedAddons.FirstOrDefault(a => a.HasBackup && string.Equals(a.BackupPath, dir, StringComparison.OrdinalIgnoreCase)); _backups.Add(new BackupEntry { BackupPath = dir, AddonTitle = addon?.Title ?? addonName, AddonId = addon?.Id ?? "", BackupDate = date, SizeBytes = GetDirectorySize(dir), IsOrphaned = addon == null }); }
        }

        private void BackupListBox_SelectionChanged(object sender, SelectionChangedEventArgs e) { _selectedBackup = BackupListBox.SelectedItem as BackupEntry; if (_selectedBackup == null) { BackupNoSelection.Visibility = Visibility.Visible; BackupDetail.Visibility = Visibility.Collapsed; return; } BackupNoSelection.Visibility = Visibility.Collapsed; BackupDetail.Visibility = Visibility.Visible; BackupDetailTitle.Text = _selectedBackup.AddonTitle; BackupDetailId.Text = string.IsNullOrEmpty(_selectedBackup.AddonId) ? "Orphaned" : $"Workshop ID: {_selectedBackup.AddonId}"; BackupDetailDate.Text = $"Backup: {_selectedBackup.FormattedDate}"; BackupDetailSize.Text = $"Size: {_selectedBackup.FormattedSize}"; BackupDetailPath.Text = _selectedBackup.BackupPath; RestoreBackupFromTabBtn.Visibility = _selectedBackup.IsOrphaned ? Visibility.Collapsed : Visibility.Visible; }
        private void RestoreBackupFromTab_Click(object sender, RoutedEventArgs e) { if (_selectedBackup == null || _selectedBackup.IsOrphaned) return; var addon = _installedAddons.FirstOrDefault(a => a.Id == _selectedBackup.AddonId); if (addon == null) return; _selectedAddon = addon; RestoreBackupButton_Click(sender, e); RefreshBackupList(); }
        private void DeleteBackupButton_Click(object sender, RoutedEventArgs e) { if (_selectedBackup == null) return; if (System.Windows.MessageBox.Show("Delete backup?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; try { if (Directory.Exists(_selectedBackup.BackupPath)) Directory.Delete(_selectedBackup.BackupPath, true); var a = _installedAddons.FirstOrDefault(x => x.BackupPath == _selectedBackup.BackupPath); if (a != null) { a.BackupPath = null; SaveInstalledAddons(); } } catch { } RefreshBackupList(); }
        private void DeleteAllBackupsButton_Click(object sender, RoutedEventArgs e) { if (_backups.Count == 0) return; if (System.Windows.MessageBox.Show($"Delete ALL {_backups.Count} backups?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return; foreach (var b in _backups.ToList()) try { if (Directory.Exists(b.BackupPath)) Directory.Delete(b.BackupPath, true); } catch { } foreach (var a in _installedAddons) a.BackupPath = null; SaveInstalledAddons(); RefreshBackupList(); }
        private void OpenBackupFolderButton_Click(object sender, RoutedEventArgs e) { if (_selectedBackup != null && Directory.Exists(_selectedBackup.BackupPath)) Process.Start("explorer.exe", _selectedBackup.BackupPath); }

        private void LoadSettings()
        {
            try
            {
                var config = new Dictionary<string, object>(); if (File.Exists(ConfigPath)) { string json = File.ReadAllText(ConfigPath); config = JsonSerializer.Deserialize<Dictionary<string, object>>(json) ?? new(); }
                SettingsGameDirBox.Text = config.TryGetValue("game_dir", out var gd) ? gd?.ToString() ?? "" : "";
                string steamCmdDefault = Path.Combine(BaseDir, "Python", "Tools", "steamcmd", "steamcmd.exe"); SettingsSteamCmdBox.Text = config.TryGetValue("steamcmd_path", out var sp) ? sp?.ToString() ?? steamCmdDefault : steamCmdDefault;
                int maxWorkers = 3; if (config.TryGetValue("max_workers", out var mw) && int.TryParse(mw?.ToString(), out var parsed)) maxWorkers = Math.Clamp(parsed, 1, 5); SettingsMaxWorkersSlider.Value = maxWorkers; SettingsMaxWorkersLabel.Text = maxWorkers.ToString();
                bool autoClear = false; if (config.TryGetValue("auto_clear_queue", out var ac) && bool.TryParse(ac?.ToString(), out var acParsed)) autoClear = acParsed; SettingsAutoClearCheckbox.IsChecked = autoClear;
            }
            catch (Exception ex) { Log($"[SETTINGS] Load failed: {ex.Message}"); }
        }

        private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var config = new Dictionary<string, object>(); if (File.Exists(ConfigPath)) { string existing = File.ReadAllText(ConfigPath); config = JsonSerializer.Deserialize<Dictionary<string, object>>(existing) ?? new(); }
                config["game_dir"] = SettingsGameDirBox.Text.Trim(); config["steamcmd_path"] = SettingsSteamCmdBox.Text.Trim(); config["max_workers"] = (int)SettingsMaxWorkersSlider.Value; config["auto_clear_queue"] = SettingsAutoClearCheckbox.IsChecked == true;
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true })); SetStatus("Settings saved");
            }
            catch (Exception ex) { Log($"[SETTINGS] Save failed: {ex.Message}"); SetStatus("Failed to save settings"); }
        }

        private void BrowseGameDirButton_Click(object sender, RoutedEventArgs e)
        {
            string currentPath = SettingsGameDirBox.Text.Trim();
            if (!string.IsNullOrEmpty(currentPath) && Directory.Exists(currentPath)) Process.Start("explorer.exe", currentPath);
            else { string defaultGuess = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) ?? @"C:\Program Files (x86)", @"Steam\steamapps\common\GarrysMod\garrysmod\addons"); if (Directory.Exists(defaultGuess)) Process.Start("explorer.exe", defaultGuess); else Process.Start("explorer.exe", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) ?? @"C:\"); }
            SetStatus("Copy the folder path from Explorer and paste it above.");
        }

        private void BrowseSteamCmdButton_Click(object sender, RoutedEventArgs e) { var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "steamcmd.exe|steamcmd.exe", Title = "Locate steamcmd.exe" }; if (dialog.ShowDialog() == true) SettingsSteamCmdBox.Text = dialog.FileName; }
        private void SettingsMaxWorkersSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) { if (SettingsMaxWorkersLabel != null) SettingsMaxWorkersLabel.Text = ((int)SettingsMaxWorkersSlider.Value).ToString(); }

        private void CreditsButton_Click(object sender, RoutedEventArgs e) { System.Windows.MessageBox.Show("ModEngine — Garry's Mod Workshop Installer\n\nCreated by Arishi\n\nSteamCMD by Valve\nGMAD by Garry's Mod\n\nBuilt with C# WPF + Python", "Credits", MessageBoxButton.OK, MessageBoxImage.Information); }

        private string? GetGameDir() { try { string cp = Path.Combine(BaseDir, "Python", "config.json"); if (File.Exists(cp)) { using var d = JsonDocument.Parse(File.ReadAllText(cp)); if (d.RootElement.TryGetProperty("game_dir", out var p)) return p.GetString(); } } catch { } return null; }
        private static string? TryExtractWorkshopId(string fp) { string aj = Path.Combine(fp, "addon.json"); if (File.Exists(aj)) try { using var d = JsonDocument.Parse(File.ReadAllText(aj)); foreach (var k in new[] { "wsid", "workshop_id", "workshopid", "id", "publishedfileid" }) if (d.RootElement.TryGetProperty(k, out var p)) { var v = p.GetString(); if (!string.IsNullOrEmpty(v) && long.TryParse(v, out _)) return v; } } catch { } foreach (var f in Directory.GetFiles(fp, "*.gma")) if (long.TryParse(Path.GetFileNameWithoutExtension(f), out _)) return Path.GetFileNameWithoutExtension(f); string wf = Path.Combine(fp, ".wsid"); if (File.Exists(wf)) try { var c = File.ReadAllText(wf).Trim(); if (long.TryParse(c, out _)) return c; } catch { } return null; }
        private string? FindAddonFolder(string id, string title) { string? gd = GetGameDir(); if (gd == null) return null; foreach (var d in Directory.GetDirectories(gd)) if (Path.GetFileName(d) == id || Path.GetFileName(d).Contains(id)) return d; string ct = CleanFolderName(title); foreach (var d in Directory.GetDirectories(gd)) if (Path.GetFileName(d).Equals(ct, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(d).Contains(ct, StringComparison.OrdinalIgnoreCase)) return d; return null; }
        private static string CleanFolderName(string n) { foreach (var c in new[] { '<', '>', ':', '"', '/', '\\', '|', '?', '*' }) n = n.Replace(c.ToString(), ""); return n.Trim().Replace(" ", "_"); }
        private static long GetDirectorySize(string p) { long t = 0; try { foreach (var f in Directory.GetFiles(p, "*", SearchOption.AllDirectories)) try { t += new FileInfo(f).Length; } catch { } } catch { } return t; }

        private void Log(string t) { LogBox.AppendText(t + "\n"); LogBox.ScrollToEnd(); }
        private void SetStatus(string t) => StatusLabel.Text = t;
        private void SetBusy(bool b) { InstallButton.IsEnabled = !b; AddButton.IsEnabled = !b; ClearQueueButton.IsEnabled = !b; CancelButton.IsEnabled = b; ForceReinstallCheckbox.IsEnabled = !b; }
        private void UpdateProgressFromLog(string l) { if (l.Contains("[PHASE 1]")) SetProgress(5, "Downloading…"); else if (l.Contains("PHASE 1 COMPLETE")) SetProgress(40, "Done"); else if (l.Contains("[PHASE 2]")) SetProgress(45, "Processing…"); else if (l.Contains("[STEAMCMD]")) SetProgress(ProgressIncrement(5, 38), "Downloading…"); else if (l.Contains("[GMAD]")) SetProgress(ProgressIncrement(45, 90), "Extracting…"); else if (l.Contains("[SUCCESS] Installed")) SetProgress(ProgressIncrement(90, 99), "Installing…"); else if (l.Contains("PHASE 2 COMPLETE")) SetProgress(99, "Finalising…"); else if (l.Contains("DONE")) SetProgress(100, "Done"); }
        private void SetProgress(double v, string l) { MainProgressBar.Value = Math.Clamp(v, 0, 100); ProgressLabel.Text = l; }
        private double ProgressIncrement(double min, double max) { double c = MainProgressBar.Value; return Math.Clamp(c + (max - c) * 0.15, min, max); }
    }

    public static class FlashWindowHelper
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int FlashWindowEx(ref FLASHWINFO pwfi);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)] private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }
        private const uint FLASHW_ALL = 3, FLASHW_TIMERNOFG = 12;
        public static void Flash(Window window) { var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle; var fw = new FLASHWINFO { cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(FLASHWINFO)), hwnd = handle, dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG, uCount = 3, dwTimeout = 0 }; FlashWindowEx(ref fw); }
    }
}
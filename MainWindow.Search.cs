using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using IOPath = System.IO.Path;

namespace Orbit;

/// <summary>"Find anything": Ctrl+K palette that searches every indexed file and folder as you type.</summary>
public partial class MainWindow
{
    bool findOpen;
    string findKind = "all";
    CancellationTokenSource findCts;
    DispatcherTimer findDebounce;
    DateTime lastFindRun;
    bool findHooked;

    void FindEverywhere_Click(object sender, RoutedEventArgs e) => OpenFind();

    internal void OpenFind()
    {
        if (!findHooked)
        {
            SearchService.StatusChanged += () => Dispatcher.BeginInvoke(OnIndexStatus);
            findHooked = true;
        }
        if (previewOpen) ClosePreview();
        findOpen = true;
        SearchOverlay.Visibility = Visibility.Visible;
        UpdateFindStatus();
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            FindBox.Focus();
            Keyboard.Focus(FindBox);
            FindBox.SelectAll();
        });
        if (FindBox.Text.Trim().Length > 0) RunFind();
        else ShowFindHint();
    }

    void CloseFind()
    {
        if (!findOpen) return;
        findOpen = false;
        findCts?.Cancel();
        SearchOverlay.Visibility = Visibility.Collapsed;
        FocusList();
    }

    void SearchBackdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, SearchOverlay)) { CloseFind(); e.Handled = true; }
    }

    void SearchCard_MouseDown(object sender, MouseButtonEventArgs e) => e.Handled = true;

    void FindBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        findDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(60), DispatcherPriority.Input, (_, _) => { findDebounce.Stop(); RunFind(); }, Dispatcher);
        findDebounce.Stop();
        findDebounce.Start();
    }

    void FindKind_Click(object sender, RoutedEventArgs e)
    {
        findKind = FindFolders.IsChecked == true ? "folders" : FindFiles.IsChecked == true ? "files" : "all";
        RunFind();
        FindBox.Focus();
    }

    void ShowFindHint()
    {
        FindResults.ItemsSource = null;
        FindTime.Text = "";
        var idx = SearchService.Current;
        FindCount.Text = idx == null ? "" : FormatCount(idx.Count + SearchService.Added.Count) + " items";
        FindEmpty.Text = "Type part of a name. Letters can be spread out — “invq4” finds “invoice_Q4_final.pdf”. " +
                         "Add more words to narrow it down, including folder names (“finance invoice”).";
        FindEmpty.Visibility = Visibility.Visible;
    }

    async void RunFind()
    {
        var query = FindBox.Text;
        if (query.Trim().Length == 0) { ShowFindHint(); return; }
        findCts?.Cancel();
        var cts = new CancellationTokenSource();
        findCts = cts;
        var ct = cts.Token;
        var kind = findKind;
        var sw = Stopwatch.StartNew();
        lastFindRun = DateTime.UtcNow;
        List<SearchHit> hits;
        int searched;
        try
        {
            (hits, searched) = await Task.Run(() => FuzzySearch.Run(query, kind, 80, ct), ct);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { AppSettings.LogError(ex); return; }
        if (ct.IsCancellationRequested || !findOpen) return;
        sw.Stop();

        FindResults.ItemsSource = hits;
        FindResults.SelectedIndex = hits.Count > 0 ? 0 : -1;
        if (hits.Count > 0) FindResults.ScrollIntoView(hits[0]);
        FindTime.Text = $"{Math.Max(1, sw.ElapsedMilliseconds)} ms";
        FindCount.Text = FormatCount(searched) + " items";
        if (hits.Count == 0)
        {
            FindEmpty.Text = SearchService.Current == null
                ? "Building the index for the first time — results will appear as your drives are scanned."
                : $"Nothing matches “{query.Trim()}”" + (kind != "all" ? $" in {kind}." : ".");
            FindEmpty.Visibility = Visibility.Visible;
        }
        else FindEmpty.Visibility = Visibility.Collapsed;
    }

    void FindBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        int n = FindResults.Items.Count;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Down when n > 0:
                FindResults.SelectedIndex = Math.Min(n - 1, FindResults.SelectedIndex + 1);
                FindResults.ScrollIntoView(FindResults.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up when n > 0:
                FindResults.SelectedIndex = Math.Max(0, FindResults.SelectedIndex - 1);
                FindResults.ScrollIntoView(FindResults.SelectedItem);
                e.Handled = true;
                break;
            case Key.PageDown when n > 0:
                FindResults.SelectedIndex = Math.Min(n - 1, FindResults.SelectedIndex + 8);
                FindResults.ScrollIntoView(FindResults.SelectedItem);
                e.Handled = true;
                break;
            case Key.PageUp when n > 0:
                FindResults.SelectedIndex = Math.Max(0, FindResults.SelectedIndex - 8);
                FindResults.ScrollIntoView(FindResults.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                if (FindResults.SelectedItem is SearchHit hit) OpenHit(hit, ctrl);
                e.Handled = true;
                break;
            case Key.Escape:
                CloseFind();
                e.Handled = true;
                break;
        }
    }

    void FindResults_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindResults.SelectedItem is SearchHit hit) OpenHit(hit, (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    void OpenHit(SearchHit hit, bool showInFolder)
    {
        bool exists;
        try { exists = hit.IsDir ? Directory.Exists(hit.Path) : File.Exists(hit.Path); } catch { exists = false; }
        if (!exists)
        {
            SearchService.Removed[hit.Path] = 0;
            Toast($"“{hit.Name}” no longer exists — it was moved or deleted.");
            RunFind();
            return;
        }
        CloseFind();
        if (showInFolder)
        {
            var parent = IOPath.GetDirectoryName(hit.Path);
            if (parent != null) Navigate(parent, hit.Path);
        }
        else if (hit.IsDir) Navigate(hit.Path);
        else Try(() => Shell.Open(hit.Path));
    }

    void OnIndexStatus()
    {
        UpdateFindStatus();
        // keep results fresh while the first index is still being built
        if (findOpen && SearchService.Indexing && FindBox.Text.Trim().Length > 0 && (DateTime.UtcNow - lastFindRun).TotalSeconds > 1.5)
            RunFind();
    }

    void UpdateFindStatus()
    {
        var idx = SearchService.Current;
        if (SearchService.Indexing)
            FindStatus.Text = $"Indexing… {SearchService.IndexingCount:N0} items scanned";
        else if (idx == null)
            FindStatus.Text = "Index not built yet";
        else
        {
            var age = DateTime.UtcNow - idx.BuiltUtc;
            var ago = age.TotalMinutes < 1 ? "just now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago" :
                      age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago" : $"{(int)age.TotalDays} d ago";
            FindStatus.Text = $"Index updated {ago} · live changes on";
        }
    }

    static string FormatCount(int n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.#}M" : n >= 10_000 ? $"{n / 1000}K" : n.ToString("N0");
}

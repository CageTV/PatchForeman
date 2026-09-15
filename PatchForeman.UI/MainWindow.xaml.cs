using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PatchForeman;
using SeamFinder.Core;

namespace PatchForeman.UI;

public partial class MainWindow : Window
{
    string? _lastOutputPath;
    string? _lastOutputFolder;
    string? _currentLogFilePath;

    bool _outputFolderAutoSet = true;
    bool _suppressOutputTextChanged;

    public MainWindow()
    {
        InitializeComponent();
        LoadPersistedSettings();
        RefreshOutputFolderDefault();
        UpdateEspBoxEnabledStates();
        // Best-effort, non-blocking: auto-check the sibling tools' own
        // settings.json on open, per the user's explicit request ("it should
        // auto read the 4 jsons"). Never let a sibling's missing/corrupt
        // settings file stop this app from opening.
        try { RefreshSiblingSettingsStatus(); } catch { }
    }

    // --- Settings persistence (same pattern as every sibling tool's UI) ---

    static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PatchForeman", "settings.json");

    record PersistedSettings(
        string Mo2InstancePath, string Mo2GameDataPath, string OutputFolder,
        string SeamFixerEsp, string RoadMaskEsp, string TextureFixerEsp, string FloatingFixerEsp,
        bool IncludeSeamFixer, bool IncludeRoadMask, bool IncludeTextureFixer, bool IncludeFloatingFixer,
        bool TrustNorthernRoads, string FloatingThreshold, string FloatingWorldspace, bool SkipReverify,
        bool DisableOtherFourAfterMerge);

    void LoadPersistedSettings()
    {
        try
        {
            if (!File.Exists(SettingsFilePath)) return;
            var s = System.Text.Json.JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(SettingsFilePath));
            if (s is null) return;

            Mo2InstancePathBox.Text = s.Mo2InstancePath;
            Mo2GameDataPathBox.Text = s.Mo2GameDataPath;
            if (!string.IsNullOrEmpty(s.SeamFixerEsp)) SeamFixerEspBox.Text = s.SeamFixerEsp;
            if (!string.IsNullOrEmpty(s.RoadMaskEsp)) RoadMaskEspBox.Text = s.RoadMaskEsp;
            if (!string.IsNullOrEmpty(s.TextureFixerEsp)) TextureFixerEspBox.Text = s.TextureFixerEsp;
            if (!string.IsNullOrEmpty(s.FloatingFixerEsp)) FloatingFixerEspBox.Text = s.FloatingFixerEsp;
            // Default true (include) for a first-ever run / a settings file
            // saved before these checkboxes existed - JSON deserialization
            // leaves a missing bool at its type default (false), which would
            // silently exclude every tool on anyone's very first launch after
            // this update. Only apply a persisted false if the file is new
            // enough to have actually meant it - can't distinguish that from
            // JSON alone, so this defaults new/missing fields to true instead
            // via the settings file itself never having existed yet (the
            // File.Exists check above already handles a BRAND new user; this
            // covers someone upgrading from a settings.json saved before
            // these fields existed, since a JSON object missing a property
            // entirely deserializes it to false, not the record's own default).
            IncludeSeamFixerCheck.IsChecked = s.IncludeSeamFixer || IsLegacySettingsFile(s);
            IncludeRoadMaskCheck.IsChecked = s.IncludeRoadMask || IsLegacySettingsFile(s);
            IncludeTextureFixerCheck.IsChecked = s.IncludeTextureFixer || IsLegacySettingsFile(s);
            IncludeFloatingFixerCheck.IsChecked = s.IncludeFloatingFixer || IsLegacySettingsFile(s);
            TrustNorthernRoadsCheck.IsChecked = s.TrustNorthernRoads;
            if (!string.IsNullOrEmpty(s.FloatingThreshold)) FloatingThresholdBox.Text = s.FloatingThreshold;
            if (!string.IsNullOrEmpty(s.FloatingWorldspace)) FloatingWorldspaceBox.Text = s.FloatingWorldspace;
            SkipReverifyCheck.IsChecked = s.SkipReverify;
            DisableOtherFourCheck.IsChecked = s.DisableOtherFourAfterMerge;
            if (!string.IsNullOrEmpty(s.OutputFolder)) OutputFolderBox.Text = s.OutputFolder; // marks _outputFolderAutoSet false via its own TextChanged handler
        }
        catch
        {
            // Corrupt or unreadable settings file - start fresh rather than block the app from opening.
        }
    }

    // A settings.json saved before the 4 include-checkboxes existed has no
    // way to tell "explicitly unchecked" apart from "field didn't exist yet" -
    // both deserialize IncludeXxx to false. Detect that case via the raw JSON
    // rather than silently starting a returning user with every tool excluded.
    static bool IsLegacySettingsFile(PersistedSettings s) =>
        !s.IncludeSeamFixer && !s.IncludeRoadMask && !s.IncludeTextureFixer && !s.IncludeFloatingFixer;

    void SavePersistedSettings(RunSettings s)
    {
        try
        {
            var persisted = new PersistedSettings(
                s.Mo2InstancePath, s.Mo2GameDataPath, OutputFolderBox.Text.Trim(),
                SeamFixerEspBox.Text.Trim(), RoadMaskEspBox.Text.Trim(), TextureFixerEspBox.Text.Trim(), FloatingFixerEspBox.Text.Trim(),
                s.IncludeSeamFixer, s.IncludeRoadMask, s.IncludeTextureFixer, s.IncludeFloatingFixer,
                s.TrustNorthernRoads, s.FloatingThreshold, s.FloatingWorldspace, s.SkipReverify, s.DisableOtherFourAfterMerge);
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath)!);
            File.WriteAllText(SettingsFilePath, System.Text.Json.JsonSerializer.Serialize(persisted, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - a locked/inaccessible AppData shouldn't stop the run itself.
        }
    }

    // Writes the exact settings a run used into that run's own output
    // folder too (alongside log.txt/esp), separate from the always-on-launch
    // copy above. Matches SeamFinder.UI's own pattern (added there first;
    // ported here 2026-09-15 for uniformity across all 5 tools in this
    // family, per the user's explicit request).
    void SaveSettingsSnapshotToOutputFolder(RunSettings s, string outputFolder)
    {
        try
        {
            Directory.CreateDirectory(outputFolder);
            File.WriteAllText(Path.Combine(outputFolder, "settings-used.json"),
                System.Text.Json.JsonSerializer.Serialize(s, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort - see SavePersistedSettings.
        }
    }

    // --- Per-tool include checkboxes ---

    void IncludeCheck_Changed(object sender, RoutedEventArgs e) => UpdateEspBoxEnabledStates();

    void UpdateEspBoxEnabledStates()
    {
        // Each checkbox's IsChecked="True" in XAML fires this handler DURING
        // InitializeComponent, one checkbox at a time, before its own sibling
        // esp TextBox (or any later checkbox's TextBox) has been constructed
        // yet - so every one of the 4 fields below must be null-checked, not
        // just the first, or a later checkbox's Checked event NREs on a
        // still-unbuilt TextBox further down the XAML document.
        if (SeamFixerEspBox is null || RoadMaskEspBox is null || TextureFixerEspBox is null || FloatingFixerEspBox is null) return;
        SeamFixerEspBox.IsEnabled = IncludeSeamFixerCheck.IsChecked == true;
        RoadMaskEspBox.IsEnabled = IncludeRoadMaskCheck.IsChecked == true;
        TextureFixerEspBox.IsEnabled = IncludeTextureFixerCheck.IsChecked == true;
        FloatingFixerEspBox.IsEnabled = IncludeFloatingFixerCheck.IsChecked == true;
    }

    // --- Sibling tools' own settings.json (read-only sanity check) ---

    void RefreshSiblingSettings_Click(object sender, RoutedEventArgs e) => RefreshSiblingSettingsStatus();

    void RefreshSiblingSettingsStatus()
    {
        var espNames = new[] { SeamFixerEspBox.Text.Trim(), RoadMaskEspBox.Text.Trim(), TextureFixerEspBox.Text.Trim(), FloatingFixerEspBox.Text.Trim() };
        var results = SiblingSettingsReader.CheckAll(espNames);

        var missing = results.Where(r => r.SettingsFound && r.EspFoundAtOutputFolder == false).ToList();
        var noSettings = results.Where(r => !r.SettingsFound).ToList();

        if (missing.Count == 0 && noSettings.Count == 0)
        {
            SiblingSettingsStatusText.Text = "All 4 sibling tools' own settings confirm their esp at the expected location.";
        }
        else
        {
            var parts = new List<string>();
            foreach (var m in missing)
                parts.Add($"{m.ToolName}: esp not found at its own last output folder ({m.OutputFolder}) - did you rename it, or hasn't that tool run yet?");
            foreach (var n in noSettings)
                parts.Add($"{n.ToolName}: no settings.json found yet (hasn't been run through its own UI).");
            SiblingSettingsStatusText.Text = string.Join(" | ", parts);
        }
    }

    // --- Output folder: smart default, stays editable ---

    void OutputFolderBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressOutputTextChanged) return;
        _outputFolderAutoSet = false;
    }

    void RefreshOutputFolderDefault()
    {
        if (OutputFolderBox is null) return;

        var instancePath = Mo2InstancePathBox?.Text.Trim();
        string? defaultPath = null;
        if (!string.IsNullOrEmpty(instancePath))
            defaultPath = Path.Combine(instancePath, "mods", "Patch Foreman");

        OutputHintText.Text = "Writes into your MO2 instance's mods folder, so it shows up as an " +
            "installable mod (a meta.ini is added automatically). You can change this to any folder you like.";

        if (_outputFolderAutoSet && !string.IsNullOrEmpty(defaultPath))
        {
            _suppressOutputTextChanged = true;
            OutputFolderBox.Text = defaultPath;
            _suppressOutputTextChanged = false;
        }
    }

    // --- MO2 panel ---

    void Mo2BrowseInstance_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select your MO2 instance folder (where ModOrganizer.exe lives)" };
        if (dlg.ShowDialog() == true)
            Mo2InstancePathBox.Text = dlg.FolderName;
    }

    void Mo2BrowseGameData_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select the game's Data folder" };
        if (dlg.ShowDialog() == true)
            Mo2GameDataPathBox.Text = dlg.FolderName;
    }

    void Mo2InstancePathBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshMo2Instance();
        RefreshOutputFolderDefault();
    }

    void RefreshMo2Instance()
    {
        var instancePath = Mo2InstancePathBox.Text.Trim();
        if (string.IsNullOrEmpty(instancePath) || !Directory.Exists(instancePath)) return;

        var profilesDir = Path.Combine(instancePath, "profiles");
        Mo2ProfileCombo.Items.Clear();
        if (Directory.Exists(profilesDir))
        {
            foreach (var dir in Directory.GetDirectories(profilesDir))
                Mo2ProfileCombo.Items.Add(Path.GetFileName(dir));
        }

        var iniPath = Path.Combine(instancePath, "ModOrganizer.ini");
        if (File.Exists(iniPath))
        {
            var gamePath = ReadIniGamePath(iniPath);
            if (gamePath != null)
                Mo2GameDataPathBox.Text = Path.Combine(gamePath, "Data");

            var selectedProfile = ReadIniSelectedProfile(iniPath);
            if (selectedProfile != null && Mo2ProfileCombo.Items.Contains(selectedProfile))
                Mo2ProfileCombo.SelectedItem = selectedProfile;
        }

        if (Mo2ProfileCombo.SelectedItem is null && Mo2ProfileCombo.Items.Count > 0)
            Mo2ProfileCombo.SelectedIndex = 0;
    }

    static string? ReadIniGamePath(string iniPath)
    {
        foreach (var line in File.ReadAllLines(iniPath))
        {
            if (!line.StartsWith("gamePath=")) continue;
            var value = line["gamePath=".Length..].Trim();
            var start = value.IndexOf('(');
            var end = value.LastIndexOf(')');
            if (start >= 0 && end > start) value = value[(start + 1)..end];
            return value.Replace("\\\\", "\\");
        }
        return null;
    }

    static string? ReadIniSelectedProfile(string iniPath)
    {
        foreach (var line in File.ReadAllLines(iniPath))
        {
            if (!line.StartsWith("selected_profile=")) continue;
            var value = line["selected_profile=".Length..].Trim();
            var start = value.IndexOf('(');
            var end = value.LastIndexOf(')');
            if (start >= 0 && end > start) value = value[(start + 1)..end];
            return value;
        }
        return null;
    }

    // --- Output ---

    void OutputBrowse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select an output folder" };
        if (dlg.ShowDialog() == true)
            OutputFolderBox.Text = dlg.FolderName;
    }

    // --- Run ---

    record RunSettings(
        string Mo2InstancePath, string Mo2GameDataPath,
        string SeamFixerEsp, string RoadMaskEsp, string TextureFixerEsp, string FloatingFixerEsp,
        bool IncludeSeamFixer, bool IncludeRoadMask, bool IncludeTextureFixer, bool IncludeFloatingFixer,
        bool TrustNorthernRoads, string FloatingThreshold, string FloatingWorldspace, bool SkipReverify,
        bool DisableOtherFourAfterMerge);

    void SetBusy(bool busy)
    {
        RunButton.IsEnabled = !busy;
        GenerateButton.IsEnabled = !busy;
        RunProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    RunSettings SnapshotSettings() => new(
        Mo2InstancePathBox.Text.Trim(), Mo2GameDataPathBox.Text.Trim(),
        SeamFixerEspBox.Text.Trim(), RoadMaskEspBox.Text.Trim(), TextureFixerEspBox.Text.Trim(), FloatingFixerEspBox.Text.Trim(),
        IncludeSeamFixerCheck.IsChecked == true, IncludeRoadMaskCheck.IsChecked == true,
        IncludeTextureFixerCheck.IsChecked == true, IncludeFloatingFixerCheck.IsChecked == true,
        TrustNorthernRoadsCheck.IsChecked == true, FloatingThresholdBox.Text.Trim(), FloatingWorldspaceBox.Text.Trim(),
        SkipReverifyCheck.IsChecked == true, DisableOtherFourCheck.IsChecked == true);

    async void RunButton_Click(object sender, RoutedEventArgs e) => await RunOrGenerate(write: false);
    async void GenerateButton_Click(object sender, RoutedEventArgs e) => await RunOrGenerate(write: true);

    async Task RunOrGenerate(bool write)
    {
        LogBox.Clear();
        ResultText.Text = "";
        OpenPluginButton.IsEnabled = false;
        OpenFolderButton.IsEnabled = false;
        SetBusy(true);

        var settings = SnapshotSettings();
        var outputFolder = OutputFolderBox.Text.Trim();
        StartLogFile(outputFolder);
        SavePersistedSettings(settings);
        SaveSettingsSnapshotToOutputFolder(settings, outputFolder);

        try
        {
            if (string.IsNullOrEmpty(settings.Mo2InstancePath) || string.IsNullOrEmpty(settings.Mo2GameDataPath))
            {
                ShowValidation("Please fill in the MO2 instance folder and game Data folder.");
                return;
            }
            if (string.IsNullOrEmpty(outputFolder))
            {
                ShowValidation("Please choose an output folder.");
                return;
            }
            var profile = Mo2ProfileCombo.SelectedItem as string;
            if (string.IsNullOrEmpty(profile))
            {
                ShowValidation("Please select an MO2 profile.");
                return;
            }
            var threshold = float.TryParse(settings.FloatingThreshold, out var t) ? t : 96f;

            var (result, reverify) = await Task.Run(() => RunPipeline(settings, profile, outputFolder, write, threshold));
            if (result is null) return;

            if (write) Dispatcher.Invoke(() => EnsureMo2MetaIni(outputFolder));

            _lastOutputPath = result.OutputPath;
            _lastOutputFolder = outputFolder;
            var resultMsg = $"{(write ? "Generated" : "[DRY RUN] Would generate")}: {result.Stats.CellsFromOneSource} cell(s) from a single source tool, " +
                $"{result.Stats.CellsHeightAndTextureMerged} merged across tools, {result.Stats.PlacedRefsForwarded} placed-reference override(s) from Floating Object Fixer.";
            if (reverify is not null)
            {
                resultMsg += $" Re-verify: height seams {reverify.HeightSeamsBefore}->{reverify.HeightSeamsAfter}, " +
                    $"texture mismatches {reverify.TextureMismatchesBefore}->{reverify.TextureMismatchesAfter}, " +
                    $"floating objects {reverify.FloatingObjectsFlaggedBefore}->{reverify.FloatingObjectsFlaggedAfter}.";
                if (reverify.HeightSeamsAfter > reverify.HeightSeamsBefore || reverify.TextureMismatchesAfter > reverify.TextureMismatchesBefore
                    || reverify.FloatingObjectsFlaggedAfter > reverify.FloatingObjectsFlaggedBefore)
                    resultMsg += " Check the log for details - the merge introduced something new.";
            }
            resultMsg += " Review the log before installing.";
            ResultText.Text = resultMsg;

            OpenPluginButton.IsEnabled = write && !string.IsNullOrEmpty(result.OutputPath);
            OpenFolderButton.IsEnabled = true;

            if (write && settings.DisableOtherFourAfterMerge)
                DisableMergedSourceTools(settings, profile);
        }
        catch (Exception ex)
        {
            AppendLog("");
            AppendLog("ERROR: " + ex);
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    // Only disables the source esps that were actually folded into this
    // merge (checked) - an excluded tool's esp is still doing its own job
    // and PatchForeman hasn't superseded it, so it must stay active.
    void DisableMergedSourceTools(RunSettings s, string profile)
    {
        var toDisable = new List<string>();
        if (s.IncludeSeamFixer && !string.IsNullOrWhiteSpace(s.SeamFixerEsp)) toDisable.Add(s.SeamFixerEsp);
        if (s.IncludeRoadMask && !string.IsNullOrWhiteSpace(s.RoadMaskEsp)) toDisable.Add(s.RoadMaskEsp);
        if (s.IncludeTextureFixer && !string.IsNullOrWhiteSpace(s.TextureFixerEsp)) toDisable.Add(s.TextureFixerEsp);
        if (s.IncludeFloatingFixer && !string.IsNullOrWhiteSpace(s.FloatingFixerEsp)) toDisable.Add(s.FloatingFixerEsp);
        if (toDisable.Count == 0) return;

        var confirm = MessageBox.Show(this,
            "PatchForeman merged the following into a single patch:\n\n  " + string.Join("\n  ", toDisable) +
            "\n\nDisable these 4 plugins in this MO2 profile's plugins.txt now?\n" +
            "A timestamped backup of plugins.txt is made first, and only plugins currently checked are touched.",
            "Disable merged source plugins?", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        try
        {
            var disableResult = Mo2PluginsTxtEditor.DisableIfChecked(s.Mo2InstancePath, profile, toDisable);
            if (disableResult.Disabled.Count > 0)
                AppendLog($"Disabled in plugins.txt (backup: {disableResult.BackupPath}): {string.Join(", ", disableResult.Disabled)}");
            if (disableResult.AlreadyUnchecked.Count > 0)
                AppendLog($"Already unchecked: {string.Join(", ", disableResult.AlreadyUnchecked)}");
            if (disableResult.NotFound.Count > 0)
                AppendLog($"Not found in plugins.txt: {string.Join(", ", disableResult.NotFound)}");
        }
        catch (Exception ex)
        {
            AppendLog("ERROR disabling source plugins: " + ex.Message);
            MessageBox.Show(this, ex.Message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    (MergeResult? Result, ReverifySummary? Reverify) RunPipeline(RunSettings s, string profile, string outputFolder, bool write, float floatingThreshold)
    {
        void Log(string line) => Dispatcher.Invoke(() => AppendLog(line));
        const string pluginName = "PatchForeman.esp";

        Log($"MO2 instance: {s.Mo2InstancePath}");
        Log($"Profile: {profile}");
        Log($"Game Data path: {s.Mo2GameDataPath}");
        Log($"Mode: {(write ? "WRITE" : "DRY RUN")}");
        Log("");

        var resolved = Mo2Resolver.Resolve(s.Mo2InstancePath, profile, s.Mo2GameDataPath);
        Log($"Resolved {resolved.LoadOrder.Count} active plugins to real files.");
        if (resolved.MissingPlugins.Count > 0)
        {
            Log($"WARNING: {resolved.MissingPlugins.Count} active plugins could not be found:");
            foreach (var m in resolved.MissingPlugins) Log("  " + m);
        }

        // An unchecked tool is excluded by passing an empty esp name -
        // MergeEngine's own "is this plugin present in the load order" check
        // naturally treats an empty filename as absent, so no MergeEngine
        // change is needed to honor the include checkboxes.
        var sources = new SourceToolPlugins(
            s.IncludeSeamFixer ? s.SeamFixerEsp : "",
            s.IncludeRoadMask ? s.RoadMaskEsp : "",
            s.IncludeTextureFixer ? s.TextureFixerEsp : "",
            s.IncludeFloatingFixer ? s.FloatingFixerEsp : "");
        if (!s.IncludeSeamFixer) Log("Landscape Seam Fixer excluded from this merge (unchecked).");
        if (!s.IncludeRoadMask) Log("Road Mask Merger excluded from this merge (unchecked).");
        if (!s.IncludeTextureFixer) Log("Landscape Texture Fixer excluded from this merge (unchecked).");
        if (!s.IncludeFloatingFixer) Log("Floating Object Fixer excluded from this merge (unchecked).");
        var result = MergeEngine.RunForResolvedPlugins(resolved.LoadOrder, sources, pluginName, outputFolder, Log, dryRun: !write);

        ReverifySummary? reverify = null;
        if (write && !s.SkipReverify)
        {
            Log("");
            reverify = ReverifyPass.Run(
                resolved.LoadOrder, result.OutputPath, resolved.ResolveDataFile, Log,
                s.TrustNorthernRoads, floatingThreshold, s.FloatingWorldspace);
        }

        return (result, reverify);
    }

    void ShowValidation(string message)
    {
        Dispatcher.Invoke(() => MessageBox.Show(this, message, "Missing information", MessageBoxButton.OK, MessageBoxImage.Warning));
    }

    void EnsureMo2MetaIni(string outputFolder)
    {
        var instancePath = Mo2InstancePathBox.Text.Trim();
        if (string.IsNullOrEmpty(instancePath)) return;

        var modsDir = Path.Combine(instancePath, "mods") + Path.DirectorySeparatorChar;
        var fullOutput = Path.GetFullPath(outputFolder) + Path.DirectorySeparatorChar;
        if (!fullOutput.StartsWith(Path.GetFullPath(modsDir), StringComparison.OrdinalIgnoreCase)) return;

        Directory.CreateDirectory(outputFolder);

        var metaPath = Path.Combine(outputFolder, "meta.ini");
        if (!File.Exists(metaPath))
        {
            File.WriteAllText(metaPath, "[General]\r\ngameName=SkyrimSE\r\nmodid=0\r\nversion=1.0.0\r\ninstalled=true\r\n");
            AppendLog($"Wrote meta.ini so this shows up as an MO2 mod: {metaPath}");
        }
    }

    // Points AppendLog's file mirror at <outputFolder>/log.txt and starts it
    // fresh (matching LogBox.Clear() for the UI copy) - same folder the esp
    // for this run lands in. Matches SeamFinder.UI's own pattern.
    void StartLogFile(string outputFolder)
    {
        if (string.IsNullOrEmpty(outputFolder)) { _currentLogFilePath = null; return; }
        try
        {
            Directory.CreateDirectory(outputFolder);
            _currentLogFilePath = Path.Combine(outputFolder, "log.txt");
            File.WriteAllText(_currentLogFilePath, "");
        }
        catch
        {
            // Best-effort - a locked/inaccessible output folder shouldn't
            // stop the run itself, just the file mirror of its log.
            _currentLogFilePath = null;
        }
    }

    void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
        if (_currentLogFilePath is not null)
        {
            try { File.AppendAllText(_currentLogFilePath, line + Environment.NewLine); }
            catch { /* best-effort, see StartLogFile */ }
        }
    }

    // --- Result bar ---

    void OpenPluginButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputPath is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_lastOutputPath}\"") { UseShellExecute = true });
    }

    void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastOutputFolder is null) return;
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_lastOutputFolder}\"") { UseShellExecute = true });
    }
}

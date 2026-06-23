namespace Everwind.TrainerApp;

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

public partial class MainWindow : Window
{
    private const char MultiplierSuffix = '\u00D7';
    private const string ProbeDecimalFormat = "0.###";
    private const uint ShellIcon = 0x000000100;
    private const uint ShellLargeIcon = 0x000000000;
    private static readonly SolidColorBrush StatusAccentBrush = CreateFrozenBrush(215, 196, 106);
    private static readonly SolidColorBrush StatusSuccessBrush = CreateFrozenBrush(111, 185, 141);
    private static readonly SolidColorBrush StatusWarningBrush = CreateFrozenBrush(208, 161, 90);
    private static readonly SolidColorBrush StatusDangerBrush = CreateFrozenBrush(214, 106, 115);
    private static readonly SolidColorBrush StatusMutedBrush = CreateFrozenBrush(133, 141, 152);
    private static readonly SolidColorBrush DropAcceptBackgroundBrush = CreateFrozenBrush(29, 42, 56);
    private static readonly SolidColorBrush DropRejectBackgroundBrush = CreateFrozenBrush(44, 24, 30);
    private static readonly string DefaultGameExe = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "games",
        "Everwind",
        "Everwind.exe");
    private static readonly string[] BackgroundCandidates =
    [
        Path.Combine("_Bonus", "Wallpapers", "Everwind_Wallpaper_3840x2160_10.png"),
        Path.Combine("_Bonus", "Wallpapers", "Everwind_Wallpaper_3840x2160_2.png"),
        Path.Combine("_Bonus", "Wallpapers", "Everwind_Wallpaper_3840x2160_14.png")
    ];

    private static readonly string SavedGamePathFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "EverwindTrainer",
        "game-path.txt");

    private readonly DispatcherTimer _statusTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };

    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly Dictionary<string, Process> _maintainers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FeaturePinBinding> _featurePins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<UIElement, FeaturePinBinding> _featurePinsByRow = new();
    // WPF raises these toggle changes on the UI dispatcher; this only suppresses re-entrant UI events.
    private bool _suppressToggleEvents;
    private string _gameExePath = DefaultGameExe;
    private string? _probePath;
    private DateTime _launchPendingUntilUtc = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        _gameExePath = LoadSavedGamePath();
        _probePath = FindRuntimeProbe();
        _statusTimer.Tick += (_, _) => RefreshStatuses();
        _statusTimer.Start();
        RegisterFeaturePins();
        UpdateGamePathText();
        UpdateBrandIcon();
        UpdateBackgroundImage();
        RefreshStatuses();
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var feature in _maintainers.Keys.ToArray())
        {
            StopFeatureMaintainer(feature);
        }

        _statusTimer.Stop();
        _commandGate.Dispose();
        base.OnClosed(e);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveChromeSource(e.OriginalSource))
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // WPF can throw if the mouse state changes mid-drag; harmless.
            }
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e)
    {
        ToggleWindowState();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleWindowState()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void LaunchGame_Click(object sender, RoutedEventArgs e) => LaunchGame();

    private void SectionToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string panelName } button ||
            FindName(panelName) is not FrameworkElement)
        {
            return;
        }

        ToggleSection(panelName, button);
        e.Handled = true;
    }

    private void SectionHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed ||
            sender is not FrameworkElement { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split('|');
        if (parts.Length != 2 ||
            FindName(parts[1]) is not ToggleButton button)
        {
            return;
        }

        ToggleSection(parts[0], button);
        e.Handled = true;
    }

    private void ToggleSection(string panelName, ToggleButton button)
    {
        if (FindName(panelName) is not FrameworkElement panel)
        {
            return;
        }

        var collapse = panel.Visibility == Visibility.Visible;
        panel.Visibility = collapse ? Visibility.Collapsed : Visibility.Visible;
        button.IsChecked = collapse;
        button.ToolTip = collapse ? "Expand section" : "Collapse section";
    }

    private void RegisterFeaturePins()
    {
        RegisterFeaturePin("health", HealthRow, PlayerOptionsPanel, HealthPinButton);
        RegisterFeaturePin("stamina", StaminaRow, PlayerOptionsPanel, StaminaPinButton);
        RegisterFeaturePin("damage", DamageRow, PlayerOptionsPanel, DamagePinButton);
        RegisterFeaturePin("blockDamage", BlockDamageRow, PlayerOptionsPanel, BlockDamagePinButton);
        RegisterFeaturePin("xp", XpRow, StatsOptionsPanel, XpPinButton);
        RegisterFeaturePin("speed", SpeedRow, StatsOptionsPanel, SpeedPinButton);
        RegisterFeaturePin("jump", JumpRow, StatsOptionsPanel, JumpPinButton);
        RegisterFeaturePin("noDurability", NoDurabilityRow, InventoryOptionsPanel, NoDurabilityPinButton);
        RegisterFeaturePin("durability", DurabilityRow, InventoryOptionsPanel, DurabilityPinButton);
        RegisterFeaturePin("itemAmount", ItemAmountRow, InventoryOptionsPanel, ItemAmountPinButton);
        UpdatePinnedCardVisibility();
    }

    private void RegisterFeaturePin(string feature, Border row, StackPanel originPanel, ToggleButton pinButton)
    {
        var binding = new FeaturePinBinding(row, originPanel, originPanel.Children.IndexOf(row), pinButton);
        _featurePins[feature] = binding;
        _featurePinsByRow[row] = binding;
    }

    private void PinFeature_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: string feature } button ||
            !_featurePins.TryGetValue(feature, out var binding))
        {
            return;
        }

        SetFeaturePinned(binding, button.IsChecked == true);
        e.Handled = true;
    }

    private void SetFeaturePinned(FeaturePinBinding binding, bool pinned)
    {
        if (pinned)
        {
            if (!ReferenceEquals(binding.Row.Parent, PinnedOptionsPanel))
            {
                if (binding.Row.Parent is Panel currentParent)
                {
                    currentParent.Children.Remove(binding.Row);
                }

                PinnedOptionsPanel.Children.Add(binding.Row);
            }

            binding.PinButton.IsChecked = true;
        }
        else
        {
            if (ReferenceEquals(binding.Row.Parent, PinnedOptionsPanel))
            {
                PinnedOptionsPanel.Children.Remove(binding.Row);
            }
            else if (binding.Row.Parent is Panel currentParent)
            {
                currentParent.Children.Remove(binding.Row);
            }

            var insertIndex = FindOriginalInsertIndex(binding);
            binding.OriginPanel.Children.Insert(insertIndex, binding.Row);
            binding.PinButton.IsChecked = false;
        }

        UpdatePinnedCardVisibility();
    }

    private int FindOriginalInsertIndex(FeaturePinBinding binding)
    {
        var insertIndex = 0;
        foreach (UIElement child in binding.OriginPanel.Children)
        {
            if (_featurePinsByRow.TryGetValue(child, out var otherBinding) &&
                ReferenceEquals(otherBinding.OriginPanel, binding.OriginPanel) &&
                otherBinding.OriginalIndex > binding.OriginalIndex)
            {
                break;
            }

            insertIndex++;
        }

        return insertIndex;
    }

    private void UpdatePinnedCardVisibility()
    {
        PinnedCard.Visibility = PinnedOptionsPanel.Children.Count > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void GameDropZone_DragEnter(object sender, DragEventArgs e) => PreviewGameDrop(e);

    private void GameDropZone_DragOver(object sender, DragEventArgs e) => PreviewGameDrop(e);

    private void GameDropZone_DragLeave(object sender, DragEventArgs e)
    {
        UpdateGamePathText();
        e.Handled = true;
    }

    private void GameDropZone_Drop(object sender, DragEventArgs e)
    {
        if (TryGetDraggedExePath(e, out var exePath))
        {
            SetGameExePath(exePath);
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            ShowActionStatus("Drop an .exe file here", StatusKind.Warning);
            e.Effects = DragDropEffects.None;
        }

        UpdateGamePathText();
        e.Handled = true;
    }

    private void GameDropZone_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        BrowseForGameExe();
    }

    private void BrowseForGameExe()
    {
        var initialDirectory = FirstExistingDirectory(
            Path.GetDirectoryName(_gameExePath),
            Path.GetDirectoryName(DefaultGameExe),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));

        var dialog = new OpenFileDialog
        {
            Title = "Select Everwind.exe",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            FileName = "Everwind.exe",
            InitialDirectory = initialDirectory,
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
        {
            SetGameExePath(dialog.FileName);
        }
    }

    private bool IsInteractiveChromeSource(object? source)
    {
        var current = source as DependencyObject;
        while (current is not null)
        {
            if (ReferenceEquals(current, GameDropZone) ||
                current is ButtonBase ||
                current is TextBox)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }

    private async void FeatureToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressToggleEvents || sender is not ToggleButton { Tag: string feature } toggle)
        {
            return;
        }

        var enabled = toggle.IsChecked == true;

        if (enabled && ReferenceEquals(toggle, NoDurabilityToggle))
        {
            SetToggleWithoutApplying(DurabilityToggle, false);
        }
        else if (enabled && ReferenceEquals(toggle, DurabilityToggle))
        {
            SetToggleWithoutApplying(NoDurabilityToggle, false);
        }

        await ApplyFeatureAsync(feature, enabled);
    }

    private async void ItemAmountApply_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        ItemAmountValue.Text = ReadItemAmount().ToString(CultureInfo.InvariantCulture);

        var command = BuildFeatureCommand("itemAmount", enabled: true);
        if (command is null)
        {
            return;
        }

        await RunTrainerCommandAsync(command);
    }

    private async void StepValue_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split('|');
        if (parts.Length != 6 ||
            FindName(parts[0]) is not TextBox box ||
            !decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var step) ||
            !decimal.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var minimum) ||
            !decimal.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var maximum) ||
            !int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var decimals))
        {
            return;
        }

        var feature = parts[5];
        var value = ReadDecimal(box, fallback: 0M);
        value = Math.Min(maximum, Math.Max(minimum, value + step));
        box.Text = IsMultiplierFeature(feature)
            ? FormatMultiplier(value, decimals)
            : FormatDecimal(value, decimals);

        if (IsFeatureToggleOn(feature))
        {
            await ApplyFeatureAsync(feature, enabled: true);
        }
    }

    private async void ValueBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { Tag: string tag } box ||
            !TryGetFeatureFromValueTag(tag, out var feature))
        {
            return;
        }

        if (IsMultiplierFeature(feature))
        {
            NormalizeMultiplierValueBox(box, tag);
        }

        if (IsFeatureToggleOn(feature))
        {
            await ApplyFeatureAsync(feature, enabled: true);
        }
    }

    private void MultiplierValueBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox box)
        {
            return;
        }

        box.Text = StripMultiplierSuffix(box.Text);
        Dispatcher.BeginInvoke(() => box.SelectAll(), DispatcherPriority.Input);
    }

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox { Tag: string tag } box)
        {
            return;
        }

        if (TryGetFeatureFromValueTag(tag, out var feature))
        {
            if (IsMultiplierFeature(feature))
            {
                NormalizeMultiplierValueBox(box, tag);
            }

            e.Handled = true;
            Keyboard.ClearFocus();
        }
    }

    private async Task ApplyFeatureAsync(string feature, bool enabled)
    {
        if (FeatureNeedsMaintainer(feature))
        {
            if (enabled)
            {
                StartFeatureMaintainer(feature);
                return;
            }

            StopFeatureMaintainer(feature);
        }

        var command = BuildFeatureCommand(feature, enabled);
        if (command is null)
        {
            HideActionStatus();
            return;
        }

        await RunTrainerCommandAsync(command);
    }

    private List<string>? BuildFeatureCommand(string feature, bool enabled, bool once = true)
    {
        var arguments = new List<string> { "--train" };

        switch (feature)
        {
            case "health":
                arguments.Add(enabled ? "--infinite-health" : "--disable-infinite-health");
                break;
            case "stamina":
                arguments.Add(enabled ? "--infinite-stamina" : "--disable-infinite-stamina");
                break;
            case "damage":
                if (enabled)
                {
                    arguments.Add("--damage");
                    AddDecimalArgument(arguments, DamageValue, 1M);
                }
                else
                {
                    arguments.Add("--reset-damage");
                }
                break;
            case "blockDamage":
                if (enabled)
                {
                    arguments.Add("--block-damage");
                    AddDecimalArgument(arguments, BlockDamageValue, 1M);
                }
                else
                {
                    arguments.Add("--reset-block-damage");
                }
                break;
            case "xp":
                if (enabled)
                {
                    arguments.Add("--xp");
                    AddDecimalArgument(arguments, XpValue, 1M);
                }
                else
                {
                    arguments.Add("--reset-xp");
                }
                break;
            case "speed":
                if (enabled)
                {
                    arguments.Add("--speed");
                    AddDecimalArgument(arguments, SpeedValue, 1.5M);
                }
                else
                {
                    arguments.Add("--reset-speed");
                }
                break;
            case "jump":
                if (enabled)
                {
                    arguments.Add("--jump");
                    AddDecimalArgument(arguments, JumpValue, 2M);
                }
                else
                {
                    arguments.Add("--reset-jump");
                }
                break;
            case "noDurability":
                arguments.Add(enabled ? "--no-durability-loss" : "--reset-durability");
                break;
            case "durability":
                if (enabled)
                {
                    arguments.Add("--durability");
                    AddDecimalArgument(arguments, DurabilityValue, 1M);
                }
                else
                {
                    arguments.Add("--reset-durability");
                }
                break;
            case "itemAmount":
                if (!enabled)
                {
                    return null;
                }

                arguments.Add("--item-amount");
                arguments.Add(ReadItemAmount().ToString(CultureInfo.InvariantCulture));
                break;
            default:
                return null;
        }

        if (once)
        {
            arguments.Add("--once");
        }

        return arguments;
    }

    private static bool FeatureNeedsMaintainer(string feature) =>
        feature.Equals("health", StringComparison.OrdinalIgnoreCase);

    private void StartFeatureMaintainer(string feature)
    {
        StopFeatureMaintainer(feature);

        var command = BuildFeatureCommand(feature, enabled: true, once: false);
        if (command is null)
        {
            ShowActionStatus($"{FeatureDisplayName(feature)} unavailable", StatusKind.Danger);
            return;
        }

        command.Add("--interval");
        command.Add("250");

        var startInfo = CreateProbeStartInfo(command);
        if (startInfo is null)
        {
            return;
        }

        try
        {
            var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, _) => { };
            process.ErrorDataReceived += (_, eventArgs) =>
            {
                if (string.IsNullOrWhiteSpace(eventArgs.Data))
                {
                    return;
                }

                Dispatcher.BeginInvoke(() => ShowActionStatus(eventArgs.Data, StatusKind.Danger));
            };
            process.Exited += (_, _) =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_maintainers.TryGetValue(feature, out var current) && ReferenceEquals(current, process))
                    {
                        _maintainers.Remove(feature);
                        process.Dispose();
                        if (GetToggle(feature)?.IsChecked == true)
                        {
                            ShowActionStatus($"{FeatureDisplayName(feature)} guard stopped", StatusKind.Danger);
                        }
                    }
                });
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _maintainers[feature] = process;
            HideActionStatus();
        }
        catch (Exception exception)
        {
            ShowActionStatus(exception.Message, StatusKind.Danger);
        }
        finally
        {
            RefreshStatuses();
        }
    }

    private void StopFeatureMaintainer(string feature)
    {
        if (!_maintainers.Remove(feature, out var process))
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(1000);
            }
        }
        catch
        {
            // The guard process may already be gone; either way, the UI is stopping maintenance.
        }
        finally
        {
            process.Dispose();
        }
    }

    private async Task RunTrainerCommandAsync(IReadOnlyList<string> arguments)
    {
        var startInfo = CreateProbeStartInfo(arguments);
        if (startInfo is null)
        {
            return;
        }

        await _commandGate.WaitAsync();
        try
        {
            using var process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = false
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode == 0)
            {
                HideActionStatus();
            }
            else
            {
                var detail = LastMeaningfulLine(stderr) ?? LastMeaningfulLine(stdout) ?? $"exit {process.ExitCode}";
                ShowActionStatus(detail, StatusKind.Danger);
            }
        }
        catch (Exception exception)
        {
            ShowActionStatus(exception.Message, StatusKind.Danger);
        }
        finally
        {
            _commandGate.Release();
            RefreshStatuses();
        }
    }

    private void LaunchGame()
    {
        var gameExe = string.IsNullOrWhiteSpace(_gameExePath)
            ? DefaultGameExe
            : _gameExePath;

        if (!File.Exists(gameExe))
        {
            MessageBox.Show(
                $"Could not find the game executable at:\n{gameExe}\n\nDrop Everwind.exe into the launch pool, or click the pool to browse for it.",
                "Everwind Trainer",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            ShowActionStatus("Set the game path first", StatusKind.Warning);
            SetLaunchState("Launch Game", "Game file not found", StatusKind.Danger, enabled: true);
            UpdateGamePathText();
            return;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = gameExe,
                WorkingDirectory = Path.GetDirectoryName(gameExe) ?? "",
                UseShellExecute = true
            };
            _ = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Windows did not start the game process.");
            _launchPendingUntilUtc = DateTime.UtcNow.AddSeconds(12);
            SetLaunchState("Launching...", "Waiting for the game", StatusKind.Warning, enabled: false);
            HideActionStatus();
        }
        catch (Exception exception)
        {
            _launchPendingUntilUtc = DateTime.MinValue;
            ShowActionStatus($"Launch failed: {exception.Message}", StatusKind.Danger);
            RefreshStatuses();
        }
    }

    private void PreviewGameDrop(DragEventArgs e)
    {
        if (TryGetDraggedExePath(e, out _))
        {
            e.Effects = DragDropEffects.Copy;
            GameDropZone.BorderBrush = BrushForStatus(StatusKind.Accent);
            GameDropZone.Background = DropAcceptBackgroundBrush;
            GamePathTitleText.Text = "Release to use this EXE";
            GamePathDetailText.Text = "Trainer will remember it";
        }
        else
        {
            e.Effects = DragDropEffects.None;
            GameDropZone.BorderBrush = BrushForStatus(StatusKind.Danger);
            GameDropZone.Background = DropRejectBackgroundBrush;
            GamePathTitleText.Text = "Drop an .exe file";
            GamePathDetailText.Text = "Everwind.exe recommended";
        }

        e.Handled = true;
    }

    private static bool TryGetDraggedExePath(DragEventArgs e, out string exePath)
    {
        exePath = "";
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        var candidate = files?
            .FirstOrDefault(file =>
                File.Exists(file) &&
                Path.GetExtension(file).Equals(".exe", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        exePath = candidate;
        return true;
    }

    private void SetGameExePath(string exePath)
    {
        if (!File.Exists(exePath))
        {
            ShowActionStatus("That file does not exist", StatusKind.Danger);
            return;
        }

        if (!Path.GetExtension(exePath).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            ShowActionStatus("Please choose an .exe file", StatusKind.Warning);
            return;
        }

        _gameExePath = exePath;
        SaveGamePath(exePath);
        UpdateGamePathText();
        UpdateBrandIcon();
        UpdateBackgroundImage();

        if (Path.GetFileName(exePath).Equals("Everwind.exe", StringComparison.OrdinalIgnoreCase))
        {
            HideActionStatus();
        }
        else
        {
            ShowActionStatus("Selected file is not named Everwind.exe", StatusKind.Warning);
        }
    }

    private void UpdateGamePathText()
    {
        GameDropZone.ClearValue(Border.BorderBrushProperty);
        GameDropZone.ClearValue(Border.BackgroundProperty);

        var exists = File.Exists(_gameExePath);
        if (exists)
        {
            GamePathTitleText.Text = Path.GetFileName(_gameExePath);
            GamePathDetailText.Text = IsDefaultGamePath(_gameExePath)
                ? "Default install path"
                : TrimPathForDisplay(_gameExePath, 34);
            return;
        }

        GamePathTitleText.Text = "Drop Everwind.exe";
        GamePathDetailText.Text = IsDefaultGamePath(_gameExePath)
            ? "or click to browse"
            : $"Missing: {TrimPathForDisplay(_gameExePath, 26)}";
    }

    private void UpdateBrandIcon()
    {
        BrandIconImage.Visibility = Visibility.Collapsed;
        BrandIconFallback.Visibility = Visibility.Visible;

        var iconPath = File.Exists(_gameExePath) ? _gameExePath : DefaultGameExe;
        if (!File.Exists(iconPath))
        {
            return;
        }

        var shellInfo = new ShellFileInfo();
        var result = GetShellFileInfo(
            iconPath,
            0,
            ref shellInfo,
            (uint)Marshal.SizeOf<ShellFileInfo>(),
            ShellIcon | ShellLargeIcon);
        if (result == IntPtr.Zero || shellInfo.IconHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var source = Imaging.CreateBitmapSourceFromHIcon(
                shellInfo.IconHandle,
                Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();

            BrandIconImage.Source = source;
            BrandIconImage.Visibility = Visibility.Visible;
            BrandIconFallback.Visibility = Visibility.Collapsed;
            Icon = source;
        }
        catch
        {
            // Keep the EW fallback if the selected executable has no readable icon.
        }
        finally
        {
            DestroyIcon(shellInfo.IconHandle);
        }
    }

    private void UpdateBackgroundImage()
    {
        EverwindBackgroundImage.Source = null;
        EverwindBackgroundImage.Visibility = Visibility.Collapsed;

        var backgroundPath = FindEverwindBackgroundPath();
        if (backgroundPath is null)
        {
            return;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.UriSource = new Uri(backgroundPath, UriKind.Absolute);
            image.EndInit();
            image.Freeze();

            EverwindBackgroundImage.Source = image;
            EverwindBackgroundImage.Visibility = Visibility.Visible;
        }
        catch
        {
            // Keep the dark fallback if the local wallpaper cannot be loaded.
        }
    }

    private string? FindEverwindBackgroundPath()
    {
        var gameDirectory = Path.GetDirectoryName(File.Exists(_gameExePath) ? _gameExePath : DefaultGameExe);
        if (string.IsNullOrWhiteSpace(gameDirectory))
        {
            return null;
        }

        foreach (var relativePath in BackgroundCandidates)
        {
            var path = Path.Combine(gameDirectory, relativePath);
            if (File.Exists(path))
            {
                return path;
            }
        }

        var wallpaperDirectory = Path.Combine(gameDirectory, "_Bonus", "Wallpapers");
        if (!Directory.Exists(wallpaperDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(wallpaperDirectory, "Everwind_Wallpaper_3840x2160_*.png")
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string LoadSavedGamePath()
    {
        try
        {
            if (File.Exists(SavedGamePathFile))
            {
                var savedPath = File.ReadAllText(SavedGamePathFile).Trim();
                if (!string.IsNullOrWhiteSpace(savedPath))
                {
                    return savedPath;
                }
            }
        }
        catch
        {
            // A missing/unreadable preference file should never block the trainer.
        }

        return DefaultGameExe;
    }

    private static void SaveGamePath(string exePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavedGamePathFile)!);
            File.WriteAllText(SavedGamePathFile, exePath);
        }
        catch
        {
            // Launching still works for this session if the preference cannot be saved.
        }
    }

    private static bool IsDefaultGamePath(string path) =>
        path.Equals(DefaultGameExe, StringComparison.OrdinalIgnoreCase);

    private static string TrimPathForDisplay(string path, int maxLength)
    {
        if (path.Length <= maxLength)
        {
            return path;
        }

        var fileName = Path.GetFileName(path);
        var directory = Path.GetDirectoryName(path) ?? "";
        var tail = Path.Combine(Path.GetFileName(directory), fileName);
        return tail.Length + 2 <= maxLength
            ? $"\u2026\\{tail}"
            : $"\u2026\\{fileName}";
    }

    private ProcessStartInfo? CreateProbeStartInfo(IReadOnlyList<string> arguments)
    {
        var probePath = _probePath;
        if (string.IsNullOrWhiteSpace(probePath) || !File.Exists(probePath))
        {
            _probePath = FindRuntimeProbe();
            probePath = _probePath;
        }

        if (string.IsNullOrWhiteSpace(probePath) || !File.Exists(probePath))
        {
            ShowActionStatus("Runtime probe missing", StatusKind.Danger);
            MessageBox.Show(
                "Could not find Everwind.RuntimeProbe.exe. Build the trainer app from the project folder so it can copy the probe beside the UI.",
                "Everwind Trainer",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            RefreshStatuses();
            return null;
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = probePath,
            WorkingDirectory = Path.GetDirectoryName(probePath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static string? FindRuntimeProbe()
    {
        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "RuntimeProbe", "Everwind.RuntimeProbe.exe"),
            Path.Combine(baseDirectory, "Everwind.RuntimeProbe.exe"),
            Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "Everwind.RuntimeProbe",
                "bin",
                "Release",
                "net8.0-windows",
                "Everwind.RuntimeProbe.exe")),
            Path.GetFullPath(Path.Combine(
                baseDirectory,
                "..",
                "..",
                "..",
                "..",
                "Everwind.RuntimeProbe",
                "bin",
                "Debug",
                "net8.0-windows",
                "Everwind.RuntimeProbe.exe"))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private void RefreshStatuses()
    {
        var gameRuntimeFound = ProcessExists("Everwind-Win64-Shipping");
        var topLevelFound = ProcessExists("Everwind");

        if (gameRuntimeFound)
        {
            _launchPendingUntilUtc = DateTime.MinValue;
            SetLaunchState("Game Running", "Connected - in game", StatusKind.Success, enabled: false);
        }
        else if (topLevelFound)
        {
            _launchPendingUntilUtc = DateTime.MinValue;
            SetLaunchState("Game Running", "Open - waiting for world", StatusKind.Warning, enabled: false);
        }
        else if (DateTime.UtcNow < _launchPendingUntilUtc)
        {
            SetLaunchState("Launching...", "Waiting for the game", StatusKind.Warning, enabled: false);
        }
        else
        {
            _launchPendingUntilUtc = DateTime.MinValue;
            SetLaunchState("Launch Game", "Game not detected", StatusKind.Danger, enabled: true);
        }
    }

    private static bool ProcessExists(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        try
        {
            return processes.Length > 0;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    private bool IsFeatureToggleOn(string feature)
    {
        return GetToggle(feature)?.IsChecked == true;
    }

    private ToggleButton? GetToggle(string feature) =>
        feature switch
        {
            "health" => InfiniteHealthToggle,
            "stamina" => InfiniteStaminaToggle,
            "damage" => DamageToggle,
            "blockDamage" => BlockDamageToggle,
            "xp" => XpToggle,
            "speed" => SpeedToggle,
            "jump" => JumpToggle,
            "noDurability" => NoDurabilityToggle,
            "durability" => DurabilityToggle,
            _ => null
        };

    private void SetToggleWithoutApplying(ToggleButton toggle, bool value)
    {
        _suppressToggleEvents = true;
        try
        {
            toggle.IsChecked = value;
        }
        finally
        {
            _suppressToggleEvents = false;
        }
    }

    private static bool TryGetFeatureFromValueTag(string tag, out string feature)
    {
        var parts = tag.Split('|');
        feature = parts.Length > 0 ? parts[0] : "";
        return !string.IsNullOrWhiteSpace(feature);
    }

    private static string FirstExistingDirectory(params string?[] paths)
    {
        foreach (var path in paths)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            {
                return path;
            }
        }

        return "";
    }

    private static void AddDecimalArgument(ICollection<string> arguments, TextBox valueBox, decimal fallback)
    {
        arguments.Add(ReadDecimal(valueBox, fallback).ToString(ProbeDecimalFormat, CultureInfo.InvariantCulture));
    }

    private static void NormalizeMultiplierValueBox(TextBox box, string tag)
    {
        var fallback = ReadFallbackFromTag(tag, 1M);
        box.Text = FormatMultiplier(ReadDecimal(box, fallback), 2);
    }

    private static decimal ReadFallbackFromTag(string tag, decimal fallback)
    {
        var parts = tag.Split('|');
        return parts.Length > 1 &&
               decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var defaultValue)
            ? defaultValue
            : fallback;
    }

    private void ShowActionStatus(string text, StatusKind kind)
    {
        ActionStatusText.Text = text;
        var brush = BrushForStatus(kind);
        ActionStatusText.Foreground = brush;
        ActionStatusDot.Foreground = brush;
        ActionStatusPanel.Visibility = string.IsNullOrWhiteSpace(text)
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void HideActionStatus()
    {
        ActionStatusText.Text = "";
        ActionStatusPanel.Visibility = Visibility.Collapsed;
    }

    private void SetLaunchState(string title, string status, StatusKind kind, bool enabled)
    {
        LaunchTitleText.Text = title;
        LaunchStatusText.Text = status;
        LaunchStatusDot.Foreground = BrushForStatus(kind);
        LaunchButton.IsEnabled = enabled;
    }

    private static SolidColorBrush BrushForStatus(StatusKind kind) =>
        kind switch
        {
            StatusKind.Success => StatusSuccessBrush,
            StatusKind.Warning => StatusWarningBrush,
            StatusKind.Danger => StatusDangerBrush,
            StatusKind.Muted => StatusMutedBrush,
            _ => StatusAccentBrush
        };

    private static string FeatureDisplayName(string feature) =>
        feature switch
        {
            "health" => "Infinite Health",
            "stamina" => "Infinite Stamina",
            "damage" => "Player Damage",
            "blockDamage" => "Mining Power",
            "xp" => "XP Gain",
            "speed" => "Movement Speed",
            "jump" => "Jump Height",
            "noDurability" => "Infinite Durability",
            "durability" => "Durability Loss Rate",
            "itemAmount" => "[Slot 1] Minimum Stack Size",
            _ => feature
        };

    private static string? LastMeaningfulLine(string text)
    {
        return text
            .Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .LastOrDefault();
    }

    private static decimal ReadDecimal(TextBox box, decimal fallback)
    {
        var text = StripMultiplierSuffix(box.Text);
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ||
            decimal.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
        {
            return value;
        }

        return fallback;
    }

    private int ReadItemAmount()
    {
        var value = decimal.Round(ReadDecimal(ItemAmountValue, 99M), 0, MidpointRounding.AwayFromZero);
        if (value < 1M)
        {
            value = 1M;
        }
        else if (value > 9999M)
        {
            value = 9999M;
        }

        return (int)value;
    }

    private static string StripMultiplierSuffix(string text) =>
        text.Trim().TrimEnd(MultiplierSuffix, 'x', 'X').Trim();

    private static bool IsMultiplierFeature(string feature) =>
        feature is "damage" or "blockDamage" or "xp" or "speed" or "jump" or "durability";

    private static string FormatDecimal(decimal value, int decimals)
    {
        if (decimals <= 0)
        {
            return decimal.Round(value).ToString("0", CultureInfo.InvariantCulture);
        }

        return value.ToString("0." + new string('#', decimals), CultureInfo.InvariantCulture);
    }

    private static string FormatMultiplier(decimal value, int decimals) =>
        $"{FormatDecimal(value, decimals)}{MultiplierSuffix}";

    private static SolidColorBrush CreateFrozenBrush(byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromRgb(red, green, blue));
        brush.Freeze();
        return brush;
    }

    private sealed record FeaturePinBinding(
        Border Row,
        StackPanel OriginPanel,
        int OriginalIndex,
        ToggleButton PinButton);

    private enum StatusKind
    {
        Accent,
        Success,
        Warning,
        Danger,
        Muted
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr IconHandle;
        public int IconIndex;
        public uint Attributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr iconHandle);

    private static IntPtr GetShellFileInfo(
        string path,
        uint fileAttributes,
        ref ShellFileInfo fileInfo,
        uint fileInfoSize,
        uint flags) =>
        SHGetFileInfo(path, fileAttributes, ref fileInfo, fileInfoSize, flags);
}

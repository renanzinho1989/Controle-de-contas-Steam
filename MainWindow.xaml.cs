using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Data;
using System.Windows.Threading;

namespace ControleDeContasSteam;

public partial class MainWindow : Window
{
    private static readonly CultureInfo BrazilianCulture = new("pt-BR");
    private static readonly Dictionary<string, string[]> DropWeaponsByCategory = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Pistol"] =
        [
            "USP-S",
            "P2000",
            "Five-SeveN",
            "CZ75-Auto",
            "Desert Eagle",
            "Glock-18",
            "Tec-9",
            "Dual Berettas",
            "P250",
            "R8 Revolver"
        ],
        ["Rifle"] =
        [
            "M4A4",
            "M4A1-S",
            "FAMAS",
            "AUG",
            "AK-47",
            "Galil AR",
            "SG 553",
            "AWP",
            "SSG 08",
            "SCAR-20",
            "G3SG1"
        ],
        ["Submetralhadoras"] =
        [
            "MAC-10",
            "MP9",
            "MP7",
            "MP5-SD",
            "UMP-45",
            "P90",
            "PP-Bizon"
        ],
        ["Pesadas"] =
        [
            "Nova",
            "XM1014",
            "Sawed-Off",
            "MAG-7",
            "M249",
            "Negev"
        ],
        ["Outro"] =
        [
            "Zeus x27",
            "Facas",
            "Caixa",
            "Terminal"
        ]
    };
    private static readonly HashSet<string> ItemsWithoutCondition = new(StringComparer.OrdinalIgnoreCase)
    {
        "Caixa",
        "Terminal"
    };

    private readonly string _dataFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ControleDeContasSteam");

    private readonly JsonSerializerOptions _jsonOptions = new() { WriteIndented = true };
    private readonly DispatcherTimer _banDecayTimer = new() { Interval = TimeSpan.FromMinutes(1) };
    private AppDatabase _database = new();
    private SteamAccount? _editingAccount;
    private SteamAccount? _selectedDropsAccount;
    private AccountDrop? _editingDrop;
    private SteamAccount? _lastMarkedDropAccount;
    private Action? _pendingConfirmationAction;
    private bool _changingPinFocus;
    private bool _syncingAccountPassword;
    private bool _isAccountPasswordVisible;
    private bool _syncingNewAccountPassword;
    private bool _isNewAccountPasswordVisible;

    public ObservableCollection<SteamAccount> Accounts { get; } = new();
    public ObservableCollection<SteamAccount> PendingDropAccounts { get; } = new();
    public ObservableCollection<AccountDrop> AccountDrops { get; } = new();
    public ObservableCollection<DropAccountSummary> DropAccountSummaries { get; } = new();
    public ObservableCollection<AccountDrop> SelectedAccountDrops { get; } = new();

    private string DataFile => Path.Combine(_dataFolder, "dados.json");

    public MainWindow()
    {
        InitializeComponent();
        DropCategoryBox.SelectionChanged += DropCategoryBox_SelectionChanged;
        DropWeaponBox.SelectionChanged += DropWeaponBox_SelectionChanged;
        InitializeDropWeaponSelectors();
        DataContext = this;
        LoadData();
        UpdateDashboard();
        SelectSection("accounts");
        StartBanDecayTimer();
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        PinBox1.Focus();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximizeRestore();
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // DragMove can throw if the mouse state changes mid-drag.
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeRestoreButton_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximizeRestore();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        MaximizeRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void ToggleMaximizeRestore()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void LoadData()
    {
        Directory.CreateDirectory(_dataFolder);
        var fileWasMissing = !File.Exists(DataFile);

        if (!fileWasMissing)
        {
            try
            {
                var json = File.ReadAllText(DataFile);
                _database = JsonSerializer.Deserialize<AppDatabase>(json, _jsonOptions) ?? new AppDatabase();
            }
            catch
            {
                _database = CreateSeedDatabase();
            }
        }
        else
        {
            _database = CreateSeedDatabase();
        }

        if (string.IsNullOrWhiteSpace(_database.Pin))
        {
            _database.Pin = "1234";
        }

        Accounts.Clear();
        foreach (var account in _database.Accounts)
        {
            if (account.Id == Guid.Empty)
            {
                account.Id = Guid.NewGuid();
            }

            Accounts.Add(account);
        }

        var accountIds = Accounts.Select(account => account.Id).ToHashSet();

        AccountDrops.Clear();
        foreach (var drop in _database.Drops)
        {
            if (drop.Id == Guid.Empty)
            {
                drop.Id = Guid.NewGuid();
            }

            if (drop.AccountId == Guid.Empty || !accountIds.Contains(drop.AccountId))
            {
                continue;
            }

            if (drop.AddedAt == default)
            {
                drop.AddedAt = DateTime.Now;
            }

            AccountDrops.Add(drop);
        }

        ApplyDailyBanDecay();
        SaveData(fileWasMissing ? "Base inicial criada." : null);
    }

    private static AppDatabase CreateSeedDatabase()
    {
        return new AppDatabase
        {
            Pin = "1234",
            LastBanDecayDate = GetBanCycleDate(DateTime.Now),
            LastBanCheckTime = DateTime.Now
        };
    }

    private void StartBanDecayTimer()
    {
        _banDecayTimer.Tick += (_, _) =>
        {
            var changed = ApplyDailyBanDecay();
            UpdateDashboard();

            if (changed)
            {
                SaveData();
            }
        };
        _banDecayTimer.Start();
    }

    private bool ApplyDailyBanDecay()
    {
        var now = DateTime.Now;
        var currentBanCycleDate = GetBanCycleDate(now);
        _database.LastBanCheckTime = now;
        var lastBanDecayDate = _database.LastBanDecayDate == default
            ? GetBanCycleDate(_database.LastUpdated)
            : _database.LastBanDecayDate.Date;

        if (lastBanDecayDate == default)
        {
            lastBanDecayDate = currentBanCycleDate;
        }

        if (lastBanDecayDate >= currentBanCycleDate)
        {
            if (_database.LastBanDecayDate.Date == currentBanCycleDate)
            {
                return false;
            }

            _database.LastBanDecayDate = currentBanCycleDate;
            return true;
        }

        var elapsedDays = (currentBanCycleDate - lastBanDecayDate).Days;
        var changed = false;

        foreach (var account in Accounts.Where(account => account.Ban > 0))
        {
            var newBan = Math.Max(0, account.Ban - elapsedDays);
            if (newBan == account.Ban)
            {
                continue;
            }

            account.Ban = newBan;
            changed = true;
        }

        _database.LastBanDecayDate = currentBanCycleDate;
        return changed;
    }

    private static DateTime GetBanCycleDate(DateTime dateTime)
    {
        var updateTime = new TimeSpan(6, 0, 0);
        return dateTime.TimeOfDay >= updateTime
            ? dateTime.Date
            : dateTime.Date.AddDays(-1);
    }

    private void SaveData(string? status = null)
    {
        _database.Accounts = Accounts.ToList();
        _database.Drops = AccountDrops.ToList();
        _database.LastUpdated = DateTime.Now;
        Directory.CreateDirectory(_dataFolder);
        File.WriteAllText(DataFile, JsonSerializer.Serialize(_database, _jsonOptions));

        if (!string.IsNullOrWhiteSpace(status))
        {
            SetStatusText(status);
        }
    }

    private void EnterButton_Click(object sender, RoutedEventArgs e)
    {
        TryUnlock();
    }

    private void TryUnlock()
    {
        var typedPin = PinBox1.Password + PinBox2.Password + PinBox3.Password + PinBox4.Password;
        if (typedPin == _database.Pin)
        {
            PinErrorText.Text = "";
            LoginView.Visibility = Visibility.Collapsed;
            AppView.Visibility = Visibility.Visible;
            SetStatus("Acesso liberado.");
            return;
        }

        PinErrorText.Text = "PIN incorreto. O PIN inicial e 1234.";
        ClearPinBoxes();
        PinBox1.Focus();
    }

    private void PinBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character));
    }

    private void NumericTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character));
    }

    private void NumericTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var pastedText = e.DataObject.GetData(DataFormats.Text) as string ?? "";
        if (pastedText.Any(character => !char.IsDigit(character)))
        {
            e.CancelCommand();
        }
    }

    private void AccountPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingAccountPassword)
        {
            return;
        }

        _syncingAccountPassword = true;
        AccountPasswordVisibleBox.Text = AccountPasswordBox.Password;
        _syncingAccountPassword = false;
    }

    private void AccountPasswordVisibleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingAccountPassword)
        {
            return;
        }

        _syncingAccountPassword = true;
        AccountPasswordBox.Password = AccountPasswordVisibleBox.Text;
        _syncingAccountPassword = false;
    }

    private void ToggleAccountPasswordVisibility_Click(object sender, RoutedEventArgs e)
    {
        SetAccountPasswordVisibility(!_isAccountPasswordVisible);
    }

    private void SetAccountPassword(string password)
    {
        _syncingAccountPassword = true;
        AccountPasswordBox.Password = password;
        AccountPasswordVisibleBox.Text = password;
        _syncingAccountPassword = false;
    }

    private void SetAccountPasswordVisibility(bool isVisible)
    {
        _isAccountPasswordVisible = isVisible;
        AccountPasswordVisibleBox.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        AccountPasswordBox.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
        AccountPasswordVisibilityButton.Content = isVisible ? "\uE8F8" : "\uE890";
        AccountPasswordVisibilityButton.ToolTip = isVisible ? "Ocultar senha" : "Mostrar senha";

        if (isVisible)
        {
            AccountPasswordVisibleBox.Focus();
            AccountPasswordVisibleBox.CaretIndex = AccountPasswordVisibleBox.Text.Length;
            return;
        }

        AccountPasswordBox.Focus();
    }

    private void NewAccountPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingNewAccountPassword)
        {
            return;
        }

        _syncingNewAccountPassword = true;
        NewAccountPasswordVisibleBox.Text = NewAccountPasswordBox.Password;
        _syncingNewAccountPassword = false;
    }

    private void NewAccountPasswordVisibleBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncingNewAccountPassword)
        {
            return;
        }

        _syncingNewAccountPassword = true;
        NewAccountPasswordBox.Password = NewAccountPasswordVisibleBox.Text;
        _syncingNewAccountPassword = false;
    }

    private void ToggleNewAccountPasswordVisibility_Click(object sender, RoutedEventArgs e)
    {
        SetNewAccountPasswordVisibility(!_isNewAccountPasswordVisible);
    }

    private void SetNewAccountPassword(string password)
    {
        _syncingNewAccountPassword = true;
        NewAccountPasswordBox.Password = password;
        NewAccountPasswordVisibleBox.Text = password;
        _syncingNewAccountPassword = false;
    }

    private void SetNewAccountPasswordVisibility(bool isVisible)
    {
        _isNewAccountPasswordVisible = isVisible;
        NewAccountPasswordVisibleBox.Visibility = isVisible ? Visibility.Visible : Visibility.Collapsed;
        NewAccountPasswordBox.Visibility = isVisible ? Visibility.Collapsed : Visibility.Visible;
        NewAccountPasswordVisibilityButton.Content = isVisible ? "\uE8F8" : "\uE890";
        NewAccountPasswordVisibilityButton.ToolTip = isVisible ? "Ocultar senha" : "Mostrar senha";

        if (isVisible)
        {
            NewAccountPasswordVisibleBox.Focus();
            NewAccountPasswordVisibleBox.CaretIndex = NewAccountPasswordVisibleBox.Text.Length;
            return;
        }

        NewAccountPasswordBox.Focus();
    }

    private void PinBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_changingPinFocus || sender is not PasswordBox box || box.Password.Length == 0)
        {
            return;
        }

        _changingPinFocus = true;
        MoveToNextPinBox(box);
        _changingPinFocus = false;
    }

    private void PinBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryUnlock();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Back && sender is PasswordBox box && box.Password.Length == 0)
        {
            MoveToPreviousPinBox(box);
        }
    }

    private void MoveToNextPinBox(PasswordBox box)
    {
        if (box == PinBox1) PinBox2.Focus();
        else if (box == PinBox2) PinBox3.Focus();
        else if (box == PinBox3) PinBox4.Focus();
        else if (box == PinBox4 && (PinBox1.Password + PinBox2.Password + PinBox3.Password + PinBox4.Password).Length == 4) TryUnlock();
    }

    private void MoveToPreviousPinBox(PasswordBox box)
    {
        if (box == PinBox4) PinBox3.Focus();
        else if (box == PinBox3) PinBox2.Focus();
        else if (box == PinBox2) PinBox1.Focus();
    }

    private void ClearPinBoxes()
    {
        PinBox1.Clear();
        PinBox2.Clear();
        PinBox3.Clear();
        PinBox4.Clear();
    }

    private void ShowPinHelpButton_Click(object sender, RoutedEventArgs e)
    {
        PinErrorText.Text = "Entre com o PIN e altere em Configuracoes. PIN inicial: 1234.";
    }

    private void NavButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button && button.Tag is string section)
        {
            SelectSection(section);
        }
    }

    private void SelectSection(string section)
    {
        ListPanel.Visibility = section == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        DropsPanel.Visibility = section == "drops" ? Visibility.Visible : Visibility.Collapsed;
        NewAccountPanel.Visibility = section == "new-account" ? Visibility.Visible : Visibility.Collapsed;
        DropsByAccountPanel.Visibility = section == "drops-by-account" ? Visibility.Visible : Visibility.Collapsed;
        ReportsPanel.Visibility = section == "reports" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = section == "settings" ? Visibility.Visible : Visibility.Collapsed;

        ResetNavButtons();
        var view = CollectionViewSource.GetDefaultView(Accounts);
        view.SortDescriptions.Clear();

        switch (section)
        {
            case "drops":
                SectionTitleText.Text = "Drops";
                SectionSubtitleText.Text = "Acompanhe quais contas ainda precisam de drop.";
                ActivateNavButton(NavDropsButton);
                view.SortDescriptions.Add(new SortDescription(nameof(SteamAccount.DropActive), ListSortDirection.Ascending));
                break;
            case "new-account":
                SectionTitleText.Text = "Nova Conta";
                SectionSubtitleText.Text = "Cadastre uma nova conta Steam.";
                ActivateNavButton(NavRankingButton);
                ClearNewAccountForm();
                break;
            case "reports":
            case "drops-by-account":
                SectionTitleText.Text = "Profit por Conta";
                SectionSubtitleText.Text = "Gerencie o profit dos drops adicionados para cada conta.";
                ActivateNavButton(NavReportsButton);
                RefreshDropsByAccountView();
                break;
            case "settings":
                SectionTitleText.Text = "Configuracoes";
                SectionSubtitleText.Text = "Ajuste o PIN de acesso e confira o arquivo de dados.";
                ActivateNavButton(NavSettingsButton);
                break;
            default:
                SectionTitleText.Text = "Contas Steam";
                SectionSubtitleText.Text = "Vis\u00E3o geral das contas cadastradas.";
                ActivateNavButton(NavAccountsButton);
                break;
        }

        view.Refresh();
    }

    private void ResetNavButtons()
    {
        var transparent = Brushes.Transparent;
        var muted = (Brush)FindResource("Muted");
        NavAccountsButton.Background = transparent;
        NavDropsButton.Background = transparent;
        NavRankingButton.Background = transparent;
        NavReportsButton.Background = transparent;
        NavSettingsButton.Background = transparent;
        NavAccountsButton.BorderBrush = transparent;
        NavDropsButton.BorderBrush = transparent;
        NavRankingButton.BorderBrush = transparent;
        NavReportsButton.BorderBrush = transparent;
        NavSettingsButton.BorderBrush = transparent;
        NavAccountsButton.Foreground = muted;
        NavDropsButton.Foreground = muted;
        NavRankingButton.Foreground = muted;
        NavReportsButton.Foreground = muted;
        NavSettingsButton.Foreground = muted;
    }

    private void ActivateNavButton(Button button)
    {
        button.Background = (Brush)FindResource("SelectedNav");
        button.BorderBrush = (Brush)FindResource("Blue");
        button.Foreground = (Brush)FindResource("Text");
    }

    private void ClearNewAccountForm()
    {
        NewAccountErrorText.Text = "";
        NewAccountNameBox.Text = "";
        SetNewAccountPassword("");
        SetNewAccountPasswordVisibility(false);
        NewAccountUrlBox.Text = "https://steamcommunity.com/profiles/";
        NewAccountVictoriesBox.Text = "0";
        NewAccountBanBox.Text = "0";
        NewAccountRankBox.Text = "0";
        NewAccountNameBox.Focus();
    }

    private void CancelNewAccountPageButton_Click(object sender, RoutedEventArgs e)
    {
        ClearNewAccountForm();
        SelectSection("accounts");
    }

    private void SaveNewAccountPageButton_Click(object sender, RoutedEventArgs e)
    {
        var name = NewAccountNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            NewAccountErrorText.Text = "Informe o nome da conta.";
            return;
        }

        var account = new SteamAccount
        {
            Name = name,
            Password = NewAccountPasswordBox.Password,
            Url = NormalizeUrl(NewAccountUrlBox.Text),
            Victories = ParseNumber(NewAccountVictoriesBox.Text),
            Ban = ParseNumber(NewAccountBanBox.Text),
            RankCS2 = RankFormatter.Normalize(NewAccountRankBox.Text),
            DropActive = false
        };

        Accounts.Add(account);
        UpdateDashboard();
        SaveData("Conta adicionada.");
        SelectSection("accounts");
    }

    private void OpenAddAccountButton_Click(object sender, RoutedEventArgs e)
    {
        _editingAccount = null;
        ModalTitleText.Text = "Adicionar Conta";
        ModalErrorText.Text = "";
        AccountNameBox.Text = "";
        SetAccountPassword("");
        SetAccountPasswordVisibility(false);
        AccountUrlBox.Text = "https://steamcommunity.com/id/";
        AccountVictoriesBox.Text = "0";
        AccountBanBox.Text = "0";
        AccountRankBox.Text = "0";
        ModalOverlay.Visibility = Visibility.Visible;
        AccountNameBox.Focus();
    }

    private void CloseModalButton_Click(object sender, RoutedEventArgs e)
    {
        ModalOverlay.Visibility = Visibility.Collapsed;
        _editingAccount = null;
    }

    private void SaveAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var name = AccountNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ModalErrorText.Text = "Informe o nome da conta.";
            return;
        }

        var account = _editingAccount ?? new SteamAccount();
        account.Name = name;
        account.Password = AccountPasswordBox.Password;
        account.Url = NormalizeUrl(AccountUrlBox.Text);
        account.Victories = ParseNumber(AccountVictoriesBox.Text);
        account.Ban = ParseNumber(AccountBanBox.Text);
        account.RankCS2 = ReadSelectedRank();

        if (_editingAccount is null)
        {
            Accounts.Add(account);
        }

        UpdateDashboard();
        SaveData(_editingAccount is null ? "Conta adicionada." : "Conta atualizada.");
        ModalOverlay.Visibility = Visibility.Collapsed;
        _editingAccount = null;
    }

    private static int ParseNumber(string text)
    {
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? Math.Max(0, value) : 0;
    }

    private static string NormalizeUrl(string url)
    {
        url = url.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            return "";
        }

        return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
               url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            ? url
            : "https://" + url;
    }

    private string ReadSelectedRank()
    {
        return RankFormatter.Normalize(AccountRankBox.Text.Trim());
    }

    private void SetSelectedRank(string rank)
    {
        var numericRank = RankFormatter.GetNumericValue(rank);
        AccountRankBox.Text = numericRank <= 0 ? "0" : numericRank.ToString(CultureInfo.InvariantCulture);
    }

    private void EditAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SteamAccount account)
        {
            return;
        }

        _editingAccount = account;
        ModalTitleText.Text = "Editar Conta";
        ModalErrorText.Text = "";
        AccountNameBox.Text = account.Name;
        SetAccountPassword(account.Password);
        SetAccountPasswordVisibility(false);
        AccountUrlBox.Text = account.Url;
        AccountVictoriesBox.Text = account.Victories.ToString(CultureInfo.InvariantCulture);
        AccountBanBox.Text = account.Ban.ToString(CultureInfo.InvariantCulture);
        SetSelectedRank(account.RankCS2);
        ModalOverlay.Visibility = Visibility.Visible;
        AccountNameBox.Focus();
    }

    private void DeleteAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SteamAccount account)
        {
            return;
        }

        ShowDeleteConfirmation("Excluir conta", $"Excluir a conta {account.Name}?", () =>
        {
            Accounts.Remove(account);
            foreach (var drop in AccountDrops.Where(drop => drop.AccountId == account.Id).ToList())
            {
                AccountDrops.Remove(drop);
            }

            if (_selectedDropsAccount == account)
            {
                _selectedDropsAccount = null;
            }

            UpdateDashboard();
            SaveData("Conta excluida.");
        });
    }

    private void ShowDeleteConfirmation(string title, string message, Action onConfirm)
    {
        _pendingConfirmationAction = onConfirm;
        ConfirmTitleText.Text = title;
        ConfirmMessageText.Text = message;
        ConfirmOverlay.Visibility = Visibility.Visible;
    }

    private void CloseConfirmDialogButton_Click(object sender, RoutedEventArgs e)
    {
        CloseConfirmDialog();
    }

    private void ConfirmDeleteButton_Click(object sender, RoutedEventArgs e)
    {
        var action = _pendingConfirmationAction;
        CloseConfirmDialog();
        action?.Invoke();
    }

    private void CloseConfirmDialog()
    {
        ConfirmOverlay.Visibility = Visibility.Collapsed;
        _pendingConfirmationAction = null;
    }

    private void CopyPasswordButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SteamAccount account)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(account.Password))
        {
            SetStatus("Essa conta nao tem senha cadastrada.");
            return;
        }

        Clipboard.SetText(account.Password);
        SetStatus($"Senha de {account.Name} copiada.");
    }

    private void OpenUrlButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SteamAccount account)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(account.Url))
        {
            SetStatus("Essa conta nao tem URL cadastrada.");
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(account.Url) { UseShellExecute = true });
            SetStatus($"Abrindo perfil de {account.Name}.");
        }
        catch
        {
            SetStatus("Nao foi possivel abrir a URL.");
        }
    }

    private void DropCheckBox_Click(object sender, RoutedEventArgs e)
    {
        UpdateDashboard();
        SaveData("Status de drop atualizado.");
    }

    private void MarkDropButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not SteamAccount account)
        {
            return;
        }

        account.DropActive = true;
        _lastMarkedDropAccount = account;
        UndoDropButton.Visibility = Visibility.Visible;
        UpdateDashboard();
        SaveData($"Drop marcado para {account.Name}.");
    }

    private void UndoLastDropButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastMarkedDropAccount is null)
        {
            UndoDropButton.Visibility = Visibility.Collapsed;
            return;
        }

        var account = _lastMarkedDropAccount;
        account.DropActive = false;
        _lastMarkedDropAccount = null;
        UndoDropButton.Visibility = Visibility.Collapsed;
        UpdateDashboard();
        SaveData($"Drop desfeito para {account.Name}.");
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        _lastMarkedDropAccount = null;
        UndoDropButton.Visibility = Visibility.Collapsed;
        LoadData();
        UpdateDashboard();
        SelectSection("accounts");
        SetStatus("Dados recarregados.");
    }

    private void ChangePinButton_Click(object sender, RoutedEventArgs e)
    {
        var current = CurrentPinBox.Password.Trim();
        var next = NewPinBox.Password.Trim();
        var confirm = ConfirmPinBox.Password.Trim();

        if (current != _database.Pin)
        {
            SettingsMessageText.Foreground = (Brush)FindResource("Red");
            SettingsMessageText.Text = "PIN atual incorreto.";
            return;
        }

        if (next.Length != 4 || next.Any(character => !char.IsDigit(character)))
        {
            SettingsMessageText.Foreground = (Brush)FindResource("Red");
            SettingsMessageText.Text = "O novo PIN precisa ter 4 digitos.";
            return;
        }

        if (next != confirm)
        {
            SettingsMessageText.Foreground = (Brush)FindResource("Red");
            SettingsMessageText.Text = "A confirmacao nao confere.";
            return;
        }

        _database.Pin = next;
        SaveData("PIN alterado.");
        CurrentPinBox.Clear();
        NewPinBox.Clear();
        ConfirmPinBox.Clear();
        SettingsMessageText.Foreground = (Brush)FindResource("Green");
        SettingsMessageText.Text = "PIN alterado com sucesso.";
    }

    private void UpdateDashboard()
    {
        var total = Accounts.Count;
        var missingDrops = Accounts.Count(account => !account.DropActive);
        var activeDrops = Accounts.Count(account => account.DropActive);
        var bestVictory = Accounts.Count == 0 ? 0 : Accounts.Max(account => account.Victories);
        var bannedAccounts = Accounts.Count(account => account.Ban > 0);
        var bestRank = Accounts
            .Select(account => RankFormatter.GetNumericValue(account.RankCS2))
            .DefaultIfEmpty(0)
            .Max();
        var nextReset = GetNextReset();

        SyncPendingDropAccounts();
        TotalAccountsText.Text = total.ToString(CultureInfo.InvariantCulture);
        BestVictoryText.Text = bestVictory.ToString(CultureInfo.InvariantCulture);
        BannedAccountsText.Text = bannedAccounts.ToString(CultureInfo.InvariantCulture);
        BestRankText.Text = bestRank <= 0
            ? "Sem Premier"
            : bestRank.ToString("N0", new CultureInfo("pt-BR"));
        DropsPendingPageText.Text = missingDrops.ToString(CultureInfo.InvariantCulture);
        DropsActivePageText.Text = activeDrops.ToString(CultureInfo.InvariantCulture);
        DropsNextResetPageText.Text = nextReset.ToString("dddd, HH'h'", new CultureInfo("pt-BR"));
        DropsFooterText.Text = $"{missingDrops} contas sem drop";
        TableFooterText.Text = $"{total} contas | {missingDrops} drops faltando";
        UpdateBanFooterText();
        LoginFooterText.Text = $"Sistema protegido - ultima atualizacao: hoje, {DateTime.Now:HH:mm}";
        if (DropsByAccountPanel.Visibility == Visibility.Visible)
        {
            RefreshDropsByAccountView();
        }
    }

    private void SyncPendingDropAccounts()
    {
        PendingDropAccounts.Clear();

        foreach (var account in Accounts.Where(account => !account.DropActive))
        {
            PendingDropAccounts.Add(account);
        }
    }

    private static DateTime GetNextReset()
    {
        var now = DateTime.Now;
        var days = ((int)DayOfWeek.Tuesday - (int)now.DayOfWeek + 7) % 7;
        var reset = now.Date.AddDays(days).AddHours(23);
        return reset <= now ? reset.AddDays(7) : reset;
    }

    private void SetStatus(string message)
    {
        SetStatusText(message);
        UpdateDashboard();
    }

    private void SetStatusText(string message)
    {
        StatusText.Text = message;
        DropsStatusText.Text = message;
        DropByAccountStatusText.Text = message;
    }

    private void UpdateBanFooterText()
    {
        var checkTime = _database.LastBanCheckTime == default ? DateTime.Now : _database.LastBanCheckTime;
        var text = checkTime.Date == DateTime.Today
            ? $"Bans conferidos hoje as {checkTime:HH:mm} | atualiza as 06:00"
            : $"Bans conferidos em {checkTime:dd/MM} as {checkTime:HH:mm} | atualiza as 06:00";

        BanUpdateFooterText.Text = text;
        DropsBanUpdateFooterText.Text = text;
    }

    private void RefreshDropsByAccountView()
    {
        var search = DropAccountSearchBox.Text.Trim();
        var visibleAccounts = Accounts
            .Where(account => string.IsNullOrWhiteSpace(search) || account.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (_selectedDropsAccount is null || !Accounts.Contains(_selectedDropsAccount) || !visibleAccounts.Contains(_selectedDropsAccount))
        {
            _selectedDropsAccount = visibleAccounts.FirstOrDefault() ?? Accounts.FirstOrDefault();
        }

        var dropCounts = AccountDrops
            .GroupBy(drop => drop.AccountId)
            .ToDictionary(group => group.Key, group => group.Count());

        DropAccountSummaries.Clear();
        foreach (var account in visibleAccounts)
        {
            DropAccountSummaries.Add(new DropAccountSummary(
                account,
                dropCounts.TryGetValue(account.Id, out var dropCount) ? dropCount : 0,
                _selectedDropsAccount?.Id == account.Id));
        }

        SelectedAccountDrops.Clear();
        if (_selectedDropsAccount is not null)
        {
            foreach (var drop in AccountDrops
                         .Where(drop => drop.AccountId == _selectedDropsAccount.Id)
                         .OrderByDescending(drop => drop.AddedAt))
            {
                SelectedAccountDrops.Add(drop);
            }
        }

        var totalDrops = SelectedAccountDrops.Count;
        var totalValue = SelectedAccountDrops.Sum(drop => drop.Value);
        var averageValue = totalDrops == 0 ? 0 : totalValue / totalDrops;

        DropStatsTotalText.Text = totalDrops.ToString(CultureInfo.InvariantCulture);
        DropStatsValueText.Text = FormatCurrency(totalValue);
        DropStatsAverageText.Text = FormatCurrency(averageValue);
        SelectedDropAccountNameText.Text = _selectedDropsAccount?.Name ?? "Nenhuma conta selecionada";
        SelectedDropAccountCountText.Text = $"{SelectedAccountDrops.Count} drops adicionados";
        ShowingDropCountText.Text = $"Mostrando {SelectedAccountDrops.Count} drops";
        SelectedAccountTotalText.Text = $"Total da conta: {FormatCurrency(totalValue)}";
        AddDropButton.IsEnabled = _selectedDropsAccount is not null;
    }

    private void DropAccountSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshDropsByAccountView();
    }

    private void SelectDropAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not DropAccountSummary summary)
        {
            return;
        }

        _selectedDropsAccount = summary.Account;
        RefreshDropsByAccountView();
    }

    private void OpenAddDropButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDropsAccount is null)
        {
            SetStatus("Selecione uma conta para adicionar drop.");
            return;
        }

        _editingDrop = null;
        DropModalTitleText.Text = "Adicionar Drop";
        DropModalSubtitleText.Text = "Cadastre um novo drop para a conta selecionada.";
        DropModalErrorText.Text = "";
        DropAccountBox.Text = _selectedDropsAccount.Name;
        SelectDropWeapon("AK-47");
        DropNameBox.Text = "";
        SetComboBoxSelection(DropConditionBox, "Nova de FÃ¡brica");
        UpdateDropConditionForSelectedItem();
        DropValueBox.Text = "0,00";
        SaveDropButtonText.Text = "Adicionar Drop";
        DropModalOverlay.Visibility = Visibility.Visible;
        DropNameBox.Focus();
    }

    private void EditDropButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AccountDrop drop)
        {
            return;
        }

        _editingDrop = drop;
        var account = Accounts.FirstOrDefault(item => item.Id == drop.AccountId);
        DropModalTitleText.Text = "Editar Drop";
        DropModalSubtitleText.Text = "Atualize os dados do drop selecionado.";
        DropModalErrorText.Text = "";
        DropAccountBox.Text = account?.Name ?? "";
        SelectDropWeapon(drop.Weapon);
        DropNameBox.Text = drop.Name;
        SetComboBoxSelection(DropConditionBox, string.IsNullOrWhiteSpace(drop.Condition) ? "None" : drop.Condition);
        UpdateDropConditionForSelectedItem();
        DropValueBox.Text = drop.Value.ToString("N2", BrazilianCulture);
        SaveDropButtonText.Text = "Salvar Drop";
        DropModalOverlay.Visibility = Visibility.Visible;
        DropNameBox.Focus();
    }

    private void DeleteDropButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AccountDrop drop)
        {
            return;
        }

        ShowDeleteConfirmation("Excluir drop", $"Excluir o drop {drop.Weapon} {drop.Name}?", () =>
        {
            AccountDrops.Remove(drop);
            RefreshDropsByAccountView();
            SaveData("Drop excluido.");
        });
    }

    private void CloseDropModalButton_Click(object sender, RoutedEventArgs e)
    {
        DropModalOverlay.Visibility = Visibility.Collapsed;
        _editingDrop = null;
    }

    private void InitializeDropWeaponSelectors()
    {
        SetComboBoxSelection(DropCategoryBox, "Rifle");
        PopulateDropWeapons("Rifle", "AK-47");
    }

    private void DropCategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        PopulateDropWeapons(ReadComboBoxText(DropCategoryBox));
    }

    private void DropWeaponBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDropConditionForSelectedItem();
    }

    private void SelectDropWeapon(string weapon)
    {
        var category = FindDropWeaponCategory(weapon);
        SetComboBoxSelection(DropCategoryBox, category);
        PopulateDropWeapons(category, weapon);
    }

    private void PopulateDropWeapons(string category, string? selectedWeapon = null)
    {
        if (!DropWeaponsByCategory.TryGetValue(category, out var weapons))
        {
            category = "Outro";
            weapons = DropWeaponsByCategory[category];
            SetComboBoxSelection(DropCategoryBox, category);
        }

        DropWeaponBox.Items.Clear();
        foreach (var weapon in weapons)
        {
            DropWeaponBox.Items.Add(new ComboBoxItem { Content = weapon });
        }

        if (!string.IsNullOrWhiteSpace(selectedWeapon) &&
            !weapons.Any(weapon => string.Equals(weapon, selectedWeapon, StringComparison.OrdinalIgnoreCase)))
        {
            DropWeaponBox.Items.Add(new ComboBoxItem { Content = selectedWeapon });
        }

        if (!string.IsNullOrWhiteSpace(selectedWeapon))
        {
            SetComboBoxSelection(DropWeaponBox, selectedWeapon);
            UpdateDropConditionForSelectedItem();
            return;
        }

        DropWeaponBox.SelectedIndex = DropWeaponBox.Items.Count > 0 ? 0 : -1;
        UpdateDropConditionForSelectedItem();
    }

    private static string FindDropWeaponCategory(string weapon)
    {
        foreach (var pair in DropWeaponsByCategory)
        {
            if (pair.Value.Any(item => string.Equals(item, weapon, StringComparison.OrdinalIgnoreCase)))
            {
                return pair.Key;
            }
        }

        return "Outro";
    }

    private void UpdateDropConditionForSelectedItem()
    {
        var selectedItem = ReadComboBoxText(DropWeaponBox);
        var supportsCondition = ItemSupportsCondition(selectedItem);

        DropConditionBox.IsEnabled = supportsCondition;
        DropConditionBox.Opacity = supportsCondition ? 1 : 0.72;

        if (!supportsCondition)
        {
            SetComboBoxSelection(DropConditionBox, "None");
            return;
        }

        var selectedCondition = ReadComboBoxText(DropConditionBox);
        if (string.IsNullOrWhiteSpace(selectedCondition) || string.Equals(selectedCondition, "None", StringComparison.OrdinalIgnoreCase))
        {
            SetComboBoxSelection(DropConditionBox, "Nova de FÃ¡brica");
        }
    }

    private static bool ItemSupportsCondition(string item)
    {
        return !string.IsNullOrWhiteSpace(item) && !ItemsWithoutCondition.Contains(item);
    }

    private void SaveDropButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedDropsAccount is null && _editingDrop is null)
        {
            DropModalErrorText.Text = "Selecione uma conta.";
            return;
        }

        var weapon = ReadComboBoxText(DropWeaponBox);
        var name = DropNameBox.Text.Trim();
        var condition = ReadComboBoxText(DropConditionBox);

        if (string.IsNullOrWhiteSpace(weapon))
        {
            DropModalErrorText.Text = "Selecione o item.";
            return;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            DropModalErrorText.Text = "Informe o nome do item.";
            return;
        }

        if (!TryParseMoney(DropValueBox.Text, out var value))
        {
            DropModalErrorText.Text = "Informe um valor valido.";
            return;
        }

        if (!ItemSupportsCondition(weapon))
        {
            condition = "None";
        }

        var drop = _editingDrop ?? new AccountDrop
        {
            Id = Guid.NewGuid(),
            AccountId = _selectedDropsAccount!.Id,
            AddedAt = DateTime.Now
        };

        drop.Weapon = weapon;
        drop.Name = name;
        drop.Condition = condition;
        drop.Value = value;

        if (_editingDrop is null)
        {
            AccountDrops.Add(drop);
        }

        DropModalOverlay.Visibility = Visibility.Collapsed;
        _editingDrop = null;
        RefreshDropsByAccountView();
        SaveData("Drop salvo.");
    }

    private void MoneyTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character) && character != ',' && character != '.');
    }

    private void MoneyTextBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(DataFormats.Text))
        {
            e.CancelCommand();
            return;
        }

        var pastedText = e.DataObject.GetData(DataFormats.Text) as string ?? "";
        if (pastedText.Any(character => !char.IsDigit(character) && character != ',' && character != '.' && !char.IsWhiteSpace(character)))
        {
            e.CancelCommand();
        }
    }

    private static string ReadComboBoxText(ComboBox comboBox)
    {
        return comboBox.SelectedItem is ComboBoxItem item
            ? item.Content?.ToString() ?? ""
            : comboBox.Text.Trim();
    }

    private static void SetComboBoxSelection(ComboBox comboBox, string value)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Content?.ToString(), value, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }

        comboBox.Text = value;
    }

    private static bool TryParseMoney(string text, out decimal value)
    {
        text = text.Trim().Replace("R$", "", StringComparison.OrdinalIgnoreCase).Trim();

        var normalizedText = NormalizeMoneyText(text);
        if (decimal.TryParse(normalizedText, NumberStyles.Number, CultureInfo.InvariantCulture, out value))
        {
            value = Math.Max(0, value);
            return true;
        }

        value = 0;
        return false;
    }

    private static string NormalizeMoneyText(string text)
    {
        text = text.Replace(" ", "");
        var commaIndex = text.LastIndexOf(',');
        var dotIndex = text.LastIndexOf('.');

        if (commaIndex >= 0 && dotIndex >= 0)
        {
            return commaIndex > dotIndex
                ? text.Replace(".", "").Replace(',', '.')
                : text.Replace(",", "");
        }

        return commaIndex >= 0 ? text.Replace(',', '.') : text;
    }

    private static string FormatCurrency(decimal value)
    {
        return value.ToString("C2", BrazilianCulture);
    }
}

public sealed class AppDatabase
{
    public string Pin { get; set; } = "1234";
    public DateTime LastUpdated { get; set; } = DateTime.Now;
    public DateTime LastBanDecayDate { get; set; } = DateTime.Today;
    public DateTime LastBanCheckTime { get; set; } = DateTime.Now;
    public List<SteamAccount> Accounts { get; set; } = [];
    public List<AccountDrop> Drops { get; set; } = [];
}

public sealed class DropAccountSummary
{
    public DropAccountSummary(SteamAccount account, int dropCount, bool isSelected)
    {
        Account = account;
        DropCount = dropCount;
        IsSelected = isSelected;
    }

    public SteamAccount Account { get; }
    public int DropCount { get; }
    public bool IsSelected { get; }
    public string Name => Account.Name;
    public string DropCountText => DropCount == 1 ? "1 drop adicionado" : $"{DropCount} drops adicionados";

    [JsonIgnore]
    public Brush CardBackgroundBrush => IsSelected ? SteamAccount.ToBrush("#0D3157") : SteamAccount.ToBrush("#0A1724");

    [JsonIgnore]
    public Brush CardBorderBrush => IsSelected ? SteamAccount.ToBrush("#1677D2") : SteamAccount.ToBrush("#13283A");
}

public sealed class AccountDrop : INotifyPropertyChanged
{
    private string _weapon = "";
    private string _name = "";
    private string _condition = "Nova de FÃ¡brica";
    private decimal _value;
    private DateTime _addedAt = DateTime.Now;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }

    public string Weapon
    {
        get => _weapon;
        set => SetField(ref _weapon, value);
    }

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Condition
    {
        get => _condition;
        set
        {
            if (SetField(ref _condition, value))
            {
                OnPropertyChanged(nameof(ConditionBrush));
            }
        }
    }

    public decimal Value
    {
        get => _value;
        set
        {
            if (SetField(ref _value, value))
            {
                OnPropertyChanged(nameof(ValueText));
            }
        }
    }

    public DateTime AddedAt
    {
        get => _addedAt;
        set
        {
            if (SetField(ref _addedAt, value))
            {
                OnPropertyChanged(nameof(AddedAtText));
            }
        }
    }

    [JsonIgnore]
    public string ValueText => Value.ToString("C2", new CultureInfo("pt-BR"));

    [JsonIgnore]
    public string AddedAtText => AddedAt.ToString("dd/MM/yyyy HH:mm", new CultureInfo("pt-BR"));

    [JsonIgnore]
    public Brush ConditionBrush
    {
        get
        {
            return Condition switch
            {
                "Nova de FÃ¡brica" or "Nova de Fabrica" => SteamAccount.ToBrush("#39C970"),
                "Pouco Desgastada" => SteamAccount.ToBrush("#58A6FF"),
                "Testada em Campo" => SteamAccount.ToBrush("#E4C25A"),
                "Bem Desgastada" => SteamAccount.ToBrush("#F39A32"),
                "Veterana de Guerra" => SteamAccount.ToBrush("#C981FF"),
                "None" => SteamAccount.ToBrush("#8DA4B5"),
                _ => SteamAccount.ToBrush("#E9F3FF")
            };
        }
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public static class RankFormatter
{
    private static readonly CultureInfo BrazilianCulture = new("pt-BR");

    public static string Normalize(string? rank)
    {
        rank = (rank ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rank))
        {
            return "Sem Premier";
        }

        if (rank.Equals("0", StringComparison.OrdinalIgnoreCase) ||
            rank.Equals("sem premier", StringComparison.OrdinalIgnoreCase) ||
            rank.Equals("sem", StringComparison.OrdinalIgnoreCase) ||
            rank.Equals("no premier", StringComparison.OrdinalIgnoreCase))
        {
            return "Sem Premier";
        }

        var digitsOnly = new string(rank.Where(char.IsDigit).ToArray());
        if (int.TryParse(digitsOnly, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            return value <= 0 ? "Sem Premier" : value.ToString("N0", BrazilianCulture);
        }

        return rank;
    }

    public static bool IsUnranked(string? rank)
    {
        return Normalize(rank).Equals("Sem Premier", StringComparison.OrdinalIgnoreCase);
    }

    public static int GetNumericValue(string? rank)
    {
        var normalized = Normalize(rank);
        var digitsOnly = new string(normalized.Where(char.IsDigit).ToArray());
        return int.TryParse(digitsOnly, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? Math.Max(0, value)
            : 0;
    }
}

public sealed class SteamAccount : INotifyPropertyChanged
{
    private static readonly Dictionary<string, Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    private string _name = "";
    private string _password = "";
    private string _url = "";
    private int _victories;
    private int _ban;
    private string _rankCS2 = "Sem Premier";
    private bool _dropActive;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name
    {
        get => _name;
        set => SetField(ref _name, value);
    }

    public string Password
    {
        get => _password;
        set => SetField(ref _password, value);
    }

    public string Url
    {
        get => _url;
        set => SetField(ref _url, value);
    }

    public int Victories
    {
        get => _victories;
        set => SetField(ref _victories, value);
    }

    public int Ban
    {
        get => _ban;
        set
        {
            if (SetField(ref _ban, value))
            {
                OnPropertyChanged(nameof(BanDisplayText));
                OnPropertyChanged(nameof(BanBackgroundBrush));
                OnPropertyChanged(nameof(BanBorderBrush));
                OnPropertyChanged(nameof(BanForegroundBrush));
            }
        }
    }

    public string RankCS2
    {
        get => _rankCS2;
        set
        {
            if (SetField(ref _rankCS2, RankFormatter.Normalize(value)))
            {
                OnPropertyChanged(nameof(RankBrush));
                OnPropertyChanged(nameof(RankDisplayText));
                OnPropertyChanged(nameof(IsUnrankedPremier));
                OnPropertyChanged(nameof(RankChipBackgroundBrush));
                OnPropertyChanged(nameof(RankBorderBrush));
                OnPropertyChanged(nameof(RankTextBrush));
            }
        }
    }

    public bool DropActive
    {
        get => _dropActive;
        set => SetField(ref _dropActive, value);
    }

    [JsonIgnore]
    public bool IsUnrankedPremier => RankFormatter.IsUnranked(RankCS2);

    [JsonIgnore]
    public string RankDisplayText => IsUnrankedPremier ? "Premier" : RankCS2;

    [JsonIgnore]
    public string BanDisplayText => Ban < 0 ? "-" : Ban > 0 ? $"{Ban} dias" : "0 dias";

    [JsonIgnore]
    public Brush BanBackgroundBrush => Ban > 0 ? ToBrush("#32151C") : Brushes.Transparent;

    [JsonIgnore]
    public Brush BanBorderBrush => Ban > 0 ? ToBrush("#8D2530") : Brushes.Transparent;

    [JsonIgnore]
    public Brush BanForegroundBrush => Ban > 0 ? ToBrush("#DD5353") : ToBrush("#E9F3FF");

    [JsonIgnore]
    public Brush RankBrush
    {
        get
        {
            if (IsUnrankedPremier)
            {
                return ToBrush("#2E3B4C");
            }

            var rankNumber = RankCS2.Replace(".", "", StringComparison.OrdinalIgnoreCase);
            if (int.TryParse(rankNumber, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                if (value >= 30000)
                {
                    return ToBrush("#C4A000");
                }

                if (value >= 25000)
                {
                    return ToBrush("#B94444");
                }

                if (value >= 20000)
                {
                    return ToBrush("#A62B86");
                }

                if (value >= 15000)
                {
                    return ToBrush("#5B3AA6");
                }

                if (value >= 10000)
                {
                    return ToBrush("#3454B4");
                }

                if (value >= 5000)
                {
                    return ToBrush("#245F9E");
                }
            }

            return ToBrush("#3D5664");
        }
    }

    [JsonIgnore]
    public Brush RankChipBackgroundBrush
    {
        get
        {
            if (IsUnrankedPremier)
            {
                return ToBrush("#101923");
            }

            var value = RankFormatter.GetNumericValue(RankCS2);
            if (value >= 30000)
            {
                return ToBrush("#3C3307");
            }

            if (value >= 25000)
            {
                return ToBrush("#3B1C20");
            }

            if (value >= 20000)
            {
                return ToBrush("#331A39");
            }

            if (value >= 15000)
            {
                return ToBrush("#261A46");
            }

            if (value >= 10000)
            {
                return ToBrush("#0B2448");
            }

            if (value >= 5000)
            {
                return ToBrush("#062C45");
            }

            return ToBrush("#172A35");
        }
    }

    [JsonIgnore]
    public Brush RankBorderBrush => IsUnrankedPremier ? ToBrush("#4A5A68") : RankTextBrush;

    [JsonIgnore]
    public Brush RankTextBrush
    {
        get
        {
            if (IsUnrankedPremier)
            {
                return ToBrush("#D9E6F0");
            }

            var value = RankFormatter.GetNumericValue(RankCS2);
            if (value >= 30000)
            {
                return ToBrush("#E5C400");
            }

            if (value >= 25000)
            {
                return ToBrush("#E06363");
            }

            if (value >= 20000)
            {
                return ToBrush("#D65CAF");
            }

            if (value >= 15000)
            {
                return ToBrush("#D276D9");
            }

            if (value >= 10000)
            {
                return ToBrush("#78A9FF");
            }

            if (value >= 5000)
            {
                return ToBrush("#19C1D7");
            }

            return ToBrush("#76A8BA");
        }
    }

    public static Brush ToBrush(string color)
    {
        if (BrushCache.TryGetValue(color, out var cachedBrush))
        {
            return cachedBrush;
        }

        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        if (brush.CanFreeze)
        {
            brush.Freeze();
        }

        BrushCache[color] = brush;
        return brush;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

using Peak.Can.Basic;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using System.Windows;

namespace KyburzCanTool
{
    public class MainViewModel : INotifyPropertyChanged
    {
        // --------------------------------------------------------------------------------
        // EIGENSCHAFTEN UND FELDER
        // --------------------------------------------------------------------------------

        // Listen
        public ObservableCollection<CommandSet> AvailableCommands { get; set; }
        public ObservableCollection<CanMessage> CanLogMessages { get; set; } // TX-Liste
        public ObservableCollection<CanMessage> RxMessages { get; set; }     // RX-Liste

        // Steuerung
        private CommandSet _selectedCommand;
        public CommandSet SelectedCommand
        {
            get => _selectedCommand;
            set
            {
                if (_selectedCommand != value)
                {
                    if (_selectedCommand != null && _activeTxTokens.ContainsKey(_selectedCommand.Command))
                    {
                        StopCommandExecution(_selectedCommand);
                    }

                    _selectedCommand = value;
                    OnPropertyChanged(nameof(SelectedCommand));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        // LED-Steuerung
        private bool _isTxActiveLight;
        public bool IsTxActiveLight
        {
            get => _isTxActiveLight;
            set
            {
                if (_isTxActiveLight != value)
                {
                    _isTxActiveLight = value;
                    OnPropertyChanged(nameof(IsTxActiveLight));
                }
            }
        }

        private bool _isRxActiveLight;
        public bool IsRxActiveLight
        {
            get => _isRxActiveLight;
            set
            {
                if (_isRxActiveLight != value)
                {
                    _isRxActiveLight = value;
                    OnPropertyChanged(nameof(IsRxActiveLight));
                }
            }
        }

        // Interne Felder
        private readonly Dictionary<string, CancellationTokenSource> _activeTxTokens =
            new Dictionary<string, CancellationTokenSource>();

        private readonly Dictionary<int, DateTime> _lastRxTime = new Dictionary<int, DateTime>();

        private readonly ushort _channel = PCANBasic.PCAN_USBBUS1;
        private readonly TPCANBaudrate _baudrate = TPCANBaudrate.PCAN_BAUD_125K;

        private readonly DispatcherTimer _txUpdateTimer;
        private readonly DispatcherTimer _rxUpdateTimer;
        private CancellationTokenSource _rxCancellationTokenSource;

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }

        // --------------------------------------------------------------------------------
        // KONSTRUKTOR
        // --------------------------------------------------------------------------------

        public MainViewModel()
        {
            // 1. Initialisierung der Daten
            DatabaseHelper.InitializeDatabase();
            var loadedCommands = DatabaseHelper.LoadCommandSets();

            AvailableCommands = new ObservableCollection<CommandSet>(loadedCommands);
            CanLogMessages = new ObservableCollection<CanMessage>();
            RxMessages = new ObservableCollection<CanMessage>();

            // 2. PCAN INITIALISIERUNG
            TPCANStatus status = PCANBasic.Initialize(_channel, _baudrate);

            if (status != TPCANStatus.PCAN_ERROR_OK)
            {
                MessageBox.Show($"PCAN Initialisierung fehlgeschlagen! Status: {status}", "CAN Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            else
            {
                Debug.WriteLine("PCAN-USB erfolgreich initialisiert.");
            }

            // 3. Initialisierung der Commands
            StartCommand = new RelayCommand(StartCommandExecution, CanStartCommandExecute);
            StopCommand = new RelayCommand(StopCommandExecution, CanStopCommandExecute);

            // 4. Starten des RX-Threads
            StartCanReceiveThread();

            // 5. Starten des TX UI-Update-Timers (für LoopCounts und LED-Aus-Schalten)
            _txUpdateTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _txUpdateTimer.Interval = TimeSpan.FromMilliseconds(200);
            _txUpdateTimer.Tick += TxUpdateTimer_Tick;
            _txUpdateTimer.Start();

            // 6. Starten des RX UI-Update-Timers (für langsame RX-Aktualisierung und Sortierung)
            _rxUpdateTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _rxUpdateTimer.Interval = TimeSpan.FromMilliseconds(500);
            _rxUpdateTimer.Tick += RxUpdateTimer_Tick;
            _rxUpdateTimer.Start();
        }

        // --------------------------------------------------------------------------------
        // COMMAND-LOGIK
        // --------------------------------------------------------------------------------

        private bool CanStartCommandExecute()
        {
            return SelectedCommand != null && !_activeTxTokens.ContainsKey(SelectedCommand.Command);
        }

        private void StartCommandExecution()
        {
            if (SelectedCommand == null) return;

            StartCanTransmitThread(SelectedCommand);
            CommandManager.InvalidateRequerySuggested();

            foreach (var msg in SelectedCommand.MessagesToSend.Where(m => m.RxTX == "TX"))
            {
                if (!CanLogMessages.Contains(msg))
                {
                    Application.Current.Dispatcher.Invoke(() => CanLogMessages.Add(msg));
                }
            }
        }

        private bool CanStopCommandExecute()
        {
            return SelectedCommand != null && _activeTxTokens.ContainsKey(SelectedCommand.Command);
        }

        private void StopCommandExecution()
        {
            if (SelectedCommand == null) return;
            StopCanTransmitThread(SelectedCommand.Command);
            CommandManager.InvalidateRequerySuggested();
        }

        private void StopCommandExecution(CommandSet commandSet)
        {
            StopCanTransmitThread(commandSet.Command);
            CommandManager.InvalidateRequerySuggested();
        }

        // --------------------------------------------------------------------------------
        // TX-LOGIK
        // --------------------------------------------------------------------------------

        private void StartCanTransmitThread(CommandSet commandSet)
        {
            if (_activeTxTokens.ContainsKey(commandSet.Command)) return;

            var txCancellationTokenSource = new CancellationTokenSource();

            // Korrektur: Task.Run OHNE das Token als zweiten Parameter, um die TaskCanceledException zu vermeiden
            Task.Run(() => CanTransmitWorker(commandSet, txCancellationTokenSource.Token));

            _activeTxTokens.Add(commandSet.Command, txCancellationTokenSource);

            Debug.WriteLine($"TX Worker gestartet für: {commandSet.Command}");
        }

        private void StopCanTransmitThread(string command)
        {
            if (_activeTxTokens.TryGetValue(command, out var source))
            {
                source.Cancel();
                _activeTxTokens.Remove(command);

                Debug.WriteLine($"TX Worker gestoppt für: {command}");

                var msgsToRemove = CanLogMessages.Where(m => m.Command == command && m.RxTX == "TX").ToList();
                foreach (var msg in msgsToRemove)
                {
                    // Auch beim Entfernen: Prüfung auf existierende App
                    if (Application.Current != null)
                    {
                        Application.Current.Dispatcher.Invoke(() => CanLogMessages.Remove(msg));
                    }
                }
            }
        }

        private async void CanTransmitWorker(CommandSet commandSet, CancellationToken token)
        {
            var txMessages = commandSet.MessagesToSend.Where(m => m.RxTX == "TX").ToList();
            var lastSentTime = new Dictionary<int, DateTime>();

            foreach (var msg in txMessages)
            {
                lastSentTime[msg.GetHashCode()] = DateTime.MinValue;
            }

            try
            {
                while (!token.IsCancellationRequested)
                {
                    var now = DateTime.Now;
                    bool allFinished = true;

                    foreach (var msg in txMessages)
                    {
                        if (msg.LoopCount != -2)
                        {
                            allFinished = false;

                            if (msg.CycleTime > 0 && (now - lastSentTime[msg.GetHashCode()]).TotalMilliseconds >= msg.CycleTime)
                            {
                                if (msg.LoopCount == 0 || msg.LoopCount > 0)
                                {
                                    // --- PCAN SEND LOGIK ---
                                    TPCANMsg pcanMsg = new TPCANMsg();
                                    pcanMsg.ID = (uint)msg.CanID;
                                    pcanMsg.LEN = msg.DLC;
                                    pcanMsg.DATA = new byte[8];
                                    for (int i = 0; i < msg.DLC && i < 8; i++)
                                    {
                                        pcanMsg.DATA[i] = msg.Payload[i];
                                    }

                                    TPCANStatus status = PCANBasic.Write(_channel, ref pcanMsg);
                                    // -----------------------

                                    lastSentTime[msg.GetHashCode()] = now;

                                    if (status == TPCANStatus.PCAN_ERROR_OK)
                                    {
                                        // LED EINSCHALTEN - Doppelte Sicherung gegen Task-Abbruch und NullReference
                                        if (!token.IsCancellationRequested && Application.Current != null)
                                        {
                                            Application.Current.Dispatcher.Invoke(() => IsTxActiveLight = true);
                                        }
                                    }
                                    else
                                    {
                                        Debug.WriteLine($"SEND ERROR: ID={msg.CanID:X3}, Status={status}");
                                    }

                                    if (msg.LoopCount > 0)
                                    {
                                        msg.LoopCount--;
                                        // LoopCount Update - Doppelte Sicherung
                                        if (!token.IsCancellationRequested && Application.Current != null)
                                        {
                                            Application.Current.Dispatcher.Invoke(() => msg.OnPropertyChanged(nameof(CanMessage.LoopCount)));
                                        }
                                    }
                                    else if (msg.LoopCount < 0)
                                    {
                                        msg.LoopCount = -2;
                                    }
                                }
                            }
                        }
                    }

                    if (allFinished)
                    {
                        // Stoppen des Kommandos - Doppelte Sicherung
                        if (Application.Current != null)
                        {
                            Application.Current.Dispatcher.Invoke(() => StopCommandExecution(commandSet));
                        }
                        return;
                    }

                    await Task.Delay(1);
                }
            }
            catch (OperationCanceledException)
            {
                // Fängt den sauberen Abbruch ab
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unerwarteter Fehler im TX Worker: {ex.Message}");
            }
        }

        // --------------------------------------------------------------------------------
        // RX-LOGIK
        // --------------------------------------------------------------------------------

        private void StartCanReceiveThread()
        {
            _rxCancellationTokenSource = new CancellationTokenSource();
            // Startet Task OHNE explizite Überwachung durch das Token
            Task.Run(() => CanReceiveWorker(_rxCancellationTokenSource.Token));
        }

        private async void CanReceiveWorker(CancellationToken token)
        {
            TPCANMsg pcanMsg;
            TPCANTimestamp timestamp;

            try
            {
                while (!token.IsCancellationRequested)
                {
                    TPCANStatus status = PCANBasic.Read(_channel, out pcanMsg, out timestamp);

                    if (status == TPCANStatus.PCAN_ERROR_OK)
                    {
                        int receivedID = (int)pcanMsg.ID;
                        byte[] receivedData = pcanMsg.DATA;
                        DateTime currentTime = DateTime.Now;

                        // 1. Berechnung der Zykluszeit
                        int calculatedCycleTimeMs = 0;
                        if (_lastRxTime.ContainsKey(receivedID))
                        {
                            calculatedCycleTimeMs = (int)(currentTime - _lastRxTime[receivedID]).TotalMilliseconds;
                        }
                        _lastRxTime[receivedID] = currentTime;

                        // 2. Suche oder Erstellung des Listeneintrags
                        var rxEntry = RxMessages.FirstOrDefault(m => m.CanID == receivedID);

                        if (rxEntry == null)
                        {
                            rxEntry = new CanMessage
                            {
                                CanID = receivedID,
                                RxTX = "RX",
                                Comment = "Dynamisch empfangen",
                                DLC = pcanMsg.LEN,
                                ReceivedPayload = BitConverter.ToString(receivedData, 0, pcanMsg.LEN).Replace("-", " "),
                                RxTime = currentTime.ToString("HH:mm:ss.fff"),
                                CycleTime = calculatedCycleTimeMs
                            };

                            // Hinzufügen zum RX Log - Doppelte Sicherung
                            if (!token.IsCancellationRequested && Application.Current != null)
                            {
                                Application.Current.Dispatcher.Invoke(() => RxMessages.Add(rxEntry));
                            }
                        }
                        else
                        {
                            // 3. Nur die Daten des bestehenden Eintrags aktualisieren
                            rxEntry.DLC = pcanMsg.LEN;
                            rxEntry.ReceivedPayload = BitConverter.ToString(receivedData, 0, pcanMsg.LEN).Replace("-", " ");
                            rxEntry.RxTime = currentTime.ToString("HH:mm:ss.fff");
                            rxEntry.CycleTime = calculatedCycleTimeMs;
                        }

                        // LED EINSCHALTEN - Doppelte Sicherung
                        if (!token.IsCancellationRequested && Application.Current != null)
                        {
                            Application.Current.Dispatcher.Invoke(() => IsRxActiveLight = true);
                        }
                    }
                    else if (status != TPCANStatus.PCAN_ERROR_QRCVEMPTY)
                    {
                        Debug.WriteLine($"CAN Read Error: {status}");
                    }

                    await Task.Delay(1);
                }
            }
            catch (OperationCanceledException)
            {
                // Fängt den sauberen Abbruch ab
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Unerwarteter Fehler im RX Worker: {ex.Message}");
            }
        }

        // --------------------------------------------------------------------------------
        // UI-UPDATE LOGIK (Timer)
        // --------------------------------------------------------------------------------

        private void TxUpdateTimer_Tick(object sender, EventArgs e)
        {
            // Aktualisiert die LoopCounts im TX DataGrid
            foreach (var msg in CanLogMessages)
            {
                msg.OnPropertyChanged(nameof(CanMessage.LoopCount));
            }

            // LED-Steuerung (Schaltet die Lichter nach 200ms wieder aus = Blinken)
            if (IsTxActiveLight) IsTxActiveLight = false;
            if (IsRxActiveLight) IsRxActiveLight = false;
        }

        private void RxUpdateTimer_Tick(object sender, EventArgs e)
        {
            // Führt das langsame UI-Update und die Sortierung durch

            // 1. Aktualisierung der Datenbindung für die Anzeige
            foreach (var msg in RxMessages)
            {
                msg.OnPropertyChanged(nameof(CanMessage.CurrentPayloadString));
                msg.OnPropertyChanged(nameof(CanMessage.RxTime));
                msg.OnPropertyChanged(nameof(CanMessage.CycleTime));
            }

            // 2. Sortierung der RxMessages nach CanID
            var sortedList = RxMessages.OrderBy(m => m.CanID).ToList();

            for (int i = 0; i < sortedList.Count; i++)
            {
                if (RxMessages.IndexOf(sortedList[i]) != i)
                {
                    RxMessages.Move(RxMessages.IndexOf(sortedList[i]), i);
                }
            }
        }

        // --------------------------------------------------------------------------------
        // AUFRÄUMEN
        // --------------------------------------------------------------------------------

        public void Cleanup()
        {
            _rxCancellationTokenSource?.Cancel();
            _txUpdateTimer.Stop();
            _rxUpdateTimer.Stop();

            // Deinitialisierung des CAN-Adapters
            PCANBasic.Uninitialize(_channel);
            Debug.WriteLine("PCAN-USB deinitialisiert.");
        }

        // --------------------------------------------------------------------------------
        // INotifyPropertyChanged IMPLEMENTIERUNG
        // --------------------------------------------------------------------------------

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
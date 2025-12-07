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

        public ObservableCollection<CommandSet> AvailableCommands { get; set; }
        public ObservableCollection<CanMessage> CanLogMessages { get; set; }

        private CommandSet _selectedCommand;
        public CommandSet SelectedCommand
        {
            get => _selectedCommand;
            set
            {
                if (_selectedCommand != value)
                {
                    // Stoppt den aktuell laufenden Command beim Wechsel
                    if (_selectedCommand != null && _activeTxTokens.ContainsKey(_selectedCommand.Command))
                    {
                        StopCommandExecution(_selectedCommand);
                    }

                    _selectedCommand = value;
                    OnPropertyChanged(nameof(SelectedCommand));
                    CommandManager.InvalidateRequerySuggested(); // Aktualisiert Button-Status
                }
            }
        }

        // Dictionary zur Verwaltung der aktiven Sende-Tasks (mit Cancel-Token)
        private readonly Dictionary<string, CancellationTokenSource> _activeTxTokens =
            new Dictionary<string, CancellationTokenSource>();

        private readonly ushort _channel = PCANBasic.PCAN_USBBUS1; // CAN-Kanal
        private readonly TPCANBaudrate _baudrate = TPCANBaudrate.PCAN_BAUD_125K; // CAN-Baudrate

        private readonly DispatcherTimer _uiUpdateTimer;
        private CancellationTokenSource _rxCancellationTokenSource;

        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }

        // --------------------------------------------------------------------------------
        // KONSTRUKTOR
        // --------------------------------------------------------------------------------

        public MainViewModel()
        {
            // 1. Initialisierung der DB und Laden der Daten
            DatabaseHelper.InitializeDatabase();
            var loadedCommands = DatabaseHelper.LoadCommandSets();

            AvailableCommands = new ObservableCollection<CommandSet>(loadedCommands);
            CanLogMessages = new ObservableCollection<CanMessage>();

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

            // 3. RX-Nachrichten zur Log-Liste hinzufügen (einmalig)
            foreach (var cmd in loadedCommands)
            {
                foreach (var msg in cmd.MessagesToSend.Where(m => m.RxTX == "RX"))
                {
                    var logMsg = new CanMessage
                    {
                        CanID = msg.CanID,
                        DLC = msg.DLC,
                        RxTX = msg.RxTX,
                        Comment = msg.Comment,
                        ReceivedPayload = "--- Waiting ---",
                        RxTime = "N/A"
                    };
                    CanLogMessages.Add(logMsg);
                }
            }

            // 4. Initialisierung der Commands für die Buttons
            StartCommand = new RelayCommand(StartCommandExecution, CanStartCommandExecute);
            StopCommand = new RelayCommand(StopCommandExecution, CanStopCommandExecute);

            // 5. Starten des RX-Threads
            StartCanReceiveThread();

            // 6. Starten des UI-Update-Timers
            _uiUpdateTimer = new DispatcherTimer(DispatcherPriority.Normal);
            _uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(500); // 2x pro Sekunde
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
            _uiUpdateTimer.Start();
        }

        // --------------------------------------------------------------------------------
        // COMMAND-LOGIK (Start/Stop Buttons)
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

            // Fügen Sie die TX-Nachrichten zum Log hinzu, wenn sie gestartet werden
            foreach (var msg in SelectedCommand.MessagesToSend.Where(m => m.RxTX == "TX"))
            {
                // Nur hinzufügen, wenn noch nicht im Log
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

        // Überladene Version für implizite Stopp-Logik
        private void StopCommandExecution(CommandSet commandSet)
        {
            StopCanTransmitThread(commandSet.Command);
            CommandManager.InvalidateRequerySuggested();
        }

        // --------------------------------------------------------------------------------
        // TX-LOGIK (Senden)
        // --------------------------------------------------------------------------------

        private void StartCanTransmitThread(CommandSet commandSet)
        {
            if (_activeTxTokens.ContainsKey(commandSet.Command)) return;

            var txCancellationTokenSource = new CancellationTokenSource();

            // Starte einen Task für das Senden
            Task.Run(() => CanTransmitWorker(commandSet, txCancellationTokenSource.Token), txCancellationTokenSource.Token);

            // Speichern des Tokens, um den Task später abbrechen zu können
            _activeTxTokens.Add(commandSet.Command, txCancellationTokenSource);

            Debug.WriteLine($"TX Worker gestartet für: {commandSet.Command}");
        }

        private void StopCanTransmitThread(string command)
        {
            if (_activeTxTokens.TryGetValue(command, out var source))
            {
                source.Cancel(); // Token abbrechen
                _activeTxTokens.Remove(command);

                Debug.WriteLine($"TX Worker gestoppt für: {command}");

                // Optional: Die TX-Nachrichten aus dem Log entfernen
                var msgsToRemove = CanLogMessages.Where(m => m.Command == command && m.RxTX == "TX").ToList();
                foreach (var msg in msgsToRemove)
                {
                    Application.Current.Dispatcher.Invoke(() => CanLogMessages.Remove(msg));
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

            while (!token.IsCancellationRequested)
            {
                var now = DateTime.Now;
                bool allFinished = true;

                foreach (var msg in txMessages)
                {
                    if (msg.LoopCount != -2) // -2 bedeutet "Abgeschlossen"
                    {
                        allFinished = false; // Mindestens eine Nachricht muss noch gesendet werden

                        if (msg.CycleTime > 0 && (now - lastSentTime[msg.GetHashCode()]).TotalMilliseconds >= msg.CycleTime)
                        {
                            if (msg.LoopCount == 0 || msg.LoopCount > 0)
                            {
                                // --- PCAN SEND LOGIK ---
                                TPCANMsg pcanMsg = new TPCANMsg();
                                pcanMsg.ID = (uint)msg.CanID;
                                pcanMsg.LEN = msg.DLC;
                                pcanMsg.DATA = new byte[8];

                                // Die Payload aus dem Message-Objekt in die TPCANMsg.DATA Struktur kopieren
                                for (int i = 0; i < msg.DLC && i < 8; i++)
                                {
                                    pcanMsg.DATA[i] = msg.Payload[i];
                                }

                                TPCANStatus status = PCANBasic.Write(_channel, ref pcanMsg);
                                // -----------------------

                                lastSentTime[msg.GetHashCode()] = now;

                                if (status == TPCANStatus.PCAN_ERROR_OK)
                                {
                                    Debug.WriteLine($"SENT: ID={msg.CanID:X3}, Command={msg.Command}, Loop={msg.LoopCount}");
                                }
                                else
                                {
                                    Debug.WriteLine($"SEND ERROR: ID={msg.CanID:X3}, Status={status}");
                                }

                                // LoopCount dekrementieren und UI-Update triggern
                                if (msg.LoopCount > 0)
                                {
                                    msg.LoopCount--;
                                    Application.Current.Dispatcher.Invoke(() => msg.OnPropertyChanged(nameof(CanMessage.LoopCount)));
                                }
                                else if (msg.LoopCount == 0)
                                {
                                    // Wenn LoopCount 0 war (immer senden), bleibt es 0
                                }
                                else
                                {
                                    // Wenn der Zähler abgelaufen ist
                                    msg.LoopCount = -2;
                                }
                            }
                        }
                    }
                }

                if (allFinished)
                {
                    // Alle zyklischen Nachrichten sind abgelaufen (falls LoopCount > 0 war)
                    Application.Current.Dispatcher.Invoke(() => StopCommandExecution(commandSet));
                    return;
                }

                // Warten, um die CPU zu entlasten (Poll-Interval)
                await Task.Delay(1);
            }
        }

        // --------------------------------------------------------------------------------
        // RX-LOGIK (Empfangen)
        // --------------------------------------------------------------------------------

        private void StartCanReceiveThread()
        {
            _rxCancellationTokenSource = new CancellationTokenSource();
            Task.Run(() => CanReceiveWorker(_rxCancellationTokenSource.Token));
        }

        private async void CanReceiveWorker(CancellationToken token)
        {
            TPCANMsg pcanMsg;
            TPCANTimestamp timestamp;

            while (!token.IsCancellationRequested)
            {
                // Versuche, eine Nachricht zu lesen
                TPCANStatus status = PCANBasic.Read(_channel, out pcanMsg, out timestamp);

                if (status == TPCANStatus.PCAN_ERROR_OK)
                {
                    // Nachricht erfolgreich empfangen
                    int receivedID = (int)pcanMsg.ID;
                    byte[] receivedData = pcanMsg.DATA;

                    var logEntry = CanLogMessages.FirstOrDefault(m => m.CanID == receivedID && m.RxTX == "RX");

                    if (logEntry != null)
                    {
                        // Aktualisieren Sie die Daten im UI-Thread
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            logEntry.ReceivedPayload = BitConverter.ToString(receivedData, 0, pcanMsg.LEN).Replace("-", " ");
                            logEntry.RxTime = DateTime.Now.ToString("HH:mm:ss.fff");
                            Debug.WriteLine($"RECEIVED: ID={receivedID:X3}");
                        });
                    }
                }
                else if (status == TPCANStatus.PCAN_ERROR_QRCVEMPTY)
                {
                    // Keine Nachrichten in der Queue, einfach fortfahren
                }
                else
                {
                    // Fehler beim Lesen, zur Diagnose ausgeben
                    Debug.WriteLine($"CAN Read Error: {status}");
                }

                // Kurze Verzögerung, um die CPU nicht zu überlasten
                await Task.Delay(1);
            }
        }

        // --------------------------------------------------------------------------------
        // AUFRÄUMEN UND UI-UPDATE
        // --------------------------------------------------------------------------------

        private void UiUpdateTimer_Tick(object sender, EventArgs e)
        {
            // Erzwingt ein UI-Update für RX-Nachrichten (2x/sek), um die Zeit und Payload zu zeigen
            foreach (var msg in CanLogMessages.Where(m => m.RxTX == "RX"))
            {
                msg.OnPropertyChanged(nameof(CanMessage.CurrentPayloadString));
                msg.OnPropertyChanged(nameof(CanMessage.RxTime));
            }
        }

        // Wird beim Schließen der Anwendung aufgerufen (durch MainWindow.xaml.cs)
        public void Cleanup()
        {
            _rxCancellationTokenSource?.Cancel();
            _uiUpdateTimer.Stop();

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
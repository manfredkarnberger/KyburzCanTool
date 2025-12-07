using System.ComponentModel;
using System.Text;

namespace KyburzCanTool
{
    public class CanMessage : INotifyPropertyChanged
    {
        // Entspricht den Feldern der SQLite-Tabelle 'Kyburz'
        public int CanID { get; set; }
        public byte DLC { get; set; }
        public byte[] Payload { get; set; }
        public int CycleTime { get; set; } // Millisekunden
        public string RxTX { get; set; }
        public string Command { get; set; }
        public string Comment { get; set; }
        public int LoopCount { get; set; }

        // Felder für empfangene Nachrichten
        private string _receivedPayload;
        public string ReceivedPayload
        {
            get => _receivedPayload;
            set
            {
                if (_receivedPayload != value)
                {
                    _receivedPayload = value;
                    OnPropertyChanged(nameof(ReceivedPayload));
                }
            }
        }

        public string PayloadHexString => BitConverter.ToString(Payload).Replace("-", " ");
        public string RxTime { get; set; } // Zeitstempel für empfangene Nachrichten

        // Hilfsklasse für DataGrid-Anzeige (Payload-Konvertierung)
        public string CurrentPayloadString
        {
            get
            {
                // Wenn es eine Empfangene Nachricht ist, zeige deren Payload
                if (!string.IsNullOrEmpty(ReceivedPayload)) return ReceivedPayload;

                // Sonst die zu sendende Payload
                return Payload == null ? "" : BitConverter.ToString(Payload).Replace("-", " ");
            }
        }

        // Implementierung für DataBinding-Updates
        public event PropertyChangedEventHandler PropertyChanged;
        public void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    // CommandSet.cs
    public class CommandSet : INotifyPropertyChanged
    {
        private bool _isSelected;
        public string Command { get; set; }

        // Liste der zu diesem Command gehörenden Nachrichten
        public List<CanMessage> MessagesToSend { get; set; } = new List<CanMessage>();

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}

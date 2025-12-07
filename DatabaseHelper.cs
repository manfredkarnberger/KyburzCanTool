using Microsoft.Data.Sqlite;
using System.Collections.Generic;
using System.IO;

namespace KyburzCanTool
{
    public class DatabaseHelper
    {
        private const string DbFile = "CanMessages.db";
        private const string ConnectionString = $"Data Source={DbFile}";

        public static void InitializeDatabase()
        {
            // Erstellt die Datenbankdatei, falls sie nicht existiert
            if (!File.Exists(DbFile))
            {
                using (var connection = new SqliteConnection(ConnectionString))
                {
                    connection.Open();
                    var command = connection.CreateCommand();

                    // Erstellt die Tabelle 'Kyburz'
                    command.CommandText = @"
                    CREATE TABLE Kyburz (
                        CanID INTEGER,
                        DLC INTEGER,
                        Payload TEXT, -- HEX-String in DB gespeichert
                        CycleTime INTEGER,
                        RxTX TEXT,    -- 'TX' oder 'RX'
                        Command TEXT,
                        Comment TEXT,
                        LoopCount INTEGER
                    );";
                    command.ExecuteNonQuery();

                    // Fügen Sie hier Testdaten ein (optional)
                    command.CommandText = @"
                    INSERT INTO Kyburz VALUES 
                    (0x26F, 8, '00000000FFFF0000', 100, 'TX', 'SPEED', 'SPEED', 10),
                    (0x56F, 8, '0000000000FF0000', 100, 'TX', 'SPEED', 'SPEED', 10),
                    (0x270, 8, '0000000000000002', 100, 'TX', 'SPEED', 'SPEED', 10),
                    (0x270, 8, '000C080000000000', 100, 'TX', 'CAMERA', 'CAMERA', 10);
                    ";
                    command.ExecuteNonQuery();
                }
            }
        }

        public static List<CommandSet> LoadCommandSets()
        {
            var commandSets = new Dictionary<string, CommandSet>();

            using (var connection = new SqliteConnection(ConnectionString))
            {
                connection.Open();
                var command = connection.CreateCommand();
                command.CommandText = "SELECT CanID, DLC, Payload, CycleTime, RxTX, Command, Comment, LoopCount FROM Kyburz";

                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var canMessage = new CanMessage
                        {
                            CanID = reader.GetInt32(0),
                            DLC = (byte)reader.GetInt32(1),
                            // Payload aus HEX-String in Byte-Array konvertieren
                            Payload = HexStringToByteArray(reader.GetString(2)),
                            CycleTime = reader.GetInt32(3),
                            RxTX = reader.GetString(4),
                            Command = reader.GetString(5),
                            Comment = reader.GetString(6),
                            LoopCount = reader.GetInt32(7)
                        };

                        if (!commandSets.ContainsKey(canMessage.Command))
                        {
                            commandSets.Add(canMessage.Command, new CommandSet { Command = canMessage.Command });
                        }
                        commandSets[canMessage.Command].MessagesToSend.Add(canMessage);
                    }
                }
            }

            return new List<CommandSet>(commandSets.Values);
        }

        // Hilfsmethode zur Konvertierung eines Hex-Strings in ein Byte-Array
        private static byte[] HexStringToByteArray(string hex)
        {
            if (string.IsNullOrEmpty(hex)) return new byte[0];
            // Entfernt Leerzeichen
            hex = hex.Replace(" ", "");
            int numberChars = hex.Length;
            byte[] bytes = new byte[numberChars / 2];
            for (int i = 0; i < numberChars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }
    }
}

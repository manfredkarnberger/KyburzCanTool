using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace KyburzCanTool
{
    public class BooleanToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isTrue && isTrue)
            {
                // Wenn aktiv, zeige hellgrün (LED AN)
                return Brushes.LimeGreen;
            }
            else
            {
                // Wenn inaktiv, zeige dunkelgrau oder schwarz (LED AUS)
                return Brushes.DarkGray;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}

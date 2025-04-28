using Microsoft.UI.Xaml.Data;
using System;

namespace SolutionManager
{
    public class IsFavoriteConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (bool)value ? "🌟" : "☆"; // Star for favorite, empty star otherwise
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class IsFavoriteForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (bool)value ? "Yellow" : "White";
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class JobStatusWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (string)value == "In Progress" ? "0" : "40";
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class JobStatusVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return (string)value == "In Progress" ? "Collapsed" : "Visible";
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class JobStatusTooltipConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var tooltip = "Running jobs cannot be cancelled.";
            if (value is string status)
            {
                if (status == "Waiting")
                {
                    tooltip = "Cancel Job";
                }
                else if (status != "In Progress")
                {
                    tooltip = "Clear Job from History";
                }
            }
            return tooltip;
        }
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}

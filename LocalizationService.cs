using System;
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;

namespace VolumeLock;

public static class LocalizationService
{
    private static readonly ResourceLoader Loader = new();

    public static string GetString(string key)
    {
        try
        {
            return Loader.GetString(key);
        }
        catch
        {
            return key;
        }
    }

    public static string GetFormatted(string key, params object[] args)
    {
        var format = GetString(key);

        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, args);
        }
        catch
        {
            return format;
        }
    }
}

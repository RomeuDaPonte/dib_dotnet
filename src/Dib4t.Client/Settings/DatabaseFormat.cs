using System.Globalization;
using System.Text;
using Dib4t.SharedKernel;

namespace Dib4t.Client.Settings;

/// <summary>The texts of the Base de Dados tab as the legacy page writes them (<c>settings/databaseBackup.jsx</c>).</summary>
public static class DatabaseFormat
{
    public const string Missing = "—";
    public const string BackupExtension = ".dibbak";

    private static readonly string[] Units = ["KB", "MB", "GB", "TB"];

    /// <summary>Legacy <c>formatBytes</c>: <c>512 B</c>, <c>1.50 KB</c>, <c>12.3 MB</c> (two decimals below 10, else one; <c>toFixed</c>).</summary>
    public static string Bytes(double? bytes)
    {
        if (bytes is not { } value || double.IsNaN(value))
        {
            return Missing;
        }

        if (value < 1024)
        {
            return $"{JsNumber.ToString(value)} B";
        }

        value /= 1024;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{JsNumber.ToFixed(value, value < 10 ? 2 : 1)} {Units[unit]}";
    }

    /// <summary>
    /// Legacy <c>formatNumber</c>: <c>toLocaleString("pt-PT")</c> as Chrome writes it (CLDR pt-PT): no group separator
    /// below 10 000, then groups of three with a no-break space; comma decimals, at most three.
    /// </summary>
    public static string Number(double? number)
    {
        if (number is not { } value || double.IsNaN(value))
        {
            return Missing;
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "∞" : "-∞";
        }

        var rounded = Math.Round((decimal)Math.Abs(value), 3, MidpointRounding.AwayFromZero);
        var text = rounded.ToString("0.###", CultureInfo.InvariantCulture);
        var parts = text.Split('.');
        var integer = parts[0];
        var result = new StringBuilder();
        if (value < 0 && rounded != 0)
        {
            result.Append('-');
        }

        if (integer.Length >= 5)
        {
            for (var i = 0; i < integer.Length; i++)
            {
                if (i > 0 && (integer.Length - i) % 3 == 0)
                {
                    result.Append(' ');
                }

                result.Append(integer[i]);
            }
        }
        else
        {
            result.Append(integer);
        }

        if (parts.Length > 1)
        {
            result.Append(',').Append(parts[1]);
        }

        return result.ToString();
    }

    /// <summary>Legacy <c>dib4t-backup-${moment().format("YYYY-MM-DD_HH-mm-ss")}.dibbak</c>: the browser's local time.</summary>
    public static string BackupFileName(DateTimeOffset localNow) =>
        $"dib4t-backup-{localNow.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture)}{BackupExtension}";

    /// <summary>Legacy <c>moment(date).format(format)</c> in the browser's zone (.NET format text).</summary>
    public static string Local(DateTimeOffset instant, TimeZoneInfo zone, string format) =>
        TimeZoneInfo.ConvertTime(instant, zone).ToString(format, CultureInfo.InvariantCulture);

    /// <summary>Legacy file check: the name ends with <c>.dibbak</c>, any case.</summary>
    public static bool IsBackupFile(string name) => name.EndsWith(BackupExtension, StringComparison.OrdinalIgnoreCase);
}

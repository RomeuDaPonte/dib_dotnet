using Dib4t.Client.Settings;

namespace Dib4t.Client.Tests.Settings;

/// <summary>
/// The texts of the Base de Dados tab against the legacy functions run in Node 16 (<c>formatBytes</c>, and
/// <c>toLocaleString("pt-PT")</c> as Chrome writes it).
/// </summary>
public sealed class DatabaseFormatTests
{
    [Theory]
    [InlineData(null, "—")]
    [InlineData(double.NaN, "—")]
    [InlineData(0d, "0 B")]
    [InlineData(412.5, "412.5 B")]
    [InlineData(1000d, "1000 B")]
    [InlineData(1023d, "1023 B")]
    [InlineData(1024d, "1.00 KB")]
    [InlineData(1536d, "1.50 KB")]
    [InlineData(500_000d, "488.3 KB")]
    [InlineData(581_632d, "568.0 KB")]
    [InlineData(1_000_000d, "976.6 KB")]
    [InlineData(1_960_437d, "1.87 MB")]
    [InlineData(4_300_000d, "4.10 MB")]
    [InlineData(10_485_759d, "10.00 MB")] // 9.99… < 10: two decimals, rounded up by toFixed
    [InlineData(12_680_000d, "12.1 MB")]
    [InlineData(1_073_741_824d, "1.00 GB")]
    [InlineData(2_199_023_255_552_000d, "2000.0 TB")]
    public void Bytes_is_the_legacy_formatBytes(double? bytes, string text) => DatabaseFormat.Bytes(bytes).Should().Be(text);

    [Theory]
    [InlineData(null, "—")]
    [InlineData(double.NaN, "—")]
    [InlineData(0d, "0")]
    [InlineData(999d, "999")]
    [InlineData(1234d, "1234")]
    [InlineData(9999d, "9999")]
    [InlineData(10_000d, "10 000")]
    [InlineData(30_754d, "30 754")]
    [InlineData(1_234_567d, "1 234 567")]
    [InlineData(-12_345d, "-12 345")]
    [InlineData(1.5, "1,5")]
    [InlineData(1234.5678, "1234,568")]
    [InlineData(12_345.6789, "12 345,679")]
    public void Number_is_toLocaleString_pt_PT(double? number, string text) => DatabaseFormat.Number(number).Should().Be(text);

    [Fact]
    public void The_backup_file_name_is_the_local_time_as_legacy_moment_writes_it() =>
        DatabaseFormat.BackupFileName(new DateTimeOffset(2026, 3, 29, 1, 2, 3, 456, TimeSpan.FromHours(1)))
            .Should().Be("dib4t-backup-2026-03-29_01-02-03.dibbak");

    [Theory]
    [InlineData("x.dibbak", true)]
    [InlineData("x.DIBBAK", true)]
    [InlineData("x.dibbak.gz", false)]
    [InlineData("dibbak", false)]
    public void A_backup_file_ends_with_dibbak_in_any_case(string name, bool backup) => DatabaseFormat.IsBackupFile(name).Should().Be(backup);
}

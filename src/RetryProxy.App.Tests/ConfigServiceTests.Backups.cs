using System;
using System.IO;
using System.Text;
using Xunit;

namespace RetryProxy.App.Tests;

public sealed partial class ConfigServiceTests
{
    [Fact]
    public void ReadFailureBackupIncludesLatestCommittedWalTransaction()
    {
        var initial = Rows(Sample());
        Seed(initial);
        using var live = Connect();
        Execute(live, "PRAGMA wal_autocheckpoint=0; PRAGMA wal_checkpoint(TRUNCATE);");
        Execute(live, """
            UPDATE config SET value='{"runForVersion":"WAL 最新提交"}' WHERE key='common';
            DELETE FROM config WHERE key='other';
            """);
        Assert.True(new FileInfo(DbPath + "-wal").Length > 0);
        var latest = ReadRows();
        // 主文件仍是提交前的四行，证明本测试没有意外先把 WAL 写回主文件。
        var mainOnly = Path.Combine(_directory, "main-only.db");
        File.Copy(DbPath, mainOnly);
        AssertRows(initial, ReadRows(mainOnly));

        AssertBlocked(Open());

        var backup = Assert.Single(Backups("config_*.db.bak"));
        AssertRows(latest, ReadRows(backup));
        Assert.False(File.Exists(backup + "-wal"));
        AssertRows(latest, ReadRows());
        Assert.Contains(_notifications, message => message.Contains("原文件已备份"));
    }

    private (byte[] Db, byte[] Wal) CorruptDatabase()
    {
        var db = Encoding.UTF8.GetBytes("故障数据库内容，不能被 SQLite 解析");
        var wal = Encoding.UTF8.GetBytes("故障 WAL 内容，需要与主文件一起保留");
        File.WriteAllBytes(DbPath, db);
        File.WriteAllBytes(DbPath + "-wal", wal);
        return (db, wal);
    }

    [Fact]
    public void UnreadableDatabasePreservesBothDbAndWalEvidence()
    {
        var original = CorruptDatabase();
        AssertBlocked(Open());
        var backup = Assert.Single(Backups("config_*.db.bak"));
        Assert.Equal(original.Db, File.ReadAllBytes(backup));
        Assert.Equal(original.Wal, File.ReadAllBytes(backup + "-wal"));
        Assert.Equal(original.Db, ReadSharedBytes(DbPath));
        Assert.Equal(original.Wal, ReadSharedBytes(DbPath + "-wal"));
        Assert.Contains(_notifications, message => message.Contains("原文件已备份"));
    }

    [Fact]
    public void WalCopyFailureDoesNotClaimSuccessfulBackup()
    {
        var original = CorruptDatabase();
        using (var lockedWal = new FileStream(DbPath + "-wal", FileMode.Open, FileAccess.Read, FileShare.None))
        {
            AssertBlocked(Open());
        }
        Assert.Empty(Backups("config_*.db.bak"));
        Assert.Empty(Directory.GetFiles(BackupDirectory));
        Assert.Contains(_notifications, message => message.Contains("备份未完成"));
        Assert.DoesNotContain("原文件已备份", ReadLog());
        Assert.Equal(original.Db, ReadSharedBytes(DbPath));
        Assert.Equal(original.Wal, ReadSharedBytes(DbPath + "-wal"));
    }

    [Fact]
    public void UnwritableBackupDirectoryKeepsReadFailureBlockedWithoutSuccessNotice()
    {
        var rows = Rows(Sample());
        rows["proxy"] = "null";
        Seed(rows);
        BlockBackupDirectory();
        AssertBlocked(Open());
        AssertRows(rows, ReadRows());
        Assert.Contains(_notifications, message => message.Contains("备份未完成"));
        Assert.DoesNotContain("原文件已备份", ReadLog());
    }

    [Fact]
    public void FaultBackupRetentionRemovesWalAlongWithExpiredDatabase()
    {
        Directory.CreateDirectory(BackupDirectory);
        for (var i = 0; i < 5; i++)
        {
            var path = Path.Combine(BackupDirectory, $"config_20000101_000000_00{i}.db.bak");
            File.WriteAllText(path, "旧故障数据库");
            File.WriteAllText(path + "-wal", "旧故障日志");
        }
        var clientBackup = Path.Combine(BackupDirectory, "client-original.bak");
        File.WriteAllText(clientBackup, "客户端独立备份");
        CorruptDatabase();
        AssertBlocked(Open());
        Assert.Equal(5, Backups("config_*.db.bak").Length);
        Assert.Equal(5, Backups("config_*.db.bak-wal").Length);
        Assert.False(File.Exists(Path.Combine(BackupDirectory, "config_20000101_000000_000.db.bak-wal")));
        Assert.True(File.Exists(clientBackup));
    }
}

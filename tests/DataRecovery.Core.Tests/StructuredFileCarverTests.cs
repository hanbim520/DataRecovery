using System.Buffers.Binary;
using DataRecovery.Core.FileSystems;

namespace DataRecovery.Core.Tests;

public sealed class StructuredFileCarverTests
{
    [Fact]
    public void FindsBmpRiffSevenZipAndSqliteUsingDeclaredLengths()
    {
        var data = new byte[8192];
        WriteBmp(data.AsSpan(100), 1000);
        WriteRiff(data.AsSpan(1500), "WEBP", 500);
        WriteSevenZip(data.AsSpan(2500), 700);
        WriteSqlite(data.AsSpan(4000), pageSize: 512, pageCount: 6);

        var files = StructuredFileCarver.FindCandidates(data, 10_000);

        Assert.Collection(
            files.OrderBy(file => file.Offset),
            file => AssertFile(file, 10_100, 1000, ".bmp"),
            file => AssertFile(file, 11_500, 500, ".webp"),
            file => AssertFile(file, 12_500, 700, ".7z"),
            file => AssertFile(file, 14_000, 3072, ".sqlite"));
    }

    [Fact]
    public void RejectsInvalidDeclaredLengthsAndSourceOverflow()
    {
        var data = new byte[2048];
        WriteBmp(data, 4096);
        data[100] = (byte)'R';
        data[101] = (byte)'I';
        data[102] = (byte)'F';
        data[103] = (byte)'F';

        Assert.Empty(StructuredFileCarver.FindCandidates(data, 0, sourceLength: 2048));
    }

    [Fact]
    public void FindsLittleEndianElfFromProgramHeaders()
    {
        var data = new byte[4096];
        data[0] = 0x7F;
        data[1] = (byte)'E';
        data[2] = (byte)'L';
        data[3] = (byte)'F';
        data[4] = 2;
        data[5] = 1;
        data[6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(16, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(18, 2), 0x3E);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(32, 8), 64);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(54, 2), 56);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(56, 2), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(64 + 8, 8), 512);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(64 + 32, 8), 2048);

        var file = Assert.Single(StructuredFileCarver.FindCandidates(data, 5000));

        AssertFile(file, 5000, 2560, ".elf");
    }

    private static void WriteBmp(Span<byte> data, uint size)
    {
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(2, 4), size);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(10, 4), 54);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(14, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(18, 4), 10);
        BinaryPrimitives.WriteInt32LittleEndian(data.Slice(22, 4), 10);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(26, 2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(28, 2), 24);
    }

    private static void WriteRiff(Span<byte> data, string type, uint totalSize)
    {
        "RIFF"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(4, 4), totalSize - 8);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(data.Slice(8, 4));
    }

    private static void WriteSevenZip(Span<byte> data, ulong totalSize)
    {
        byte[] signature = [0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C];
        signature.CopyTo(data);
        data[6] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(12, 8), totalSize - 32);
        BinaryPrimitives.WriteUInt64LittleEndian(data.Slice(20, 8), 0);
    }

    private static void WriteSqlite(Span<byte> data, ushort pageSize, uint pageCount)
    {
        "SQLite format 3\0"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16BigEndian(data.Slice(16, 2), pageSize);
        BinaryPrimitives.WriteUInt32BigEndian(data.Slice(28, 4), pageCount);
    }

    private static void AssertFile(
        DataRecovery.Core.Models.RecoveredFile file,
        long offset,
        long size,
        string extension)
    {
        Assert.Equal(offset, file.Offset);
        Assert.Equal(size, file.Size);
        Assert.EndsWith(extension, file.Name);
    }
}

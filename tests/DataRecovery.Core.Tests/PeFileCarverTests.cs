using System.Buffers.Binary;
using DataRecovery.Core.FileSystems;
using DataRecovery.Core.Models;
using DataRecovery.Core.Services;

namespace DataRecovery.Core.Tests;

public sealed class PeFileCarverTests
{
    [Fact]
    public void FindsStructurallyValidPeAndUsesSectionAndCertificateLength()
    {
        var data = new byte[4096];
        const int relativeOffset = 128;
        WritePe(data.AsSpan(relativeOffset), dll: false, includeCertificate: true);

        var file = Assert.Single(PeFileCarver.FindCandidates(data, absoluteOffset: 10_000));

        Assert.Equal(10_128, file.Offset);
        Assert.Equal(2176, file.Size);
        Assert.Equal("程序", file.Category);
        Assert.EndsWith(".exe", file.Name);
        Assert.Equal("PE/EXE 结构", file.Signature);
    }

    [Fact]
    public void RejectsIncidentalMzWithoutValidPeStructure()
    {
        var data = new byte[2048];
        data[100] = (byte)'M';
        data[101] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(100 + 0x3C, 4), 64);

        Assert.Empty(PeFileCarver.FindCandidates(data, 0));
    }

    [Fact]
    public void IdentifiesDllAndRejectsCandidatePastSourceEnd()
    {
        var data = new byte[4096];
        WritePe(data, dll: true, includeCertificate: false);

        var file = Assert.Single(PeFileCarver.FindCandidates(data, 0, sourceLength: 4096));
        Assert.EndsWith(".dll", file.Name);
        Assert.Empty(PeFileCarver.FindCandidates(data, 3000, sourceLength: 4096));
    }

    [Fact]
    public async Task RecoveryScannerFindsPeWithoutFileSystemMetadata()
    {
        var data = new byte[4096];
        WritePe(data.AsSpan(512), dll: false, includeCertificate: false);
        var path = Path.Combine(Path.GetTempPath(), $"pe-carver-route-{Guid.NewGuid():N}.img");
        try
        {
            await File.WriteAllBytesAsync(path, data);

            var result = await new RecoveryScanner(new FileSystemDetector())
                .ScanAsync(path, ScanMode.LostFiles);

            Assert.Equal(FileSystemKind.Unknown, result.FileSystem.Kind);
            var file = Assert.Single(result.Files, candidate => candidate.Signature == "PE/EXE 结构");
            Assert.Equal(512, file.Offset);
            Assert.Equal("程序", file.Category);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void WritePe(Span<byte> data, bool dll, bool includeCertificate)
    {
        data[0] = (byte)'M';
        data[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(0x3C, 4), 0x80);
        "PE\0\0"u8.CopyTo(data.Slice(0x80, 4));

        const int coff = 0x84;
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(coff, 2), 0x8664);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(coff + 2, 2), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(coff + 16, 2), 0xF0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            data.Slice(coff + 18, 2),
            (ushort)(0x0002 | (dll ? 0x2000 : 0)));

        const int optional = coff + 20;
        BinaryPrimitives.WriteUInt16LittleEndian(data.Slice(optional, 2), 0x20B);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(optional + 32, 4), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(optional + 36, 4), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(optional + 56, 4), 12_288);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(optional + 60, 4), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(optional + 108, 4), 16);
        if (includeCertificate)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(optional + 112 + 4 * 8, 4), 2048);
            BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(optional + 112 + 4 * 8 + 4, 4), 128);
        }

        var sections = optional + 0xF0;
        ".text\0\0\0"u8.CopyTo(data.Slice(sections, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(sections + 16, 4), 512);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(sections + 20, 4), 512);
        ".rdata\0\0"u8.CopyTo(data.Slice(sections + 40, 8));
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(sections + 40 + 16, 4), 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(sections + 40 + 20, 4), 1024);
    }
}

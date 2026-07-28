using System.Collections.Specialized;
using System.Buffers.Binary;
using System.Reflection;
using System.Reflection.Metadata;
using BitmapSource = Jalium.UI.Media.Imaging.BitmapSource;
using ClipboardBackend = Jalium.UI.Controls.Platform.ClipboardPlatform;

namespace Jalium.UI.Tests;

[Collection("Application")]
public sealed class ClipboardParityTests
{
    [Fact]
    public void CanonicalSurface_UsesWpfReturnAndParameterTypes()
    {
        Assert.Equal("Jalium.UI", typeof(Clipboard).Namespace);
        Assert.True(typeof(Clipboard).IsAbstract && typeof(Clipboard).IsSealed);
        AssertMethod(nameof(Clipboard.Clear), typeof(void));
        AssertMethod(nameof(Clipboard.ContainsAudio), typeof(bool));
        AssertMethod(nameof(Clipboard.ContainsData), typeof(bool), typeof(string));
        AssertMethod(nameof(Clipboard.ContainsText), typeof(bool), typeof(TextDataFormat));
        AssertMethod(nameof(Clipboard.Flush), typeof(void));
        AssertMethod(nameof(Clipboard.GetAudioStream), typeof(Stream));
        AssertMethod(nameof(Clipboard.GetData), typeof(object), typeof(string));
        AssertMethod(nameof(Clipboard.GetDataObject), typeof(IDataObject));
        AssertMethod(nameof(Clipboard.GetFileDropList), typeof(StringCollection));
        AssertMethod(nameof(Clipboard.GetImage), typeof(BitmapSource));
        AssertMethod(nameof(Clipboard.GetText), typeof(string));
        AssertMethod(nameof(Clipboard.GetText), typeof(string), typeof(TextDataFormat));
        AssertMethod(nameof(Clipboard.IsCurrent), typeof(bool), typeof(IDataObject));
        AssertMethod(nameof(Clipboard.SetAudio), typeof(void), typeof(byte[]));
        AssertMethod(nameof(Clipboard.SetAudio), typeof(void), typeof(Stream));
        AssertMethod(nameof(Clipboard.SetData), typeof(void), typeof(string), typeof(object));
        AssertGenericMethod(nameof(Clipboard.SetDataAsJson), typeof(void), parameterCount: 2);
        AssertMethod(nameof(Clipboard.SetDataObject), typeof(void), typeof(object));

        var copyOverload = AssertMethod(
            nameof(Clipboard.SetDataObject),
            typeof(void),
            typeof(object),
            typeof(bool));
        Assert.False(copyOverload.GetParameters()[1].HasDefaultValue);

        AssertMethod(nameof(Clipboard.SetFileDropList), typeof(void), typeof(StringCollection));
        AssertMethod(nameof(Clipboard.SetImage), typeof(void), typeof(BitmapSource));
        AssertMethod(nameof(Clipboard.SetText), typeof(void), typeof(string));
        AssertMethod(nameof(Clipboard.SetText), typeof(void), typeof(string), typeof(TextDataFormat));
        Assert.Equal(2, typeof(Clipboard).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Count(method => method.Name == nameof(Clipboard.TryGetData) && method.IsGenericMethodDefinition));

        Assert.Null(typeof(Clipboard).Assembly.GetType(
            "Jalium.UI.Controls.Clipboard", throwOnError: false));
        Assert.Null(typeof(Clipboard).Assembly.GetType(
            "Jalium.UI.Controls.ClipboardDataObject", throwOnError: false));
    }

    [Fact]
    public void LegacyControlsClipboardDataTypes_AreNeitherExportedNorForwarded()
    {
        Assert.Equal("PersistentObject", DataFormats.Serializable);

        Assembly implementation = typeof(Clipboard).Assembly;
        Assert.Null(implementation.GetType("Jalium.UI.Controls.Clipboard", throwOnError: false));
        Assert.Null(implementation.GetType("Jalium.UI.Controls.ClipboardDataObject", throwOnError: false));
        Assert.Null(implementation.GetType("Jalium.UI.Controls.DataFormats", throwOnError: false));
        Assert.Null(implementation.GetType("Jalium.UI.Controls.IDataObject", throwOnError: false));

        Assembly facade = Assembly.Load("Jalium.UI.Controls");
        Assert.Contains(typeof(Clipboard), facade.GetForwardedTypes());
        Assert.DoesNotContain(
            facade.GetForwardedTypes(),
            type => type.FullName is
                "Jalium.UI.Controls.Clipboard" or
                "Jalium.UI.Controls.ClipboardDataObject" or
                "Jalium.UI.Controls.DataFormats" or
                "Jalium.UI.Controls.IDataObject");
    }

    [Fact]
    public void CustomData_RoundTripsThroughTypedClipboardAndTracksOwnership()
    {
        const string format = "Jalium.UI.Tests.Clipboard.Payload";
        var payload = new Payload("alpha", 42);
        var dataObject = new DataObject();
        dataObject.SetData(format, payload);

        RunClipboardTest(() =>
        {
            Clipboard.SetDataObject(dataObject, copy: true);

            Assert.True(Clipboard.IsCurrent(dataObject));
            Assert.True(Clipboard.ContainsData(format));
            Assert.Same(payload, Clipboard.GetData(format));
            Assert.True(Clipboard.TryGetData(format, out Payload? roundTrip));
            Assert.Same(payload, roundTrip);
            Assert.True(Clipboard.TryGetData(
                format,
                static (TypeName _) => typeof(Payload),
                out Payload? resolved));
            Assert.Same(payload, resolved);
        });
    }

    [Fact]
    public void JsonData_RoundTripsWithoutReducingTheValueToText()
    {
        const string format = "Jalium.UI.Tests.Clipboard.JsonPayload";
        var payload = new Payload("json", 7);

        RunClipboardTest(() =>
        {
            Clipboard.SetDataAsJson(format, payload);

            Assert.True(Clipboard.ContainsData(format));
            Assert.True(Clipboard.TryGetData(format, out Payload? roundTrip));
            Assert.Equal(payload, roundTrip);
        });
    }

    [Fact]
    public void JsonData_FromAnExternalClipboardPayload_IsRecoveredByTypedApi()
    {
        if (!OperatingSystem.IsWindows())
            return;

        const string format = "Jalium.UI.Tests.Clipboard.ExternalJsonPayload";
        var payload = new Payload("external", 11);
        RunClipboardTest(() =>
        {
            Clipboard.Clear();
            Assert.True(ClipboardBackend.SetBinaryData(
                format,
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(payload)));

            Assert.True(Clipboard.TryGetData(format, out Payload? roundTrip));
            Assert.Equal(payload, roundTrip);
        });
    }

    [Fact]
    public void LinuxMimeMapping_CoversWpfFormatsAndReversesRegisteredNames()
    {
        Assert.Equal(
            ["text/plain;charset=utf-8", "text/plain", "UTF8_STRING"],
            ClipboardBackend.GetMimeTypesForFormat(DataFormats.UnicodeText));
        Assert.Equal(["text/html"], ClipboardBackend.GetMimeTypesForFormat(DataFormats.Html));
        Assert.Equal(
            ["text/rtf", "application/rtf"],
            ClipboardBackend.GetMimeTypesForFormat(DataFormats.Rtf));
        Assert.Equal(["text/uri-list"], ClipboardBackend.GetMimeTypesForFormat(DataFormats.FileDrop));
        Assert.Equal(["image/png"], ClipboardBackend.GetMimeTypesForFormat(DataFormats.Bitmap));
        Assert.Equal(["application/vnd.jalium.test"],
            ClipboardBackend.GetMimeTypesForFormat("application/vnd.jalium.test"));

        const string registeredFormat = "Jalium.UI.Tests.Registered Payload";
        string encodedMime = Assert.Single(ClipboardBackend.GetMimeTypesForFormat(registeredFormat));
        Assert.StartsWith("application/x-jalium-clipboard-format-", encodedMime);
        Assert.Equal(registeredFormat, ClipboardBackend.GetFormatForMimeType(encodedMime));
        Assert.Equal(DataFormats.UnicodeText,
            ClipboardBackend.GetFormatForMimeType("text/plain; charset=UTF-8"));
    }

    [Fact]
    public void LinuxUriList_UsesFileUrisCommentsAndPercentEscapes()
    {
        string first = Path.Combine(Path.GetTempPath(), "clipboard space.txt");
        string second = Path.Combine(Path.GetTempPath(), "clipboard-中文.txt");
        string encoded = ClipboardBackend.BuildUriList([first, second]);

        Assert.Contains("%20", encoded);
        Assert.EndsWith("\r\n", encoded);

        string external = "# generated by a peer\r\n" + encoded + "https://example.invalid/not-a-file\r\n";
        string[] parsed = ClipboardBackend.ParseUriList(System.Text.Encoding.UTF8.GetBytes(external));
        Assert.Equal([Path.GetFullPath(first), Path.GetFullPath(second)], parsed);
    }

    [Fact]
    public void LinuxPngEncoding_ProducesRgbaPngWithDeclaredDimensions()
    {
        byte[] bgra =
        [
            0x30, 0x20, 0x10, 0xFF,
            0x60, 0x50, 0x40, 0x80,
        ];
        byte[] png = ClipboardBackend.EncodePng(2, 1, 8, bgra);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(2, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16, 4)));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20, 4)));
        Assert.Equal(8, png[24]);
        Assert.Equal(6, png[25]);
        Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
    }

    [Fact]
    public unsafe void UnicodeClipboardDecoder_RequiresBoundedNullTerminatedUtf16()
    {
        char[] characters = ['O', 'K', '\0', 'X'];
        fixed (char* pointer = characters)
        {
            Assert.Equal(
                "OK",
                ClipboardBackend.DecodeUnicodeClipboardText(
                    (nint)pointer,
                    (nuint)(characters.Length * sizeof(char))));
            Assert.Null(ClipboardBackend.DecodeUnicodeClipboardText(
                (nint)pointer,
                2 * sizeof(char)));
            Assert.Null(ClipboardBackend.DecodeUnicodeClipboardText((nint)pointer, 5));
            Assert.Null(ClipboardBackend.DecodeUnicodeClipboardText(
                (nint)pointer,
                (nuint)ClipboardBackend.MaxClipboardPayloadBytes + 2));
        }

        Assert.True(ClipboardBackend.TryGetUnicodeClipboardByteCount(0, out int emptyByteCount));
        Assert.Equal(sizeof(char), emptyByteCount);
        Assert.False(ClipboardBackend.TryGetUnicodeClipboardByteCount(int.MaxValue, out _));
    }

    [Fact]
    public unsafe void DibLayoutReader_RejectsTruncationOverflowAndOutOfRangePixels()
    {
        byte[] truncated = new byte[20];
        fixed (byte* pointer = truncated)
        {
            Assert.False(ClipboardBackend.TryReadClipboardDibLayout(
                (nint)pointer,
                (nuint)truncated.Length,
                out _));
        }

        byte[] dib = new byte[48];
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(0, 4), 40);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), 2);
        BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(8, 4), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(dib.AsSpan(14, 2), 32);
        fixed (byte* pointer = dib)
        {
            Assert.True(ClipboardBackend.TryReadClipboardDibLayout(
                (nint)pointer,
                (nuint)dib.Length,
                out ClipboardBackend.ClipboardDibLayout layout));
            Assert.Equal(2, layout.Width);
            Assert.Equal(1, layout.AbsoluteHeight);
            Assert.Equal(8, layout.SourceStride);
            Assert.Equal(8, layout.SourceDataSize);

            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), int.MaxValue);
            Assert.False(ClipboardBackend.TryReadClipboardDibLayout(
                (nint)pointer,
                nuint.MaxValue,
                out _));

            BinaryPrimitives.WriteInt32LittleEndian(dib.AsSpan(4, 4), 2);
            Assert.False(ClipboardBackend.TryReadClipboardDibLayout(
                (nint)pointer,
                47,
                out _));
        }
    }

    [Fact]
    public void ImageLayoutAndPngHeader_RejectOversizedOrOverflowingAllocations()
    {
        Assert.True(ClipboardBackend.TryGetClipboardImageLayout(
            2,
            3,
            12,
            out int packedStride,
            out int sourceByteCount,
            out int packedByteCount));
        Assert.Equal(8, packedStride);
        Assert.Equal(36, sourceByteCount);
        Assert.Equal(24, packedByteCount);

        Assert.False(ClipboardBackend.TryGetClipboardImageLayout(
            int.MaxValue,
            2,
            int.MaxValue,
            out _,
            out _,
            out _));
        Assert.False(ClipboardBackend.TryGetClipboardImageLayout(
            8192,
            8192,
            8192 * 4,
            out _,
            out _,
            out _));

        byte[] png = ClipboardBackend.EncodePng(
            1,
            1,
            4,
            [0x30, 0x20, 0x10, 0xFF]);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16, 4), int.MaxValue);
        Assert.False(ClipboardBackend.TryReadPngBgraLayout(
            png,
            out _,
            out _,
            out _,
            out _));
        Assert.Null(ClipboardBackend.DecodePng(png));
    }

    [Fact]
    public void FileDropPayloadSizing_RejectsInvalidPathsAndUnboundedLists()
    {
        Assert.True(ClipboardBackend.TryGetFileDropPayloadSize(
            [@"C:\one.txt", @"D:\two.txt"],
            out int validSize));
        Assert.InRange(validSize, 1, ClipboardBackend.MaxClipboardPayloadBytes);

        Assert.False(ClipboardBackend.TryGetFileDropPayloadSize([null!], out _));
        Assert.False(ClipboardBackend.TryGetFileDropPayloadSize(["bad\0path"], out _));
        Assert.False(ClipboardBackend.TryGetFileDropPayloadSize(
            [new string('x', ClipboardBackend.MaxClipboardPathChars + 1)],
            out _));
        Assert.False(ClipboardBackend.TryGetFileDropPayloadSize(
            Enumerable.Repeat("x", ClipboardBackend.MaxClipboardFileCount + 1).ToArray(),
            out _));

        string largePath = new('x', ClipboardBackend.MaxClipboardPathChars);
        int repeatedPathCount =
            ClipboardBackend.MaxClipboardPayloadBytes / ((largePath.Length + 1) * sizeof(char)) + 1;
        Assert.False(ClipboardBackend.TryGetFileDropPayloadSize(
            Enumerable.Repeat(largePath, repeatedPathCount).ToArray(),
            out _));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(6)]
    public void InvalidTextDataFormat_IsRejected(int value)
    {
        var format = (TextDataFormat)value;
        Assert.Throws<System.ComponentModel.InvalidEnumArgumentException>(() => Clipboard.ContainsText(format));
        Assert.Throws<System.ComponentModel.InvalidEnumArgumentException>(() => Clipboard.GetText(format));
        Assert.Throws<System.ComponentModel.InvalidEnumArgumentException>(() => Clipboard.SetText("value", format));
    }

    private static MethodInfo AssertMethod(string name, Type returnType, params Type[] parameterTypes)
    {
        var method = typeof(Clipboard).GetMethod(
            name,
            BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly,
            null,
            parameterTypes,
            null);
        Assert.NotNull(method);
        Assert.Equal(returnType, method!.ReturnType);
        return method;
    }

    private static MethodInfo AssertGenericMethod(string name, Type returnType, int parameterCount)
    {
        MethodInfo method = Assert.Single(
            typeof(Clipboard).GetMethods(
                BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly),
            candidate => candidate.Name == name && candidate.IsGenericMethodDefinition &&
                candidate.GetParameters().Length == parameterCount);
        Assert.Equal(returnType, method.ReturnType);
        return method;
    }

    private static void RunClipboardTest(Action action)
    {
        if (!OperatingSystem.IsWindows())
        {
            action();
            return;
        }

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            IDataObject? original = null;
            try
            {
                original = Clipboard.GetDataObject();
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try
                {
                    if (original == null)
                        Clipboard.Clear();
                    else
                        Clipboard.SetDataObject(original, copy: true);
                }
                catch (Exception exception)
                {
                    // Preserve the assertion/operation failure rather than replacing it
                    // with a best-effort clipboard restoration failure.
                    failure ??= exception;
                }
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null)
            throw new AggregateException(failure);
    }

    private sealed record Payload(string Name, int Count);
}

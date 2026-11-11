using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.Assimp;
using AssimpFile = Silk.NET.Assimp.File;

namespace Wisko.Engine.Assets.Importing;

/// <summary>
/// Read-only Assimp file IO backed by .NET streams. Every file Assimp opens for an import (the model and any
/// .bin/.mtl it references) goes through System.IO, so Unicode paths behave the same on every platform, and
/// percent-encoded references (glTF URIs such as "my%20model.bin") fall back to their decoded name. An optional
/// <see cref="ModelFileAccessLog"/> (carried in the callback table's UserData) records every request and can confine
/// reads to one folder.
/// </summary>
internal static unsafe class AssimpFileSystem
{
    /// <summary>Creates the native callback table; free it with <see cref="Free"/> after the import.</summary>
    public static FileIO* Create(ModelFileAccessLog? accessLog = null)
    {
        var io = (FileIO*)NativeMemory.AllocZeroed((nuint)sizeof(FileIO));
        io->OpenProc = new PfnFileOpenProc(&Open);
        io->CloseProc = new PfnFileCloseProc(&Close);
        if (accessLog is not null)
        {
            io->UserData = (byte*)GCHandle.ToIntPtr(GCHandle.Alloc(accessLog));
        }

        return io;
    }

    public static void Free(FileIO* io)
    {
        if (io->UserData is not null)
        {
            GCHandle.FromIntPtr((IntPtr)io->UserData).Free();
        }

        NativeMemory.Free(io);
    }

    /// <summary>The path itself when it exists, else its percent-decoded form when that exists, else null.</summary>
    public static string? ResolveExisting(string path)
    {
        if (System.IO.File.Exists(path))
        {
            return path;
        }

        if (path.Contains('%'))
        {
            var decoded = Uri.UnescapeDataString(path);
            if (decoded != path && System.IO.File.Exists(decoded))
            {
                return decoded;
            }
        }

        return null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static AssimpFile* Open(FileIO* io, byte* fileName, byte* mode)
    {
        try
        {
            var openMode = Marshal.PtrToStringUTF8((IntPtr)mode) ?? "rb";
            var path = Marshal.PtrToStringUTF8((IntPtr)fileName);
            if (!openMode.StartsWith('r') || path is null)
            {
                return null; // imports only read
            }

            var accessLog = io->UserData is null ? null : GCHandle.FromIntPtr((IntPtr)io->UserData).Target as ModelFileAccessLog;
            var resolved = ResolveExisting(path);
            if (resolved is null)
            {
                accessLog?.RecordMissing(path);
                return null;
            }

            resolved = Path.GetFullPath(resolved);
            if (accessLog is not null && !accessLog.IsAllowed(resolved))
            {
                accessLog.RecordRefused(resolved);
                return null;
            }

            var stream = new FileStream(resolved, FileMode.Open, FileAccess.Read, FileShare.Read);
            accessLog?.RecordOpened(resolved);
            var file = (AssimpFile*)NativeMemory.AllocZeroed((nuint)sizeof(AssimpFile));
            file->ReadProc = new PfnFileReadProc(&Read);
            file->WriteProc = new PfnFileReadProc(&Write);
            file->TellProc = new PfnFileTellProc(&Tell);
            file->FileSizeProc = new PfnFileTellProc(&Size);
            file->SeekProc = new PfnFileSeek(&Seek);
            file->FlushProc = new PfnFileFlushProc(&Flush);
            file->UserData = (byte*)GCHandle.ToIntPtr(GCHandle.Alloc(stream));
            return file;
        }
        catch (Exception)
        {
            return null;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Close(FileIO* io, AssimpFile* file)
    {
        if (file is null)
        {
            return;
        }

        var handle = GCHandle.FromIntPtr((IntPtr)file->UserData);
        (handle.Target as Stream)?.Dispose();
        handle.Free();
        NativeMemory.Free(file);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Read(AssimpFile* file, byte* buffer, nuint size, nuint count)
    {
        try
        {
            if (size == 0 || count == 0)
            {
                return 0;
            }

            var stream = StreamOf(file);
            var span = new Span<byte>(buffer, checked((int)(size * count)));
            var total = 0;
            while (total < span.Length)
            {
                var read = stream.Read(span[total..]);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            return (nuint)total / size;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Write(AssimpFile* file, byte* buffer, nuint size, nuint count) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Tell(AssimpFile* file) => (nuint)StreamOf(file).Position;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nuint Size(AssimpFile* file) => (nuint)StreamOf(file).Length;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static Return Seek(AssimpFile* file, nuint offset, Origin origin)
    {
        try
        {
            var seekOrigin = origin switch
            {
                Origin.Cur => SeekOrigin.Current,
                Origin.End => SeekOrigin.End,
                _ => SeekOrigin.Begin
            };
            StreamOf(file).Seek((long)offset, seekOrigin);
            return Return.Success;
        }
        catch (Exception)
        {
            return Return.Failure;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void Flush(AssimpFile* file)
    {
    }

    private static Stream StreamOf(AssimpFile* file) => (Stream)GCHandle.FromIntPtr((IntPtr)file->UserData).Target!;
}

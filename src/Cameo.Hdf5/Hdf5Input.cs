using System.Runtime.CompilerServices;
using PureHDF;
using PureHDF.VOL.Native;

namespace Cameo.Hdf5;

/// <summary>Owns the stream even if PureHDF rejects a header before creating a NativeFile.</summary>
internal sealed class Hdf5Input : IDisposable
{
    private readonly FileStream _stream;
    internal NativeFile File { get; }

    private Hdf5Input(string path)
    {
        string absolutePath = Path.GetFullPath(path);
        _stream = System.IO.File.OpenRead(absolutePath);
        NativeFile? file = null;
        try
        {
            file = H5File.Open(_stream, leaveOpen: true);
            // PureHDF 2.2.0's stream API discards origin metadata. Restore exactly the
            // values its path API assigns, before any object/link lookup can observe it.
            // These typed accessors are intentionally pinned integration glue: upgrading
            // PureHDF requires the external-link and handle-release compatibility tests.
            FilePath(file) = absolutePath;
            FolderPath(file) = Path.GetDirectoryName(absolutePath);
            File = file;
        }
        catch
        {
            try { file?.Dispose(); }
            finally { _stream.Dispose(); }
            throw;
        }
    }

    internal static Hdf5Input OpenRead(string path) => new(path);

    public void Dispose()
    {
        try { File.Dispose(); }
        finally { _stream.Dispose(); }
    }

    // No reflection or process-wide directory/environment changes. The private field
    // contract is the deliberately limited compatibility cost of the pinned 2.2.0 shim.
    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<Path>k__BackingField")]
    private static extern ref string? FilePath(NativeFile file);

    [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "<FolderPath>k__BackingField")]
    private static extern ref string? FolderPath(NativeFile file);
}

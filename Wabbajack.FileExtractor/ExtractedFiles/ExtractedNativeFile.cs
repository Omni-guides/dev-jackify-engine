using System;
using System.Threading;
using System.Threading.Tasks;
using Wabbajack.Common;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;

namespace Wabbajack.FileExtractor.ExtractedFiles;

public class ExtractedNativeFile : NativeFileStreamFactory, IExtractedFile, IDisposable
{
    private bool _disposed = false;

    public ExtractedNativeFile(AbsolutePath file, IPath path) : base(file, path)
    {
    }

    public ExtractedNativeFile(AbsolutePath file) : base(file)
    {
    }

    public bool CanMove { get; set; } = true;

    public async ValueTask Move(AbsolutePath newPath, CancellationToken token)
    {
        if (CanMove)
        {
            await _file.MoveToAsync(newPath, true, token);
            // File has been moved, mark as disposed
            _disposed = true;
        }
        else
        {
            await _file.CopyToAsync(newPath, token);
        }
    }

    public void Dispose()
    {
        if (!_disposed && _file.FileExists())
        {
            try
            {
                _file.Delete();
            }
            catch
            {
                // Ignore cleanup errors
            }
        }
    }
}
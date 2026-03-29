using Microsoft.Extensions.Logging;
using System.IO;
using System.Net.Http;
using System;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using System.Runtime.InteropServices;
using Wabbajack.Hashing.xxHash64;
using Wabbajack.Paths;
using Wabbajack.Paths.IO;
using Wabbajack.RateLimiter;
using Wabbajack.Networking.Http.Interfaces;

namespace Wabbajack.Networking.Http;

public class ResumableDownloader(ILogger<ResumableDownloader> _logger, IHttpClientFactory _httpClientFactory, ITransferMetrics _metrics) : IHttpDownloader
{
    // P/Invoke for fsync() on Linux to ensure file data is synced to disk
    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);
    public async Task<Hash> Download(HttpRequestMessage _msg, AbsolutePath _outputPath, IJob job, CancellationToken token)
    {
        if (_msg.RequestUri == null)
        {
            throw new ArgumentException("Request URI is null");
        }

        try
        {
            return await DownloadAndHash(_msg, _outputPath, job, token, 5);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download '{name}'", _outputPath.FileName.ToString());

            throw;
        }
    }

    private async Task<Hash> DownloadAndHash(HttpRequestMessage msg, AbsolutePath filePath, IJob job, CancellationToken token, int retry = 5, bool reset = false)
    {
        // DIAGNOSTIC: Log retry state (DEBUG level - only visible with --debug flag)
        _logger.LogDebug("[DOWNLOAD_DIAG] DownloadAndHash for '{name}': retry={Retry}, reset={Reset}, fileExists={FileExists}, fileSize={FileSize}", 
            filePath.FileName.ToString(), retry, reset, filePath.FileExists(), filePath.FileExists() ? filePath.Size() : 0);

        try
        {
            if (reset)
            {
                _logger.LogDebug("[DOWNLOAD_DIAG] Resetting download for '{name}': deleting existing file", filePath.FileName.ToString());
                filePath.Delete();
            }

            var downloadedFilePath = await DownloadStreamDirectlyToFile(msg, filePath, job, token);

            // Ensure file handle is fully closed and file is synced before hashing
            // Small delay on Linux to ensure filesystem has fully processed the file
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                await Task.Delay(50, token); // 50ms delay to ensure file is fully synced
            }

            return await HashFile(downloadedFilePath, token);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            _logger.LogDebug("Failed to download '{name}' due to requested range not being satisfiable. Retrying from beginning...", filePath.FileName.ToString());

            if (retry == 0)
            {
                _logger.LogError(ex, "Failed to download '{name}'. Partial file will be resumed on next attempt.", filePath.FileName.ToString());
                
                // Don't delete partial files - allow resume on next attempt
                throw;
            }

            // Clone the HttpRequestMessage for retry to avoid "already sent" exception
            var clonedMsg = CloneHttpRequestMessage(msg);
            return await DownloadAndHash(clonedMsg, filePath, job, token, retry - 1, true);
        }
        catch (Exception ex) when (ex is SocketException || ex is IOException || ex is HttpRequestException)
        {
            // Match upstream: only catch SocketException, IOException, and HttpRequestException
            // TaskCanceledException bubbles up to DownloadDispatcher which handles it
            var fileSizeOnError = filePath.FileExists() ? filePath.Size() : 0;
            _logger.LogDebug("[DOWNLOAD_DIAG] Retry triggered for '{name}': exceptionType={ExType}, retriesRemaining={Retries}, fileSize={FileSize}, reset={Reset}", 
                filePath.FileName.ToString(), ex.GetType().Name, retry, fileSizeOnError, false);
            
            _logger.LogDebug("Failed to download '{name}' due to network error. Retrying... ({retries} retries remaining)", 
                filePath.FileName.ToString(), retry);

            if (retry == 0)
            {
                _logger.LogError(ex, "Failed to download '{name}' after all retries. Partial file will be resumed on next attempt.", filePath.FileName.ToString());
                
                // Don't delete partial files - allow resume on next attempt
                // Partial files are excluded from hash checking (only exact size matches are hashed)
                throw;
            }

            // Clone the HttpRequestMessage for retry to avoid "already sent" exception
            var clonedMsg = CloneHttpRequestMessage(msg);
            return await DownloadAndHash(clonedMsg, filePath, job, token, retry - 1);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download '{name}'. Partial file will be resumed on next attempt.", filePath.FileName.ToString());
            
            // Don't delete partial files - allow resume on next attempt
            throw;
        }
    }

    private async Task<AbsolutePath> DownloadStreamDirectlyToFile(HttpRequestMessage message, AbsolutePath filePath, IJob job, CancellationToken token)
    {
        if(job.Size == null) throw new ArgumentException("Job size must be set before downloading");

        var fileExistsBefore = filePath.FileExists();
        using Stream fileStream = GetDownloadFileStream(filePath);

        var startingPosition = fileStream.Length;

        // DIAGNOSTIC: Log initial state (DEBUG level - only visible with --debug flag)
        _logger.LogDebug("[DOWNLOAD_DIAG] Starting download for '{name}': fileExists={FileExists}, startingPosition={StartPos}, expectedJobSize={JobSize}, currentFileSize={FileSize}", 
            filePath.FileName.ToString(), fileExistsBefore, startingPosition, job.Size.Value, fileStream.Length);

        var httpClient = _httpClientFactory.CreateClient("ResumableClient");

        message.Headers.Range = new RangeHeaderValue(startingPosition, null);
        using var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, token);

        // DIAGNOSTIC: Log HTTP response details (DEBUG level - only visible with --debug flag)
        var statusCode = response.StatusCode;
        var responseContentLength = response.Content.Headers.ContentLength;
        _logger.LogDebug("[DOWNLOAD_DIAG] HTTP Response for '{name}': StatusCode={StatusCode}, ContentLength={ContentLength}, startingPosition={StartPos}", 
            filePath.FileName.ToString(), statusCode, responseContentLength, startingPosition);

        // Check for RequestedRangeNotSatisfiable (416) - server can't satisfy the range request
        // This happens when the file on server is smaller than our starting position, or the range is invalid
        // We need to throw an HttpRequestException so the catch block can reset the file and retry from beginning
        if (statusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            _logger.LogDebug("[DOWNLOAD_DIAG] RequestedRangeNotSatisfiable (416) for '{name}': startingPosition={StartPos}, expectedJobSize={JobSize}. Will reset and retry from beginning.", 
                filePath.FileName.ToString(), startingPosition, job.Size.Value);
            throw new HttpRequestException($"Requested range not satisfiable for '{filePath.FileName}'. Starting position {startingPosition} is beyond file size.", null, statusCode: statusCode);
        }

        if (responseContentLength != null) 
        {
            var expectedTotalSize = startingPosition + responseContentLength.Value;
            
            // DIAGNOSTIC: Log size calculation (DEBUG level - only visible with --debug flag)
            _logger.LogDebug("[DOWNLOAD_DIAG] Size calculation for '{name}': startingPosition={StartPos}, responseContentLength={RespLen}, expectedTotalSize={ExpectedTotal}, currentFileLength={FileLen}", 
                filePath.FileName.ToString(), startingPosition, responseContentLength.Value, expectedTotalSize, fileStream.Length);
            
            if (responseContentLength.Value == 0)
            {
                _logger.LogDebug("[DOWNLOAD_DIAG] ContentLength is 0 for '{name}', returning early", filePath.FileName.ToString());
                return filePath;
            }
            
            if (expectedTotalSize > fileStream.Length)
            {
                _logger.LogDebug("[DOWNLOAD_DIAG] Setting file length for '{name}': from {OldLen} to {NewLen}", 
                    filePath.FileName.ToString(), fileStream.Length, expectedTotalSize);
                fileStream.SetLength(expectedTotalSize);
            }
        }
        else
        {
            _logger.LogDebug("[DOWNLOAD_DIAG] ContentLength is null for '{name}'", filePath.FileName.ToString());
        }

        var responseStream = await response.Content.ReadAsStreamAsync(token);

        long reportProgressThreshold = 10 * 1024 * 1024; // Report progress every 10MB
        bool shouldReportProgress = job.Size > reportProgressThreshold;

        long reportEveryXBytesProcessed = Math.Max(1024 * 1024, job.Size.Value / 100); // Report every 1MB or 1% of file, whichever is larger
        long bytesProcessed = startingPosition;
        long lastReportedBytes = startingPosition;

        var buffer = new byte[2 * 1024 * 1024]; // 2MB buffer for better throughput
        int bytesRead;
        long totalBytesRead = 0;
        while ((bytesRead = await responseStream.ReadAsync(buffer, token)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), token);
            _metrics.Record(bytesRead);
            bytesProcessed += bytesRead;
            totalBytesRead += bytesRead;

            if (shouldReportProgress && (bytesProcessed - lastReportedBytes) >= reportEveryXBytesProcessed)
            {
                var delta = (int)(bytesProcessed - lastReportedBytes);
                await job.Report(delta, token);
                lastReportedBytes = bytesProcessed;
            }
        }

        // DIAGNOSTIC: Log bytes read from stream (DEBUG level - only visible with --debug flag)
        _logger.LogDebug("[DOWNLOAD_DIAG] Stream read complete for '{name}': totalBytesRead={BytesRead}, bytesProcessed={BytesProcessed}, startingPosition={StartPos}", 
            filePath.FileName.ToString(), totalBytesRead, bytesProcessed, startingPosition);

        // Report any remaining progress that didn't meet the threshold to ensure limiter accounts for all bytes
        if (bytesProcessed > lastReportedBytes)
        {
            var delta = (int)(bytesProcessed - lastReportedBytes);
            await job.Report(delta, token);
            lastReportedBytes = bytesProcessed;
        }

        // Ensure all data is written to disk before hashing (critical on Linux where page cache can delay writes)
        // This prevents hash mismatches when resuming installations, as files must be fully synced to disk
        await fileStream.FlushAsync(token);
        fileStream.Flush(); // Flush buffered data to OS
        
        // On Linux, explicitly sync file data to disk using fsync() to ensure the file is fully written
        // before we hash it. This is critical for small files where the timing window is smaller.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && fileStream is FileStream fs)
        {
            var handle = fs.SafeFileHandle;
            if (!handle.IsInvalid && !handle.IsClosed)
            {
                var fd = handle.DangerousGetHandle().ToInt32();
                var result = fsync(fd);
                if (result != 0)
                {
                    var error = Marshal.GetLastWin32Error();
                    _logger.LogWarning("fsync() failed for '{name}' with error {error}. File may not be fully synced to disk.", 
                        filePath.FileName.ToString(), error);
                }
            }
        }

        // DIAGNOSTIC: Log final file state before returning (DEBUG level - only visible with --debug flag)
        var finalFileSize = filePath.FileExists() ? filePath.Size() : 0;
        _logger.LogDebug("[DOWNLOAD_DIAG] Download complete for '{name}': finalFileSize={FinalSize}, expectedJobSize={JobSize}, bytesProcessed={BytesProcessed}, startingPosition={StartPos}", 
            filePath.FileName.ToString(), finalFileSize, job.Size.Value, bytesProcessed, startingPosition);

        return filePath;
    }

    private static async Task<Hash> HashFile(AbsolutePath filePath, CancellationToken token)
    {
        using var fileStream = filePath.Open(FileMode.Open, FileAccess.Read, FileShare.Read);
        
        // Ensure stream is at position 0 (should be by default, but be explicit)
        if (fileStream.Position != 0)
        {
            fileStream.Position = 0;
        }

        return await fileStream.Hash(token);
    }

    private static Stream GetDownloadFileStream(AbsolutePath filePath)
    {
        if (filePath.FileExists())
        {
            return filePath.Open(FileMode.Append, FileAccess.Write, FileShare.None);
        }
        else
        {
            return filePath.Open(FileMode.Create, FileAccess.Write, FileShare.None);
        }
    }

    /// <summary>
    /// Clones an HttpRequestMessage to avoid "already sent" exceptions during retries
    /// </summary>
    private static HttpRequestMessage CloneHttpRequestMessage(HttpRequestMessage original)
    {
        var cloned = new HttpRequestMessage(original.Method, original.RequestUri);
        
        // Copy headers
        foreach (var header in original.Headers)
        {
            cloned.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        
        // Don't copy content - it can only be read once and causes issues
        
        return cloned;
    }
}